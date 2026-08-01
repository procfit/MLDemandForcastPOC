namespace CosmosPro.ML.DemandForCast.Extractor;

internal readonly record struct StockMovement(int LojaId, string Sku, DateOnly Data, decimal Saldo);

/// <summary>
/// Converte movimentos esparsos de estoque em série diária densa.
/// </summary>
/// <remarks>
/// Sem isso o Stage teria linha apenas nos dias com movimento, e o
/// <c>FeatureBuilder</c> preenche dia faltante com <c>Quantidade = 0</c> e
/// <c>EmRuptura = false</c> — ou seja, um dia em ruptura (justamente sem
/// movimento por não haver estoque) entraria no treino como demanda zero
/// legítima, enviesando o modelo para baixo.
/// </remarks>
internal static class StockCarryForward
{
    /// <summary>
    /// Recebe movimentos <b>ordenados por (LojaId, Sku, Data)</b> e devolve um
    /// registro por dia até <paramref name="periodEnd"/>, repetindo o último
    /// saldo conhecido nos dias sem movimento.
    /// Dias anteriores ao primeiro movimento de cada par não são emitidos: o
    /// saldo ali é desconhecido e assumir zero criaria ruptura falsa.
    /// </summary>
    public static IEnumerable<StockMovement> Densify(IEnumerable<StockMovement> ordered, DateOnly periodEnd)
    {
        StockMovement? previous = null;

        foreach (var current in ordered)
        {
            if (previous is not { } prev)
            {
                previous = current;
                continue;
            }

            var sameSeries = prev.LojaId == current.LojaId && string.Equals(prev.Sku, current.Sku, StringComparison.Ordinal);

            // A query já agrega por dia; se ainda assim vier duplicata, o último vence.
            if (sameSeries && current.Data == prev.Data)
            {
                previous = current;
                continue;
            }

            yield return prev;

            // Mesma série: preenche até a véspera do próximo movimento.
            // Série nova: o par anterior se estende até o fim do período.
            var fillUntil = sameSeries ? current.Data.AddDays(-1) : periodEnd;
            for (var dia = prev.Data.AddDays(1); dia <= fillUntil; dia = dia.AddDays(1))
            {
                yield return prev with { Data = dia };
            }

            previous = current;
        }

        if (previous is { } last)
        {
            yield return last;
            for (var dia = last.Data.AddDays(1); dia <= periodEnd; dia = dia.AddDays(1))
            {
                yield return last with { Data = dia };
            }
        }
    }
}
