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
/// </summary>
internal sealed class StageObservationLoader(string connectionString, ILogger logger)
{
    public async Task<IReadOnlyList<DailyObservation>> LoadAsync(int redeId, int maxSkus, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var (selectedSkus, abcBySku) = await SelectTopSkusAndAbcAsync(conn, redeId, maxSkus, ct);
        if (selectedSkus.Count == 0)
        {
            logger.LogWarning("Stage da rede {RedeId} não tem vendas — nada a treinar.", redeId);
            return [];
        }
        logger.LogInformation("Treino da rede {RedeId} sobre {N} SKUs (top por volume).", redeId, selectedSkus.Count);

        var produtos = await LoadProdutosAsync(conn, redeId, ct);
        var lojas = await LoadLojasAsync(conn, redeId, ct);
        var promosBySku = await LoadPromocoesAsync(conn, redeId, ct);

        // Acumulador mutável por (Sku, LojaId, Data).
        var acc = new Dictionary<(string Sku, int Loja, DateOnly Data), Mutable>();

        await ReadVendasAsync(conn, redeId, selectedSkus, acc, ct);
        await MarkRupturasAsync(conn, redeId, selectedSkus, acc, ct);

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

    private sealed class Mutable
    {
        public decimal Quantidade;
        public decimal PrecoUnitario;
        public bool EmRuptura;
    }

    /// <remarks>
    /// O ranking é <b>por rede</b>. Sem o filtro, a rede maior monopolizaria o corte
    /// de <c>maxSkus</c> e a menor treinaria com quase nada.
    /// </remarks>
    private static async Task<(HashSet<string> Skus, Dictionary<string, string> Abc)> SelectTopSkusAndAbcAsync(
        SqlConnection conn, int redeId, int maxSkus, CancellationToken ct)
    {
        var totals = new List<(string Sku, decimal Vol)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT Sku, SUM(Quantidade) AS Vol
                FROM dbo.Vendas
                WHERE RedeId = @redeId
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

    private static async Task ReadVendasAsync(
        SqlConnection conn, int redeId, HashSet<string> skus,
        Dictionary<(string, int, DateOnly), Mutable> acc, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT Data, LojaId, Sku, Quantidade, PrecoUnitario
            FROM dbo.Vendas
            WHERE RedeId = @redeId AND Sku IN ({InClause(skus, cmd)})";
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

    private static async Task MarkRupturasAsync(
        SqlConnection conn, int redeId, HashSet<string> skus,
        Dictionary<(string, int, DateOnly), Mutable> acc, CancellationToken ct)
    {
        // Dias com estoque <= 0 são ruptura. Podem não ter linha em Vendas (não
        // houve venda justamente por falta) — criamos a observação com qty 0 e
        // EmRuptura=true para o backtest NÃO contá-la como demanda zero genuína.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT Data, LojaId, Sku
            FROM dbo.EstoquesDiarios
            WHERE RedeId = @redeId AND QuantidadeEmEstoque <= 0 AND Sku IN ({InClause(skus, cmd)})";
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

    private static async Task<Dictionary<string, List<(DateOnly Ini, DateOnly Fim, int? Loja)>>> LoadPromocoesAsync(
        SqlConnection conn, int redeId, CancellationToken ct)
    {
        var d = new Dictionary<string, List<(DateOnly, DateOnly, int?)>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DataInicio, DataFim, Sku, LojaId FROM dbo.Promocoes WHERE RedeId = @redeId";
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
