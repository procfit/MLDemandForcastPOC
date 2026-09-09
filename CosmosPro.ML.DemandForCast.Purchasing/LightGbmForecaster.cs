using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting;
using CosmosPro.ML.DemandForCast.Forecasting.Engines;

namespace CosmosPro.ML.DemandForCast.Purchasing;

/// <summary>
/// Adapter de um <see cref="IForecastModel"/> treinado para <see cref="IForecaster"/>.
/// Indexa as features pré-construídas por (Sku, Loja, Data) e devolve a previsão
/// pontual do modelo. Mantém o simulador agnóstico em relação ao ML.NET.
///
/// <para>
/// <b>Aceita qualquer engine, e não só o LightGBM de um estágio.</b> A assinatura pedia
/// <c>LightGbmForecastModel</c> concreto, o que impedia simular a decisão de compra com o
/// <c>HurdleForecastEngine</c> — e comparar a DECISÃO é justamente o que decide se ele merece
/// virar produção (o menor erro por unidade, medido na F17, não decide isso sozinho). O
/// adaptador nunca precisou de mais que <c>Predict(FeatureVector)</c>.
/// </para>
///
/// <para>
/// As features fornecidas devem ter sido geradas com o mesmo lead time usado no
/// treino — caso contrário, a previsão olha para uma janela de histórico
/// incompatível com a aprendida.
/// </para>
/// </summary>
public sealed class LightGbmForecaster : IForecaster
{
    private readonly IForecastModel _model;
    private readonly Dictionary<(string Sku, int LojaId, DateOnly Data), FeatureVector> _byKey;

    public LightGbmForecaster(IForecastModel model, IEnumerable<FeatureVector> features)
    {
        _model = model;
        _byKey = features.ToDictionary(f => (f.Sku, f.LojaId, f.Data));
    }

    public decimal Predict(string sku, int lojaId, DateOnly data)
    {
        if (!_byKey.TryGetValue((sku, lojaId, data), out var f)) return 0m;
        var v = _model.Predict(f);
        return v <= 0 ? 0m : (decimal)v;
    }
}
