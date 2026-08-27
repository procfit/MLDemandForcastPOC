using CosmosPro.ML.DemandForCast.Worker.Training;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Worker.Purchasing;

/// <summary>
/// Lê o estoque inicial (do dia anterior à janela de simulação) por SKU×loja do
/// Stage. É o "snapshot" físico real da rede no instante T₀ — ponto de partida
/// honesto para todas as políticas comparadas.
///
/// <para>
/// Estratégia: para cada SKU×loja, último <c>QuantidadeEmEstoque</c> registrado
/// em <c>EstoquesDiarios</c> com Data &lt; <paramref name="primeiroDiaSimulacao"/>.
/// Lojas/SKUs sem registro caem para 0 (sem estoque inicial — política terá que
/// abastecer no primeiro pedido).
/// </para>
/// </summary>
internal sealed class StageEstoqueInicialLoader(string connectionString, ILogger logger)
{
    public async Task<Dictionary<(string Sku, int LojaId), decimal>> LoadAsync(
        int redeId,
        IReadOnlyCollection<string> skus,
        DateOnly primeiroDiaSimulacao,
        CancellationToken ct)
    {
        var result = new Dictionary<(string, int), decimal>(skus.Count * 8);
        if (skus.Count == 0) return result;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Join com tabela temporária, e nao Sku IN (@s0, ...): a lista vem dos itens da
        // simulacao e pode passar dos 2100 parametros que o SQL Server aceita por comando.
        var escopo = await EscopoDeSkus.MaterializarAsync(conn, skus, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@cutoff", primeiroDiaSimulacao.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"
            SELECT Sku, LojaId, QuantidadeEmEstoque
            FROM (
                SELECT e.Sku, e.LojaId, e.QuantidadeEmEstoque,
                       ROW_NUMBER() OVER (PARTITION BY e.Sku, e.LojaId ORDER BY e.Data DESC) AS rn
                FROM dbo.EstoquesDiarios e
                {escopo.Join("e")}
                WHERE e.RedeId = @redeId AND e.Data < @cutoff
            ) t WHERE rn = 1";

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sku = r.GetString(0);
            var loja = r.GetInt32(1);
            var qty = r.IsDBNull(2) ? 0m : r.GetDecimal(2);
            result[(sku, loja)] = Math.Max(0, qty);
        }
        logger.LogInformation("Estoque inicial carregado para {N} (sku,loja).", result.Count);
        return result;
    }

}
