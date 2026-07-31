using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Worker.Training;

/// <summary>
/// Resultado serializável de um treino: comparação walk-forward dos engines.
/// Gravado como JSON em <c>TreinoJob.ResultadoJson</c> e renderizado pela UI.
/// </summary>
/// <param name="TreinoAte">
/// Corte pedido no job (<c>null</c> = sem corte). Registrado junto ao resultado para
/// que ler o treino baste para saber sob qual regime ele foi produzido.
/// </param>
/// <param name="UltimaDataTreinada">
/// Data mais recente <b>carregada</b> do Stage. É o valor honesto para
/// <c>ComparisonItem.ModeloTreinadoAte</c>: com corte é no máximo o dia anterior a
/// ele, e sem corte é o fim do histórico importado — em nenhum dos casos dá para
/// deduzi-lo de <paramref name="TreinoAte"/>.
/// <para>
/// É um teto, não o dia exato do ajuste: o fit descarta as observações sem alvo
/// válido (ruptura), então se o último dia carregado for de ruptura o ajuste
/// terminou antes. O erro só existe nessa direção, que deixa a checagem do
/// comparador mais rígida — nunca mais frouxa.
/// </para>
/// </param>
public sealed record TrainingResult(
    DateTimeOffset GeradoEm,
    int SkusUsados,
    long TotalObservacoes,
    long TotalFeatures,
    int Folds,
    int TestWindowDias,
    IReadOnlyList<EngineResult> Engines,
    string MelhorEngine,
    DateOnly? TreinoAte = null,
    DateOnly? UltimaDataTreinada = null);

public sealed record EngineResult(
    string Engine,
    MetricsDto Global,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricsDto>> PorDimensao);

public sealed record MetricsDto(int N, double Mae, double Rmse, double Wape, double? Mape)
{
    public static MetricsDto From(ForecastMetrics m) => new(m.N, m.Mae, m.Rmse, m.Wape, m.Mape);
}
