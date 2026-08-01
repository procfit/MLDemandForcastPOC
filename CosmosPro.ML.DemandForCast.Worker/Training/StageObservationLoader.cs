using CosmosPro.ML.DemandForCast.Features.Models;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Worker.Training;

/// <summary>
/// Lê o banco Stage e monta a série de <see cref="DailyObservation"/> que alimenta
/// o feature engineering (F5). Cruza Vendas com os mestres (Produtos/Lojas), marca
/// ruptura a partir de EstoquesDiarios e promoção a partir de Promocoes, e deriva a
/// classe ABC do volume de vendas (não vem nos mestres).
///
/// <para>
/// Limita aos <c>maxSkus</c> SKUs de maior volume — o backtest retreina o LightGBM
/// a cada fold, então o número de SKUs domina o tempo de treino. Decisão de POC.
/// </para>
///
/// <para>
/// <b>Corte de informação:</b> com <c>treinoAte</c> definido, nenhuma consulta com
/// eixo temporal pode alcançar essa data ou depois dela — ver
/// <see cref="Engine.Entities.TreinoJob.TreinoAte"/>. Ao acrescentar uma fonte
/// nova aqui, decida explicitamente se ela é datada (filtra) ou atemporal (não
/// filtra); esquecer o filtro reintroduz o vazamento sem nenhum sintoma visível.
/// </para>
///
/// <para>
/// Duas brechas residuais, ambas sem conserto na modelagem atual do Stage e por
/// isso registradas em vez de silenciadas: (a) Produtos e Lojas não são
/// historizados e o import substitui a tabela inteira, então um produto
/// recategorizado depois do corte carrega o valor novo para trás; (b) uma campanha
/// cadastrada retroativamente entra com <c>DataInicio</c> anterior ao corte, porque
/// não existe coluna de "data de cadastro" para distinguir.
/// </para>
/// </summary>
internal sealed class StageObservationLoader(string connectionString, ILogger logger)
{
    public async Task<IReadOnlyList<DailyObservation>> LoadAsync(
        int redeId, int maxSkus, DateOnly? treinoAte, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var (selectedSkus, abcBySku) = await SelectTopSkusAndAbcAsync(conn, redeId, maxSkus, treinoAte, ct);
        if (selectedSkus.Count == 0)
        {
            logger.LogWarning("Stage da rede {RedeId} não tem vendas — nada a treinar.", redeId);
            return [];
        }
        logger.LogInformation(
            "Treino da rede {RedeId} sobre {N} SKUs (top por volume), corte {Corte}.",
            redeId, selectedSkus.Count, treinoAte?.ToString("yyyy-MM-dd") ?? "nenhum");

        var produtos = await LoadProdutosAsync(conn, redeId, ct);
        var lojas = await LoadLojasAsync(conn, redeId, ct);
        var promosBySku = await LoadPromocoesAsync(conn, redeId, treinoAte, ct);

        // Acumulador mutável por (Sku, LojaId, Data).
        var acc = new Dictionary<(string Sku, int Loja, DateOnly Data), Mutable>();

        await ReadVendasAsync(conn, redeId, selectedSkus, treinoAte, acc, ct);
        await MarkRupturasAsync(conn, redeId, selectedSkus, treinoAte, acc, ct);

        // Materializa as observações, aplicando promoção e atributos estáticos.
        var result = new List<DailyObservation>(acc.Count);
        foreach (var ((sku, loja, data), m) in acc)
        {
            produtos.TryGetValue(sku, out var prod);
            lojas.TryGetValue(loja, out var lj);
            var emPromocao = IsEmPromocao(promosBySku, sku, loja, data);

            result.Add(new DailyObservation
            {
                Data = data,
                LojaId = loja,
                Sku = sku,
                Quantidade = m.Quantidade,
                PrecoUnitario = m.PrecoUnitario,
                EmRuptura = m.EmRuptura,
                EmPromocao = emPromocao,
                Categoria = prod.Categoria ?? "",
                PrincipioAtivo = prod.PrincipioAtivo ?? "",
                ClasseAbc = abcBySku.GetValueOrDefault(sku, "C"),
                UF = lj.UF ?? "",
                Regiao = lj.Regiao ?? "",
                PerfilLoja = lj.Perfil ?? "",
            });
        }

        logger.LogInformation("{N} observações montadas para o feature engineering.", result.Count);
        return result;
    }

    /// <summary>
    /// Só o orçamento top-<paramref name="maxSkus"/> e a classe ABC de cada SKU dele, no
    /// corte pedido. Mesma seleção e mesma regra de classificação de
    /// <see cref="LoadAsync"/> — é a mesma consulta —, sem montar a série: quem precisa
    /// apenas do rótulo ABC não deveria pagar a varredura de <c>Vendas</c> linha a linha,
    /// a de <c>EstoquesDiarios</c> e a materialização de todas as observações.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> LoadOrcamentoAbcAsync(
        int redeId, int maxSkus, DateOnly? treinoAte, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var (selectedSkus, abcBySku) = await SelectTopSkusAndAbcAsync(conn, redeId, maxSkus, treinoAte, ct);

        var result = new Dictionary<string, string>(selectedSkus.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var sku in selectedSkus)
            result[sku] = abcBySku.GetValueOrDefault(sku, "C");

        logger.LogInformation(
            "Orçamento da rede {RedeId} no corte {Corte}: {N} SKU(s) com classe ABC.",
            redeId, treinoAte?.ToString("yyyy-MM-dd") ?? "nenhum", result.Count);
        return result;
    }

    private sealed class Mutable
    {
        public decimal Quantidade;
        public decimal PrecoUnitario;
        public bool EmRuptura;
    }

    /// <remarks>
    /// O ranking é <b>por rede</b>. Sem o filtro, a rede maior monopolizaria o corte
    /// de <c>maxSkus</c> e a menor treinaria com quase nada.
    ///
    /// <para>
    /// Respeita <paramref name="treinoAte"/>: tanto a seleção dos SKUs quanto a classe
    /// ABC saem de uma soma sobre a variável-alvo. Somar o período inteiro vazaria o
    /// futuro duas vezes — escolheria os SKUs sabendo quais venderiam, e a ClasseAbc,
    /// que entra como feature, carregaria o volume de depois do corte.
    /// </para>
    /// </remarks>
    private static async Task<(HashSet<string> Skus, Dictionary<string, string> Abc)> SelectTopSkusAndAbcAsync(
        SqlConnection conn, int redeId, int maxSkus, DateOnly? treinoAte, CancellationToken ct)
    {
        var totals = new List<(string Sku, decimal Vol)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT Sku, SUM(Quantidade) AS Vol
                FROM dbo.Vendas
                WHERE RedeId = @redeId {CorteData("Data", treinoAte, cmd)}
                GROUP BY Sku ORDER BY Vol DESC";
            cmd.Parameters.AddWithValue("@redeId", redeId);
            cmd.CommandTimeout = 300;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                totals.Add((r.GetString(0), r.IsDBNull(1) ? 0 : r.GetDecimal(1)));
        }

        // Classe ABC por volume cumulativo (A: até 80%, B: até 95%, C: resto).
        var grand = totals.Sum(t => t.Vol);
        var abc = new Dictionary<string, string>(totals.Count, StringComparer.OrdinalIgnoreCase);
        decimal cum = 0;
        foreach (var (sku, vol) in totals)
        {
            cum += vol;
            var ratio = grand > 0 ? cum / grand : 1m;
            abc[sku] = ratio <= 0.8m ? "A" : ratio <= 0.95m ? "B" : "C";
        }

        var selected = totals.Take(maxSkus).Select(t => t.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (selected, abc);
    }

    /// <remarks>A variável-alvo. É o vazamento que o corte existe para impedir.</remarks>
    private static async Task ReadVendasAsync(
        SqlConnection conn, int redeId, HashSet<string> skus, DateOnly? treinoAte,
        Dictionary<(string, int, DateOnly), Mutable> acc, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT Data, LojaId, Sku, Quantidade, PrecoUnitario
            FROM dbo.Vendas
            WHERE RedeId = @redeId AND Sku IN ({InClause(skus, cmd)}) {CorteData("Data", treinoAte, cmd)}";
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.CommandTimeout = 600;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var data = DateOnly.FromDateTime(r.GetDateTime(0));
            var loja = r.GetInt32(1);
            var sku = r.GetString(2);
            var key = (sku, loja, data);
            if (!acc.TryGetValue(key, out var m)) { m = new Mutable(); acc[key] = m; }
            m.Quantidade += r.IsDBNull(3) ? 0 : r.GetDecimal(3);
            m.PrecoUnitario = r.IsDBNull(4) ? 0 : r.GetDecimal(4);
        }
    }

    /// <remarks>
    /// Datada e materializante: uma ruptura sem venda correspondente <b>cria</b> a
    /// observação. Sem o corte, um dia posterior a ele entraria na série mesmo com
    /// Vendas já filtrada.
    /// </remarks>
    private static async Task MarkRupturasAsync(
        SqlConnection conn, int redeId, HashSet<string> skus, DateOnly? treinoAte,
        Dictionary<(string, int, DateOnly), Mutable> acc, CancellationToken ct)
    {
        // Dias com estoque <= 0 são ruptura. Podem não ter linha em Vendas (não
        // houve venda justamente por falta) — criamos a observação com qty 0 e
        // EmRuptura=true para o backtest NÃO contá-la como demanda zero genuína.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT Data, LojaId, Sku
            FROM dbo.EstoquesDiarios
            WHERE RedeId = @redeId AND QuantidadeEmEstoque <= 0
              AND Sku IN ({InClause(skus, cmd)}) {CorteData("Data", treinoAte, cmd)}";
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.CommandTimeout = 600;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetString(2), r.GetInt32(1), DateOnly.FromDateTime(r.GetDateTime(0)));
            if (!acc.TryGetValue(key, out var m)) { m = new Mutable(); acc[key] = m; }
            m.EmRuptura = true;
        }
    }

    /// <remarks>
    /// <b>Sem corte, deliberadamente.</b> Categoria e princípio ativo são atributos de
    /// cadastro sem eixo temporal — a tabela não tem coluna de data e não há
    /// historização de versões. Não há o que filtrar: um produto conhecido hoje
    /// descreve o mesmo produto de um ano atrás.
    /// </remarks>
    private static async Task<Dictionary<string, (string? Categoria, string? PrincipioAtivo)>> LoadProdutosAsync(
        SqlConnection conn, int redeId, CancellationToken ct)
    {
        var d = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Sku, Categoria, PrincipioAtivo FROM dbo.Produtos WHERE RedeId = @redeId";
        cmd.Parameters.AddWithValue("@redeId", redeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            d[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        return d;
    }

    /// <remarks>
    /// <b>Sem corte, deliberadamente.</b> UF, região e perfil são geografia da loja,
    /// não evento datado. A tabela tem <c>DataAbertura</c>, mas nenhuma das colunas
    /// lidas aqui depende dela, e uma loja aberta depois do corte não vira observação:
    /// só entram no dicionário lojas que já aparecem em Vendas/EstoquesDiarios, ambas
    /// já cortadas.
    /// </remarks>
    private static async Task<Dictionary<int, (string? UF, string? Regiao, string? Perfil)>> LoadLojasAsync(
        SqlConnection conn, int redeId, CancellationToken ct)
    {
        var d = new Dictionary<int, (string?, string?, string?)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LojaId, UF, Regiao, Perfil FROM dbo.Lojas WHERE RedeId = @redeId";
        cmd.Parameters.AddWithValue("@redeId", redeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            d[r.GetInt32(0)] = (
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3));
        return d;
    }

    /// <remarks>
    /// Cortada por <c>DataInicio</c>: uma promoção que ainda não começou no corte não
    /// é informação disponível naquele instante. O filtro é redundante na aritmética
    /// atual — <see cref="IsEmPromocao"/> só casa datas dentro do intervalo, e toda
    /// observação já é anterior ao corte —, mas a invariante fica local ao SQL em vez
    /// de depender de como o consumidor a usa. Campanhas iniciadas antes do corte
    /// permanecem, inclusive as que terminam depois: só afetam dias anteriores a ele.
    /// </remarks>
    private static async Task<Dictionary<string, List<(DateOnly Ini, DateOnly Fim, int? Loja)>>> LoadPromocoesAsync(
        SqlConnection conn, int redeId, DateOnly? treinoAte, CancellationToken ct)
    {
        var d = new Dictionary<string, List<(DateOnly, DateOnly, int?)>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT DataInicio, DataFim, Sku, LojaId
            FROM dbo.Promocoes
            WHERE RedeId = @redeId {CorteData("DataInicio", treinoAte, cmd)}";
        cmd.Parameters.AddWithValue("@redeId", redeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sku = r.GetString(2);
            if (!d.TryGetValue(sku, out var list)) { list = []; d[sku] = list; }
            list.Add((
                DateOnly.FromDateTime(r.GetDateTime(0)),
                DateOnly.FromDateTime(r.GetDateTime(1)),
                r.IsDBNull(3) ? null : r.GetInt32(3)));
        }
        return d;
    }

    private static bool IsEmPromocao(
        Dictionary<string, List<(DateOnly Ini, DateOnly Fim, int? Loja)>> promos,
        string sku, int loja, DateOnly data)
    {
        if (!promos.TryGetValue(sku, out var list)) return false;
        foreach (var (ini, fim, lojaPromo) in list)
        {
            if (data >= ini && data <= fim && (lojaPromo is null || lojaPromo == loja))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Devolve o predicado de corte para a coluna de data indicada, ou string vazia
    /// quando não há corte. Estritamente menor: o dia do corte já é futuro.
    /// <para>
    /// O parâmetro é numerado porque um mesmo <see cref="SqlCommand"/> pode precisar
    /// cortar duas colunas (uma tabela com início e fim, por exemplo); nome fixo
    /// estouraria "parameter already added" só em runtime.
    /// </para>
    /// </summary>
    private static string CorteData(string coluna, DateOnly? treinoAte, SqlCommand cmd)
    {
        if (treinoAte is not { } corte) return "";
        var p = $"@treinoAte{cmd.Parameters.Count}";
        cmd.Parameters.AddWithValue(p, corte);
        return $"AND {coluna} < {p}";
    }

    /// <summary>Monta um IN (@s0, @s1, ...) parametrizado e adiciona os parâmetros ao comando.</summary>
    private static string InClause(HashSet<string> skus, SqlCommand cmd)
    {
        var names = new List<string>(skus.Count);
        int i = 0;
        foreach (var sku in skus)
        {
            var p = $"@s{i++}";
            names.Add(p);
            cmd.Parameters.AddWithValue(p, sku);
        }
        return string.Join(", ", names);
    }
}
