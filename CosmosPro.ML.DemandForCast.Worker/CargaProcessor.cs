using System.Globalization;
using System.IO.Compression;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.Worker;

internal sealed class CargaProcessor(
    IMinioClient minio,
    IConfiguration config,
    IServiceProvider services,
    ILogger<CargaProcessor> logger)
{
    private const string BucketName = "imports";

    /// <summary>Ordem de DELETE: filhos primeiro (FKs apontam para Lojas/Produtos).</summary>
    private static readonly string[] DeleteOrder =
    [
        // Itens antes do cabeçalho da sugestão, e ambos antes de Produtos/Lojas.
        "SugestoesCompraItens",
        "SugestoesCompra",
        "Vendas",
        "EstoquesDiarios",
        "Compras",
        "Promocoes",
        "MercadoIqvia",
        "SinaisExternos",
        "Produtos",
        "Lojas",
    ];

    /// <summary>Ordem de INSERT: pais primeiro. Mapping = (nome do CSV no ZIP, tabela destino).</summary>
    private static readonly (string Csv, string Table)[] InsertOrder =
    [
        ("lojas.csv", "Lojas"),
        ("produtos.csv", "Produtos"),
        ("vendas.csv", "Vendas"),
        ("estoques_diarios.csv", "EstoquesDiarios"),
        ("compras.csv", "Compras"),
        ("promocoes.csv", "Promocoes"),
        ("mercado_iqvia.csv", "MercadoIqvia"),
        // Opcional (sem FK): ZIPs antigos podem não trazer — BulkInsert pula se ausente.
        ("sinais_externos.csv", "SinaisExternos"),
        // Opcionais: só quem extrai do PBS traz sugestão. Cabeçalho antes dos itens
        // (FK), e ambos depois de Produtos/Lojas (FKs compostas).
        ("sugestoes_compra.csv", "SugestoesCompra"),
        ("sugestoes_compra_itens.csv", "SugestoesCompraItens"),
    ];

    public async Task<long> ProcessAsync(CargaStage carga, Rede rede, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"carga-{carga.Id}");
        Directory.CreateDirectory(workDir);

        try
        {
            await DownloadAndExtractAsync(carga.BlobKey, workDir, ct);

            // Import avulso não tem sessão e continua sem conhecer manifesto nenhum: a
            // exigência da declaração da sugestão nasce da sessão, não do import.
            var sessao = await CarregarSessaoAsync(carga.Id, ct);
            var leitura = sessao is null ? null : ManifestoLeitor.Ler(workDir);

            var linhas = await LoadIntoStageAsync(workDir, rede, ct);

            if (sessao is not null)
            {
                await RegistrarNaSessaoAsync(sessao, leitura!, ct);
            }

            return linhas;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Falha ao limpar diretório temporário {Dir}.", workDir); }
        }
    }

    /// <summary>
    /// A sessão de comparação (F14) é quem aponta para a carga, não o contrário — logo a
    /// pergunta "esta carga pertence a uma sessão?" só se responde de <c>ComparacaoSessoes</c>.
    /// Devolve <c>null</c> para todo import avulso, que é a maioria.
    /// </summary>
    private async Task<SessaoDaCarga?> CarregarSessaoAsync(Guid cargaId, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        return await db.ComparacaoSessoes.AsNoTracking()
            .Where(s => s.CargaStageId == cargaId)
            .Select(s => new SessaoDaCarga(s.Id, s.Status))
            .FirstOrDefaultAsync(ct);
    }

    private sealed record SessaoDaCarga(Guid Id, SessaoStatus Status);

    /// <summary>
    /// Transcreve para a sessão o que o ZIP declarou — ou por que ele não sustenta
    /// comparação nenhuma.
    ///
    /// <para>
    /// Roda <b>depois</b> do import, e o import roda mesmo quando o envio é inviável: os
    /// CSVs já passaram pelo validador do upload e os dados são da própria rede, então
    /// carregá-los é honesto e deixa a carga com contagem de linhas verdadeira. Marcar a
    /// carga como concluída sem ter carregado nada seria pior — mentiria sobre o import
    /// para descrever um problema que é da sessão.
    /// </para>
    ///
    /// <para>
    /// <c>SugestaoDataHora</c> e <c>SugestaoTipoCalculo</c> não são retrato de tela: a fase
    /// seguinte deriva da primeira o corte anti-vazamento do treino (sem ele a comparação se
    /// recusa a rodar, ver <c>ComparacaoProcessor</c>) e da segunda contra qual dos dois
    /// métodos do ERP a disputa acontece.
    /// </para>
    /// </summary>
    private async Task RegistrarNaSessaoAsync(
        SessaoDaCarga sessao, LeituraDoManifesto leitura, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var agora = DateTimeOffset.UtcNow;

        if (leitura.MotivoInviabilidade is { } motivo)
        {
            // Inviável não é falha: nada quebrou, faltou pré-condição no que foi enviado, e
            // o remédio (gerar de novo pelo extrator) é outro. Ver ManifestoLeitor.
            if (!ComparacaoSessao.PodeTransicionar(sessao.Status, SessaoStatus.Inviavel))
            {
                logger.LogWarning(
                    "Sessão {SessaoId} está em {Status} e não pode ir para Inviavel; motivo descartado: {Motivo}",
                    sessao.Id, sessao.Status, motivo);
                return;
            }

            var marcadas = await db.ComparacaoSessoes
                .Where(s => s.Id == sessao.Id && s.Status == sessao.Status)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, SessaoStatus.Inviavel)
                    .SetProperty(x => x.MotivoInviabilidade, motivo)
                    .SetProperty(x => x.AtualizadoEm, agora), ct);

            if (marcadas == 0)
            {
                logger.LogWarning(
                    "Sessão {SessaoId} mudou de estado durante o import; inviabilidade não gravada.", sessao.Id);
                return;
            }

            logger.LogInformation("Sessão {SessaoId} marcada como inviável: {Motivo}", sessao.Id, motivo);
            return;
        }

        var manifesto = leitura.Manifesto!;

        // Mesmo WHERE otimista do ramo de inviabilidade: quando o SessaoWorker existir, ele
        // pode tirar a sessão de ProcessandoDados enquanto esta gravação está em voo, e sem o
        // guard uma leitura velha sobrescreveria a sugestão de uma sessão que já avançou.
        var vinculadas = await db.ComparacaoSessoes
            .Where(s => s.Id == sessao.Id && s.Status == sessao.Status)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SugestaoId, manifesto.SugestaoId)
                .SetProperty(x => x.SugestaoDescricao, manifesto.SugestaoDescricao)
                .SetProperty(x => x.SugestaoDataHora, manifesto.SugestaoDataHora)
                .SetProperty(x => x.SugestaoTipoCalculo, manifesto.SugestaoTipoCalculo)
                // Único ponto em que este número pode ser guardado: o manifesto vive no
                // diretório temporário que este processador apaga ao terminar, e quem precisa
                // dele é a materialização do resultado, três fases adiante.
                .SetProperty(x => x.SkusSemCadastro, manifesto.SkusSemCadastro)
                .SetProperty(x => x.AtualizadoEm, agora), ct);

        if (vinculadas == 0)
        {
            logger.LogWarning(
                "Sessão {SessaoId} mudou de estado durante o import; vínculo com a sugestão {SugestaoId} não gravado.",
                sessao.Id, manifesto.SugestaoId);
            return;
        }

        logger.LogInformation(
            "Sessão {SessaoId} vinculada à sugestão {SugestaoId} de {DataHora:dd/MM/yyyy} (método {TipoCalculo}), " +
            "extrator {Versao}, {SkusSemCadastro} SKU(s) sem cadastro.",
            sessao.Id, manifesto.SugestaoId, manifesto.SugestaoDataHora, manifesto.SugestaoTipoCalculo,
            manifesto.VersaoExtractor, manifesto.SkusSemCadastro);
    }

    private async Task DownloadAndExtractAsync(string blobKey, string workDir, CancellationToken ct)
    {
        var zipPath = Path.Combine(workDir, "import.zip");
        await using (var file = File.Create(zipPath))
        {
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(BucketName)
                .WithObject(blobKey)
                .WithCallbackStream((stream, token) => stream.CopyToAsync(file, token)),
                ct);
        }

        ZipFile.ExtractToDirectory(zipPath, workDir);
        File.Delete(zipPath);
    }

    private async Task<long> LoadIntoStageAsync(string workDir, Rede rede, CancellationToken ct)
    {
        var connStr = config.GetConnectionString("Stage")
            ?? throw new InvalidOperationException("Connection string 'Stage' não encontrada.");

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            // Projeta a rede no Stage antes de tudo: as FKs das tabelas de dado
            // apontam para dbo.Redes, que precisa ter a linha.
            await UpsertRedeAsync(rede, conn, tx, ct);

            // Limpa em ordem reversa de FK (filhos antes de pais), **só desta rede** —
            // sem o filtro, importar uma rede apagaria o Stage das outras.
            foreach (var table in DeleteOrder)
            {
                await using var cmd = new SqlCommand($"DELETE FROM dbo.{table} WHERE RedeId = @redeId;", conn, tx);
                cmd.Parameters.AddWithValue("@redeId", rede.Id);
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // BULK INSERT em ordem de FK (pais antes de filhos).
            long total = 0;
            foreach (var (csv, table) in InsertOrder)
            {
                var rows = await BulkInsertAsync(workDir, csv, table, rede.Id, conn, tx, ct);
                logger.LogInformation("BULK INSERT {Table} (rede {RedeId}): {Rows} linhas.", table, rede.Id, rows);
                total += rows;
            }

            await tx.CommitAsync(ct);
            return total;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Sincroniza a projeção da rede no Stage. UPDATE-então-INSERT em vez de
    /// IF NOT EXISTS: dentro da transação do import, evita a corrida do
    /// check-then-insert.
    /// </summary>
    private static async Task UpsertRedeAsync(
        Rede rede, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            UPDATE dbo.Redes SET Nome = @nome, Slug = @slug WHERE RedeId = @redeId;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.Redes (RedeId, Nome, Slug) VALUES (@redeId, @nome, @slug);
            """, conn, tx);
        cmd.Parameters.AddWithValue("@redeId", rede.Id);
        cmd.Parameters.AddWithValue("@nome", rede.Nome);
        cmd.Parameters.AddWithValue("@slug", rede.Slug);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> BulkInsertAsync(
        string workDir, string csvName, string table, int redeId,
        SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        var csvPath = Path.Combine(workDir, csvName);
        // Arquivos opcionais (ex.: sinais_externos.csv) podem não existir em ZIPs
        // antigos — pula sem erro.
        if (!File.Exists(csvPath))
        {
            logger.LogInformation("{Csv} ausente no ZIP — pulando (tabela {Table} fica vazia).", csvName, table);
            return 0;
        }
        var schema = TableSchemas.ByTable[table];
        var dataTable = TableSchemas.BuildEmpty(table);

        // Auto-detect separador olhando a primeira linha.
        var firstLine = await File.ReadAllLinesAsync(csvPath, ct);
        if (firstLine.Length == 0)
        {
            logger.LogWarning("{Csv} vazio, pulando.", csvName);
            return 0;
        }
        var delimiter = firstLine[0].Contains(';') ? ";" : ",";

        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true,
            PrepareHeaderForMatch = a => a.Header.Trim().Trim('"'),
            MissingFieldFound = null,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim,
        };

        await using var stream = File.OpenRead(csvPath);
        using var reader = new StreamReader(stream);
        using var csvReader = new CsvReader(reader, cfg);

        if (!await csvReader.ReadAsync())
        {
            return 0;
        }
        csvReader.ReadHeader();

        // Resolve índice de cada coluna do schema no header do CSV. CSVs podem
        // ter colunas extras (ignoradas) ou faltar opcionais (preenchidas com null).
        var headerIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < csvReader.HeaderRecord!.Length; i++)
        {
            headerIdx[csvReader.HeaderRecord[i].Trim().Trim('"')] = i;
        }

        var rowsRead = 0;
        while (await csvReader.ReadAsync())
        {
            var row = dataTable.NewRow();
            foreach (var col in schema)
            {
                if (col.ServerSupplied)
                {
                    row[col.Name] = redeId;
                    continue;
                }
                if (!headerIdx.TryGetValue(col.Name, out var idx))
                {
                    row[col.Name] = col.Nullable
                        ? DBNull.Value
                        : throw new FormatException($"{csvName}: coluna obrigatória '{col.Name}' ausente.");
                    continue;
                }
                var raw = csvReader.GetField(idx) ?? string.Empty;
                row[col.Name] = TableSchemas.Parse(col, raw);
            }
            dataTable.Rows.Add(row);
            rowsRead++;
        }

        if (rowsRead == 0) return 0;

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = $"dbo.{table}",
            BatchSize = 10_000,
            BulkCopyTimeout = 600,
        };
        foreach (var col in schema)
        {
            bulk.ColumnMappings.Add(col.Name, col.Name);
        }
        await bulk.WriteToServerAsync(dataTable, ct);
        return bulk.RowsCopied;
    }
}
