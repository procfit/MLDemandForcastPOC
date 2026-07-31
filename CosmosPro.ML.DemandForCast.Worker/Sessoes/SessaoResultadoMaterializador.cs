using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Worker.Comparison;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker.Sessoes;

/// <summary>
/// Grava o resultado da sessão enquanto o Stage ainda existe: uma linha por item em
/// <c>engine.ComparacaoSessaoItens</c> e os agregados da manchete em
/// <c>ComparacaoSessao.ResultadoJson</c>.
///
/// <para>
/// <b>Por que agora e não na abertura da tela.</b> Cada import faz
/// <c>DELETE ... WHERE RedeId</c> no Stage (ver <see cref="CargaProcessor"/>), então o
/// próximo ZIP da rede apaga a sugestão, as vendas e o cadastro que este resultado
/// descreve. Uma sessão que só guardasse ponteiros viraria tela vazia — ou, pior, tela
/// preenchida com os dados do envio seguinte. É o motivo de <c>ComparacaoSessaoItens</c>
/// existir como tabela, e o motivo de a materialização acontecer no fim da fase de
/// comparação.
/// </para>
///
/// <para>
/// <b>A população é a do ERP, lida do job que rodou.</b> Rede, janela e método saem da
/// linha de <see cref="ComparacaoPbs"/> — não são rederivados da sessão —, para que o que
/// se materializa seja exatamente o que a comparação mediu. Alargar seria pior que inútil:
/// item que o ERP não avaliou não tem contra o que ser comparado.
/// </para>
/// </summary>
internal sealed class SessaoResultadoMaterializador(
    IConfiguration config,
    IServiceProvider services,
    ILogger<SessaoResultadoMaterializador> logger)
{
    /// <summary>
    /// Enums saem como texto, mesma razão do <see cref="ComparacaoProcessor"/>:
    /// <c>UtilidadeDecisaoMl</c> é o campo que a tela consulta para decidir entre explicar
    /// e mostrar números, e um inteiro deixaria isso dependendo de quem lê saber a ordem do
    /// enum.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Materializa e conclui a sessão numa transação só. Devolve <c>false</c> quando a
    /// sessão já não estava mais na fase reclamada — ninguém foi materializado duas vezes.
    /// </summary>
    public async Task<bool> MaterializarAsync(SessaoEmAndamento sessao, CancellationToken ct)
    {
        if (sessao.ComparacaoPbsId is not { } comparacaoId)
        {
            throw new InvalidOperationException(
                $"Sessão {sessao.Id} chegou ao fim da comparação sem job de comparação registrado.");
        }

        var job = await CarregarComparacaoAsync(sessao, comparacaoId, ct);
        var comparacao = JsonSerializer.Deserialize<ComparacaoOutput>(job.ResultadoJson, Json)
            ?? throw new InvalidOperationException(
                $"O resultado da comparação {comparacaoId} não pôde ser lido.");

        var stageConnStr = config.GetConnectionString("Stage")
            ?? throw new InvalidOperationException("Connection string 'Stage' não encontrada.");

        var sugestoes = await new StageSugestaoLoader(stageConnStr, logger)
            .LoadAsync(sessao.RedeId, job.JanelaInicio, job.JanelaFim, job.TipoCalculo, ct);

        var populacao = await LerPopulacaoAsync(sessao.RedeId, job, sugestoes, stageConnStr, ct);

        var materializacao = SessaoResultadoMontador.Montar(
            sessaoId: sessao.Id,
            skusSemCadastro: sessao.SkusSemCadastro,
            comparacaoPbsId: comparacaoId,
            sugestaoDataHora: sessao.SugestaoDataHora,
            comparacao: comparacao,
            populacao: populacao,
            agora: DateTimeOffset.UtcNow);

        var gravou = await GravarAsync(sessao, materializacao, ct);
        if (!gravou) return false;

        logger.LogInformation(
            "Sessão {SessaoId}: {Itens} item(ns) materializado(s); {ComDecisao} com decisão do ML, " +
            "{ComPrevisao} com previsão do ML (camada B está {Utilidade}).",
            sessao.Id, materializacao.Itens.Count, materializacao.Resultado.ItensComDecisaoMl,
            materializacao.Resultado.ItensComPrevisaoMl, materializacao.Resultado.UtilidadeDecisaoMl);

        return true;
    }

    /// <summary>
    /// O job de comparação da sessão, com a rede conferida.
    ///
    /// <para>
    /// A checagem de rede repete a que o <see cref="ComparacaoProcessor"/> faz sobre o
    /// treino, e pelo mesmo motivo: um id trocado faria esta materialização copiar para a
    /// tela de um inquilino os itens medidos no dado comercial de outro. O <c>SessaoWorker</c>
    /// só grava ids que ele mesmo criou, então a checagem nunca deveria disparar — é
    /// exatamente por isso que ela é barata.
    /// </para>
    /// </summary>
    private async Task<ComparacaoDoJob> CarregarComparacaoAsync(
        SessaoEmAndamento sessao, Guid comparacaoId, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        var job = await db.ComparacoesPbs.AsNoTracking()
            .Where(c => c.Id == comparacaoId)
            .Select(c => new
            {
                c.RedeId,
                c.Status,
                c.ResultadoJson,
                c.JanelaInicio,
                c.JanelaFim,
                c.TipoCalculo,
            })
            .FirstOrDefaultAsync(ct);

        if (job is null)
        {
            throw new InvalidOperationException($"A comparação {comparacaoId} não existe mais.");
        }

        if (job.RedeId != sessao.RedeId)
        {
            throw new InvalidOperationException(
                $"A comparação {comparacaoId} é da rede {job.RedeId} e a sessão é da rede {sessao.RedeId}.");
        }

        if (job.Status != ComparacaoPbsStatus.Concluido || string.IsNullOrEmpty(job.ResultadoJson))
        {
            throw new InvalidOperationException(
                $"A comparação {comparacaoId} não deixou resultado para materializar (situação: {job.Status}).");
        }

        return new ComparacaoDoJob(
            job.ResultadoJson, job.JanelaInicio, job.JanelaFim, job.TipoCalculo);
    }

    private sealed record ComparacaoDoJob(
        string ResultadoJson, DateOnly JanelaInicio, DateOnly JanelaFim, byte TipoCalculo);

    /// <summary>
    /// Junta à população do ERP o que o Stage sabe do desfecho dela: nome do produto, venda
    /// da cobertura, dias sem estoque e se a cobertura passa do fim do histórico importado.
    /// </summary>
    private async Task<List<ItemDoStage>> LerPopulacaoAsync(
        int redeId,
        ComparacaoDoJob job,
        IReadOnlyList<SugestaoStage> sugestoes,
        string connStr,
        CancellationToken ct)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        var inicio = job.JanelaInicio.ToDateTime(TimeOnly.MinValue);
        var fim = job.JanelaFim.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var vendas = await LerVendasDaJanelaAsync(conn, redeId, inicio, fim, job.TipoCalculo, ct);
        var estoques = await LerRupturaDaJanelaAsync(conn, redeId, inicio, fim, job.TipoCalculo, ct);
        var fimDoHistorico = await LerFimDoHistoricoAsync(conn, redeId, ct);

        var populacao = new List<ItemDoStage>();
        foreach (var sugestao in sugestoes)
        {
            var corte = DateOnly.FromDateTime(sugestao.DataHora);

            foreach (var item in sugestao.Itens)
            {
                var chave = (item.LojaId, item.Sku);
                vendas.TryGetValue(chave, out var venda);
                estoques.TryGetValue(chave, out var estoque);

                populacao.Add(new ItemDoStage(
                    Item: item,
                    NomeProduto: venda.Nome,
                    VendidoNaJanela: venda.Unidades,
                    DiasSemEstoque: estoque.DiasSemEstoque,
                    DiasComSnapshot: estoque.DiasComSnapshot,
                    JanelaAlemDoHistorico: item.DiasEstoque >= 1
                        && (fimDoHistorico is null || corte.AddDays(item.DiasEstoque - 1) > fimDoHistorico)));
            }
        }

        return populacao;
    }

    private readonly record struct VendaDoItem(string? Nome, decimal Unidades);

    /// <summary>
    /// Venda de cada item na própria cobertura dele, agregada no servidor.
    ///
    /// <para>
    /// A janela sai do <c>DiasEstoque</c> da linha e da <c>DataHora</c> do cabeçalho, e não
    /// de um parâmetro: cada item da mesma sugestão pode cobrir um número diferente de dias,
    /// e uma janela única pontuaria compras dimensionadas para prazos diferentes contra o
    /// mesmo período. É a mesma cobertura que a camada B usa.
    /// </para>
    ///
    /// <para>
    /// <c>LEFT JOIN</c> de propósito: item sem nenhuma venda na cobertura precisa aparecer
    /// com zero — ele é o caso mais interessante da tela (compra que não vendeu nada) e
    /// desaparecer da tabela seria o pior desfecho possível. <c>RedeId</c> entra nos dois
    /// lados do join pela mesma razão da FK composta no Stage: <c>Sku</c> e <c>LojaId</c> são
    /// códigos de ERP e colidem entre redes.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<(int LojaId, string Sku), VendaDoItem>> LerVendasDaJanelaAsync(
        SqlConnection conn, int redeId, DateTime inicio, DateTime fim, byte tipoCalculo, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.LojaId, i.Sku, p.Nome, ISNULL(SUM(v.Quantidade), 0) AS Vendido
            FROM dbo.SugestoesCompraItens i
            INNER JOIN dbo.SugestoesCompra s
                ON s.RedeId = i.RedeId AND s.SugestaoId = i.SugestaoId
            INNER JOIN dbo.Produtos p
                ON p.RedeId = i.RedeId AND p.Sku = i.Sku
            LEFT JOIN dbo.Vendas v
                ON v.RedeId = i.RedeId AND v.LojaId = i.LojaId AND v.Sku = i.Sku
                AND v.Data >= CAST(s.DataHora AS date)
                AND v.Data < DATEADD(day, i.DiasEstoque, CAST(s.DataHora AS date))
            WHERE i.RedeId = @redeId AND s.TipoCalculo = @tipo
              AND s.DataHora >= @inicio AND s.DataHora < @fim
            GROUP BY i.LojaId, i.Sku, p.Nome
            """;
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.Parameters.AddWithValue("@tipo", tipoCalculo);
        cmd.Parameters.AddWithValue("@inicio", inicio);
        cmd.Parameters.AddWithValue("@fim", fim);
        cmd.CommandTimeout = 600;

        var resultado = new Dictionary<(int, string), VendaDoItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            resultado[(r.GetInt32(0), r.GetString(1))] = new VendaDoItem(
                r.IsDBNull(2) ? null : r.GetString(2), r.GetDecimal(3));
        }
        return resultado;
    }

    private readonly record struct EstoqueDoItem(int DiasSemEstoque, int DiasComSnapshot);

    /// <summary>
    /// Dias sem estoque na cobertura de cada item, e quantos dias dela têm snapshot.
    ///
    /// <para>
    /// Os dois números andam juntos porque <c>EstoquesDiarios</c> pode ser esparso: zero dia
    /// zerado em uma cobertura sem nenhum snapshot não é "não houve falta", é "não sabemos".
    /// Reportar só o primeiro faria a manchete afirmar ausência de ruptura por ausência de
    /// dado.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<(int LojaId, string Sku), EstoqueDoItem>> LerRupturaDaJanelaAsync(
        SqlConnection conn, int redeId, DateTime inicio, DateTime fim, byte tipoCalculo, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.LojaId, i.Sku,
                   SUM(CASE WHEN e.QuantidadeEmEstoque <= 0 THEN 1 ELSE 0 END) AS DiasSemEstoque,
                   COUNT(e.Data) AS DiasComSnapshot
            FROM dbo.SugestoesCompraItens i
            INNER JOIN dbo.SugestoesCompra s
                ON s.RedeId = i.RedeId AND s.SugestaoId = i.SugestaoId
            LEFT JOIN dbo.EstoquesDiarios e
                ON e.RedeId = i.RedeId AND e.LojaId = i.LojaId AND e.Sku = i.Sku
                AND e.Data >= CAST(s.DataHora AS date)
                AND e.Data < DATEADD(day, i.DiasEstoque, CAST(s.DataHora AS date))
            WHERE i.RedeId = @redeId AND s.TipoCalculo = @tipo
              AND s.DataHora >= @inicio AND s.DataHora < @fim
            GROUP BY i.LojaId, i.Sku
            """;
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.Parameters.AddWithValue("@tipo", tipoCalculo);
        cmd.Parameters.AddWithValue("@inicio", inicio);
        cmd.Parameters.AddWithValue("@fim", fim);
        cmd.CommandTimeout = 600;

        var resultado = new Dictionary<(int, string), EstoqueDoItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            resultado[(r.GetInt32(0), r.GetString(1))] = new EstoqueDoItem(
                r.IsDBNull(2) ? 0 : r.GetInt32(2), r.GetInt32(3));
        }
        return resultado;
    }

    /// <summary>
    /// Último dia de venda importado nesta rede. É o que permite dizer que a venda de uma
    /// cobertura está subcontada por fim de histórico, em vez de a sobra sair inflada sem
    /// explicação.
    /// </summary>
    private static async Task<DateOnly?> LerFimDoHistoricoAsync(
        SqlConnection conn, int redeId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(Data) FROM dbo.Vendas WHERE RedeId = @redeId";
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.CommandTimeout = 300;

        var valor = await cmd.ExecuteScalarAsync(ct);
        return valor is DateTime data ? DateOnly.FromDateTime(data) : null;
    }

    /// <summary>
    /// Apaga o detalhe anterior, grava o novo e conclui a sessão — <b>uma transação, nesta
    /// ordem</b>.
    ///
    /// <para>
    /// <b>É daqui que sai a garantia de não materializar duas vezes.</b> O claim do
    /// <see cref="SessaoWorker"/> usa <c>UPDLOCK/READPAST</c>, mas o lock morre com a
    /// consulta: dois processos podem reclamar a mesma sessão e chegar os dois aqui. O
    /// <c>UPDATE</c> final leva o mesmo <c>WHERE ... AND Status = &lt;fase reclamada&gt;</c>
    /// das gravações irmãs, então só o primeiro encontra linha; o segundo devolve zero, a
    /// transação inteira volta atrás e as linhas que ele inseriu desaparecem com ela.
    /// Separar a gravação das linhas da conclusão da sessão em duas transações reabriria
    /// exatamente esse buraco — o detalhe em dobro sem nada na sessão indicando isso.
    /// </para>
    ///
    /// <para>
    /// O <c>DELETE</c> na frente cobre o outro lado: qualquer materialização que já tenha
    /// deixado linhas para esta sessão é substituída, não somada. Sem ele, uma segunda
    /// gravação legítima duplicaria as linhas que a PK deixasse passar — ou estouraria na
    /// PK, encerrando em <c>Falha</c> uma sessão cuja comparação foi bem-sucedida.
    /// </para>
    /// </summary>
    private async Task<bool> GravarAsync(
        SessaoEmAndamento sessao, Materializacao materializacao, CancellationToken ct)
    {
        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await using (var apagar = new SqlCommand(
                "DELETE FROM dbo.ComparacaoSessaoItens WHERE SessaoId = @sessaoId;", conn, tx))
            {
                apagar.Parameters.AddWithValue("@sessaoId", sessao.Id);
                apagar.CommandTimeout = 300;
                await apagar.ExecuteNonQueryAsync(ct);
            }

            if (materializacao.Itens.Count > 0)
            {
                using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                {
                    DestinationTableName = "dbo.ComparacaoSessaoItens",
                    BatchSize = 10_000,
                    BulkCopyTimeout = 600,
                };

                var tabela = MontarTabela(materializacao.Itens);
                foreach (DataColumn coluna in tabela.Columns)
                {
                    bulk.ColumnMappings.Add(coluna.ColumnName, coluna.ColumnName);
                }
                await bulk.WriteToServerAsync(tabela, ct);
            }

            int linhas;
            await using (var concluir = new SqlCommand("""
                UPDATE dbo.ComparacaoSessoes
                    SET Status = 'Concluida',
                        ResultadoJson = @resultado,
                        AtualizadoEm = SYSDATETIMEOFFSET()
                WHERE Id = @sessaoId AND Status = @statusEsperado;
                """, conn, tx))
            {
                concluir.Parameters.AddWithValue("@sessaoId", sessao.Id);
                concluir.Parameters.AddWithValue("@statusEsperado", sessao.Status.ToString());
                concluir.Parameters.AddWithValue(
                    "@resultado", JsonSerializer.Serialize(materializacao.Resultado, Json));
                concluir.CommandTimeout = 300;
                linhas = await concluir.ExecuteNonQueryAsync(ct);
            }

            if (linhas == 0)
            {
                await tx.RollbackAsync(CancellationToken.None);
                logger.LogWarning(
                    "Sessão {SessaoId} não estava mais em {Status}; materialização descartada.",
                    sessao.Id, sessao.Status);
                return false;
            }

            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// <c>DataTable</c> do <c>SqlBulkCopy</c>. As colunas do braço de ML são anuláveis e
    /// recebem <c>DBNull</c> quando ausentes: o default do <c>DataTable</c> para decimal é
    /// zero, e deixá-lo passar gravaria "o ML mandaria comprar nada" onde ninguém calculou
    /// nada.
    /// </summary>
    private static DataTable MontarTabela(IReadOnlyList<ComparacaoSessaoItem> itens)
    {
        var tabela = new DataTable();
        tabela.Columns.Add("SessaoId", typeof(Guid));
        tabela.Columns.Add("LojaId", typeof(int));
        tabela.Columns.Add("Sku", typeof(string));
        tabela.Columns.Add("NomeProduto", typeof(string));
        tabela.Columns.Add("Curva", typeof(string));
        tabela.Columns.Add("CompraSugeridaPbs", typeof(decimal));
        tabela.Columns.Add("CompraSugeridaMl", typeof(decimal));
        tabela.Columns.Add("VendidoNaJanela", typeof(decimal));
        tabela.Columns.Add("DemandaDiaPbs", typeof(decimal));
        tabela.Columns.Add("DemandaDiaMl", typeof(decimal));
        tabela.Columns.Add("DemandaDiaReal", typeof(decimal));
        tabela.Columns.Add("SobraPbsUnidades", typeof(decimal));
        tabela.Columns.Add("SobraMlUnidades", typeof(decimal));
        tabela.Columns.Add("SobraPbsValor", typeof(decimal));
        tabela.Columns.Add("SobraMlValor", typeof(decimal));

        foreach (var item in itens)
        {
            tabela.Rows.Add(
                item.SessaoId,
                item.LojaId,
                item.Sku,
                item.NomeProduto ?? (object)DBNull.Value,
                item.Curva ?? (object)DBNull.Value,
                item.CompraSugeridaPbs,
                item.CompraSugeridaMl ?? (object)DBNull.Value,
                item.VendidoNaJanela,
                item.DemandaDiaPbs,
                item.DemandaDiaMl ?? (object)DBNull.Value,
                item.DemandaDiaReal ?? (object)DBNull.Value,
                item.SobraPbsUnidades,
                item.SobraMlUnidades ?? (object)DBNull.Value,
                item.SobraPbsValor,
                item.SobraMlValor ?? (object)DBNull.Value);
        }

        return tabela;
    }
}
