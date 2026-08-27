using System.Data;
using System.Text.Json;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.Data.SqlClient;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

/// <summary>
/// Baixa o XLSX da IQVIA do MinIO, converte em forma longa e grava nas tabelas de
/// mercado do banco engine, numa transação só.
///
/// <para>
/// <b>Recarga substitui por (mês, brick)</b>: o DELETE apaga só os recortes que o
/// arquivo cobre, porque a série mensal se acumula empilhando N arquivos — cada um
/// traz o mês e o espelho do ano anterior. Apagar por rede, como o Stage faz,
/// destruiria a série; apagar nada duplicaria PK no reenvio.
/// </para>
///
/// <para>
/// Tudo em <see cref="SqlConnection"/> própria, fora do EF: bulk insert é off-EF, e a
/// transação de usuário não convive com o EnableRetryOnFailure do
/// AddSqlServerDbContext (armadilha registrada no CLAUDE.md).
/// </para>
/// </summary>
internal sealed class MercadoProcessor(
    IMinioClient minio,
    IConfiguration config,
    ILogger<MercadoProcessor> logger)
{
    private const string BucketName = "mercado";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(long Linhas, string ResumoJson)> ProcessAsync(MercadoCarga carga, CancellationToken ct)
    {
        IqviaParseResult resultado;
        using (var xlsx = new MemoryStream())
        {
            // Em memória, não em disco: o XLSX real tem ~10 MB comprimido — nada
            // perto dos ZIPs de venda que o CargaProcessor materializa em temp.
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(BucketName)
                .WithObject(carga.BlobKey)
                .WithCallbackStream((stream, token) => stream.CopyToAsync(xlsx, token)),
                ct);
            xlsx.Position = 0;
            resultado = IqviaXlsxParser.Parse(xlsx);
        }

        logger.LogInformation(
            "XLSX da carga {Id} (rede {RedeId}): {Obs} observações, {Prod} produtos, {Pdv} PDVs; " +
            "meses [{Meses}], {Bricks} brick(s), {SemEan} linha(s) sem EAN.",
            carga.Id, carga.RedeId, resultado.Observacoes.Count, resultado.Produtos.Count,
            resultado.Pdvs.Count, string.Join(", ", resultado.Resumo.Meses.Select(m => m.ToString("yyyy-MM"))),
            resultado.Resumo.Bricks.Count, resultado.Resumo.LinhasSemEan);

        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await SubstituirObservacoesAsync(carga.RedeId, resultado, conn, tx, ct);
            await UpsertProdutosAsync(carga.RedeId, resultado.Produtos, conn, tx, ct);
            await SubstituirPdvsAsync(carga.RedeId, resultado, conn, tx, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        return (resultado.Observacoes.Count, JsonSerializer.Serialize(resultado.Resumo, Json));
    }

    private static async Task SubstituirObservacoesAsync(
        int redeId, IqviaParseResult resultado, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        // Um DELETE por (mês, brick) coberto — poucos pares por arquivo (2 meses × ~3 bricks).
        var recortes = resultado.Observacoes.Select(o => (o.Mes, o.Brick)).Distinct();
        foreach (var (mes, brick) in recortes)
        {
            await using var del = new SqlCommand(
                "DELETE FROM dbo.MercadoObservacoes WHERE RedeId = @redeId AND Mes = @mes AND Brick = @brick;",
                conn, tx);
            del.Parameters.AddWithValue("@redeId", redeId);
            del.Parameters.AddWithValue("@mes", mes.ToDateTime(TimeOnly.MinValue));
            del.Parameters.AddWithValue("@brick", brick);
            del.CommandTimeout = 120;
            await del.ExecuteNonQueryAsync(ct);
        }

        var dt = new DataTable();
        dt.Columns.Add("RedeId", typeof(int));
        dt.Columns.Add("Mes", typeof(DateTime));
        dt.Columns.Add("Brick", typeof(string));
        dt.Columns.Add("Bandeira", typeof(string));
        dt.Columns.Add("Ean", typeof(string));
        dt.Columns.Add("Unidades", typeof(decimal));
        dt.Columns.Add("ValorCpp", typeof(decimal));
        foreach (var o in resultado.Observacoes)
        {
            dt.Rows.Add(redeId, o.Mes.ToDateTime(TimeOnly.MinValue), o.Brick, o.Bandeira, o.Ean, o.Unidades, o.ValorCpp);
        }

        await BulkAsync(dt, "dbo.MercadoObservacoes", conn, tx, ct);
    }

    /// <summary>
    /// Upsert por (RedeId, Ean) via tabela temporária: DELETE dos EANs que o arquivo
    /// traz + INSERT de todos. Produtos de arquivos anteriores que este envio não cita
    /// ficam intactos — observações antigas ainda apontam para eles.
    /// </summary>
    private static async Task UpsertProdutosAsync(
        int redeId, IReadOnlyList<IqviaProduto> produtos, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        if (produtos.Count == 0) return;

        await using (var create = new SqlCommand("""
            CREATE TABLE #MercadoProdutos (
                Ean            VARCHAR(14)   NOT NULL PRIMARY KEY,
                DescricaoLonga NVARCHAR(300) NOT NULL,
                Laboratorio    NVARCHAR(120) NULL,
                Molecula       NVARCHAR(500) NULL,
                AreaFarmacia   NVARCHAR(40)  NULL,
                Nec1           NVARCHAR(80)  NULL,
                Forma3         NVARCHAR(80)  NULL,
                Classe4        NVARCHAR(80)  NULL);
            """, conn, tx))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        var dt = new DataTable();
        dt.Columns.Add("Ean", typeof(string));
        dt.Columns.Add("DescricaoLonga", typeof(string));
        dt.Columns.Add("Laboratorio", typeof(string));
        dt.Columns.Add("Molecula", typeof(string));
        dt.Columns.Add("AreaFarmacia", typeof(string));
        dt.Columns.Add("Nec1", typeof(string));
        dt.Columns.Add("Forma3", typeof(string));
        dt.Columns.Add("Classe4", typeof(string));
        foreach (var p in produtos)
        {
            dt.Rows.Add(p.Ean, Truncar(p.DescricaoLonga, 300), Truncar(p.Laboratorio, 120), Truncar(p.Molecula, 500),
                Truncar(p.AreaFarmacia, 40), Truncar(p.Nec1, 80), Truncar(p.Forma3, 80), Truncar(p.Classe4, 80));
        }
        await BulkAsync(dt, "#MercadoProdutos", conn, tx, ct);

        await using var merge = new SqlCommand("""
            DELETE p FROM dbo.MercadoProdutos p
            JOIN #MercadoProdutos t ON t.Ean = p.Ean
            WHERE p.RedeId = @redeId;

            INSERT INTO dbo.MercadoProdutos
                (RedeId, Ean, DescricaoLonga, Laboratorio, Molecula, AreaFarmacia, Nec1, Forma3, Classe4)
            SELECT @redeId, Ean, DescricaoLonga, Laboratorio, Molecula, AreaFarmacia, Nec1, Forma3, Classe4
            FROM #MercadoProdutos;

            DROP TABLE #MercadoProdutos;
            """, conn, tx);
        merge.Parameters.AddWithValue("@redeId", redeId);
        merge.CommandTimeout = 300;
        await merge.ExecuteNonQueryAsync(ct);
    }

    private static async Task SubstituirPdvsAsync(
        int redeId, IqviaParseResult resultado, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        if (resultado.Pdvs.Count == 0) return;

        foreach (var brick in resultado.Pdvs.Select(p => p.Brick).Distinct())
        {
            await using var del = new SqlCommand(
                "DELETE FROM dbo.MercadoBrickPdvs WHERE RedeId = @redeId AND Brick = @brick;", conn, tx);
            del.Parameters.AddWithValue("@redeId", redeId);
            del.Parameters.AddWithValue("@brick", brick);
            await del.ExecuteNonQueryAsync(ct);
        }

        var dt = new DataTable();
        dt.Columns.Add("RedeId", typeof(int));
        dt.Columns.Add("Brick", typeof(string));
        dt.Columns.Add("Cnpj", typeof(string));
        dt.Columns.Add("Bandeira", typeof(string));
        foreach (var p in resultado.Pdvs)
        {
            dt.Rows.Add(redeId, p.Brick, p.Cnpj, Truncar(p.Bandeira, 60));
        }
        await BulkAsync(dt, "dbo.MercadoBrickPdvs", conn, tx, ct);
    }

    private static async Task BulkAsync(
        DataTable dt, string destino, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        if (dt.Rows.Count == 0) return;
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = destino,
            BatchSize = 10_000,
            BulkCopyTimeout = 600,
        };
        foreach (DataColumn col in dt.Columns)
        {
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static string? Truncar(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];
}
