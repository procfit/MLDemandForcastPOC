using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Worker.Comparison;

/// <summary>
/// Lê do Stage as sugestões de compra do ERP (PBS) que entram numa execução do
/// comparativo F13: cabeçalhos de <c>dbo.SugestoesCompra</c> e as respectivas linhas
/// de <c>dbo.SugestoesCompraItens</c>.
///
/// <para>
/// <b>Escopo por rede</b>, na mesma regra do <see cref="Training.StageObservationLoader"/>:
/// toda consulta filtra <c>RedeId</c>, inclusive a dos itens — que também o carrega na
/// própria PK. Sem o filtro nos dois lados, um <c>SugestaoId</c> repetido entre
/// inquilinos (é código de ERP, colide) traria linha de outra rede.
/// </para>
///
/// <para>
/// <b>A janela filtra o cabeçalho, não a venda.</b> O recorte é sobre
/// <c>SugestoesCompra.DataHora</c>, e é ele que determina os dias de venda pontuados:
/// cada item é medido sobre a cobertura que começa na própria <c>DataHora</c> da
/// sugestão. Filtrar diretamente por dia de venda escolheria dias sem escolher
/// decisões, e a comparação é entre decisões.
/// </para>
///
/// <para>
/// <b>Um <c>TipoCalculo</c> por execução.</b> "Emax e Eseg" (1) e "Dias de Reposição"
/// (2) são baselines distintos do ERP; os comparadores recusam população que misture
/// os dois, então o filtro vive aqui e não depois.
/// </para>
/// </summary>
internal sealed class StageSugestaoLoader(string connectionString, ILogger logger)
{
    public async Task<IReadOnlyList<SugestaoStage>> LoadAsync(
        int redeId, DateOnly janelaInicio, DateOnly janelaFim, byte tipoCalculo, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var inicio = janelaInicio.ToDateTime(TimeOnly.MinValue);
        // Fim exclusivo no dia seguinte: DataHora é DATETIME2 e a sugestão calculada às
        // 09:00 do último dia da janela precisa entrar. Comparar com janelaFim direto
        // cortaria todas as sugestões do próprio dia final, menos as da meia-noite.
        var fim = janelaFim.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var cabecalhos = await LoadCabecalhosAsync(conn, redeId, inicio, fim, tipoCalculo, ct);
        if (cabecalhos.Count == 0)
        {
            logger.LogWarning(
                "Rede {RedeId} não tem sugestão do PBS com TipoCalculo {Tipo} entre {Inicio} e {Fim}.",
                redeId, tipoCalculo, janelaInicio, janelaFim);
            return [];
        }

        var itens = await LoadItensAsync(conn, redeId, inicio, fim, tipoCalculo, ct);

        var resultado = cabecalhos
            .Select(c => new SugestaoStage(
                c.SugestaoId,
                c.DataHora,
                c.TipoCalculo,
                c.ConsideraPedidosPendentes,
                itens.TryGetValue(c.SugestaoId, out var lista) ? lista : []))
            .ToList();

        logger.LogInformation(
            "{Sugestoes} sugestão(ões) do PBS (TipoCalculo {Tipo}) com {Itens} item(ns) na rede {RedeId}.",
            resultado.Count, tipoCalculo, resultado.Sum(s => s.Itens.Count), redeId);

        return resultado;
    }

    private static async Task<List<Cabecalho>> LoadCabecalhosAsync(
        SqlConnection conn, int redeId, DateTime inicio, DateTime fim, byte tipoCalculo, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SugestaoId, DataHora, TipoCalculo, ConsideraPedidosPendentes
            FROM dbo.SugestoesCompra
            WHERE RedeId = @redeId AND TipoCalculo = @tipo
              AND DataHora >= @inicio AND DataHora < @fim
            ORDER BY DataHora, SugestaoId
            """;
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.Parameters.AddWithValue("@tipo", tipoCalculo);
        cmd.Parameters.AddWithValue("@inicio", inicio);
        cmd.Parameters.AddWithValue("@fim", fim);
        cmd.CommandTimeout = 300;

        var resultado = new List<Cabecalho>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            resultado.Add(new Cabecalho(r.GetInt64(0), r.GetDateTime(1), r.GetByte(2), r.GetBoolean(3)));
        }
        return resultado;
    }

    /// <remarks>
    /// O join repete o predicado da janela em vez de mandar os ids num <c>IN</c>: uma
    /// sugestão real do PBS traz dezenas de milhares de linhas e a lista de ids não
    /// escala, mas o índice <c>IX_SugestoesCompra_Tipo_Data</c> cobre exatamente este
    /// predicado. <c>RedeId</c> entra nos dois lados do join pela mesma razão que
    /// existe na FK composta: <c>SugestaoId</c> é código de ERP e colide entre redes.
    /// </remarks>
    private static async Task<Dictionary<long, List<SugestaoItemStage>>> LoadItensAsync(
        SqlConnection conn, int redeId, DateTime inicio, DateTime fim, byte tipoCalculo, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.SugestaoId, i.LojaId, i.Sku, i.Curva, i.DemandaDia, i.EstoqueSaldo,
                   i.EstoqueSeguranca, i.EstoqueMaximo, i.DiasEstoque, i.PedidosPendentes,
                   i.CompraSugerida, i.CompraAutorizada, i.PrecoCompra, i.FatorEmbalagem, i.Falteiro
            FROM dbo.SugestoesCompraItens i
            INNER JOIN dbo.SugestoesCompra s
                ON s.RedeId = i.RedeId AND s.SugestaoId = i.SugestaoId
            WHERE i.RedeId = @redeId AND s.TipoCalculo = @tipo
              AND s.DataHora >= @inicio AND s.DataHora < @fim
            ORDER BY i.SugestaoId, i.LojaId, i.Sku
            """;
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.Parameters.AddWithValue("@tipo", tipoCalculo);
        cmd.Parameters.AddWithValue("@inicio", inicio);
        cmd.Parameters.AddWithValue("@fim", fim);
        cmd.CommandTimeout = 600;

        var porSugestao = new Dictionary<long, List<SugestaoItemStage>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sugestaoId = r.GetInt64(0);
            if (!porSugestao.TryGetValue(sugestaoId, out var lista))
            {
                lista = [];
                porSugestao[sugestaoId] = lista;
            }

            lista.Add(new SugestaoItemStage(
                sugestaoId,
                r.GetInt32(1),
                r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3).Trim(),
                r.GetDecimal(4),
                r.GetDecimal(5),
                r.IsDBNull(6) ? null : r.GetDecimal(6),
                r.IsDBNull(7) ? null : r.GetDecimal(7),
                r.GetInt16(8),
                r.GetDecimal(9),
                r.GetDecimal(10),
                r.GetDecimal(11),
                r.IsDBNull(12) ? null : r.GetDecimal(12),
                r.IsDBNull(13) ? null : r.GetDecimal(13),
                r.GetBoolean(14)));
        }
        return porSugestao;
    }

    private readonly record struct Cabecalho(
        long SugestaoId, DateTime DataHora, byte TipoCalculo, bool ConsideraPedidosPendentes);
}

/// <summary>
/// Cabeçalho de <c>dbo.SugestoesCompra</c> com os itens que ele avaliou. Só os campos
/// que as três camadas de comparação consomem — os parâmetros de curva
/// (<c>DiasCurvaA..E</c>, <c>Efetividade</c>) descrevem como o ERP chegou aos números
/// já gravados nos itens e não entram em nenhuma conta daqui.
/// </summary>
internal sealed record SugestaoStage(
    long SugestaoId,
    DateTime DataHora,
    byte TipoCalculo,
    bool ConsideraPedidosPendentes,
    IReadOnlyList<SugestaoItemStage> Itens);

/// <summary>
/// Linha de <c>dbo.SugestoesCompraItens</c>. É a população: o trio
/// (<c>SugestaoId</c>, <c>LojaId</c>, <c>Sku</c>) que o ERP de fato avaliou.
/// </summary>
internal sealed record SugestaoItemStage(
    long SugestaoId,
    int LojaId,
    string Sku,
    string Curva,
    decimal DemandaDia,
    decimal EstoqueSaldo,
    decimal? EstoqueSeguranca,
    decimal? EstoqueMaximo,
    short DiasEstoque,
    decimal PedidosPendentes,
    decimal CompraSugerida,
    decimal CompraAutorizada,
    decimal? PrecoCompra,
    decimal? FatorEmbalagem,
    bool Falteiro);
