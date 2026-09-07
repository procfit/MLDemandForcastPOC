using CosmosPro.ML.DemandForCast.Features.Models;
using Microsoft.ML;

namespace CosmosPro.ML.DemandForCast.Forecasting.Engines;

/// <summary>
/// Modelo hurdle treinado: o portão devolve <c>P(vende)</c> e o regressor devolve
/// <c>E[unidades | vendeu]</c>; a previsão é o produto dos dois.
///
/// <para>
/// <b>O produto é a previsão de unidades, não uma "unidade provável".</b> Num dia
/// com 20% de chance de vender e 5 unidades esperadas quando vende, a demanda
/// esperada é 1 — e é 1 que entra na média da janela, exatamente como o regressor
/// simples entrega a média condicional dele. Trocar o produto por "prever 5 quando
/// P &gt; 0,5 e 0 caso contrário" mudaria a grandeza: viraria a moda, não a média,
/// e superestimaria o item de giro alto enquanto zeraria o de giro baixo.
/// </para>
///
/// <para>
/// Não é thread-safe (dois <see cref="PredictionEngine{TSrc,TDst}"/> dentro) — uso
/// sequencial, como o <see cref="LightGbmForecastModel"/>.
/// </para>
///
/// <para>
/// <b>Não tem Save/Load, e isso é um limite consciente enquanto ele é só medido.</b>
/// O <c>.zip</c> do ML.NET guarda UM pipeline, e aqui há dois — persistir exigiria um
/// contêiner próprio e um formato versionado. Hoje o hurdle existe para aparecer na
/// comparação walk-forward de cada treino; o modelo que vai para o MinIO é o
/// <see cref="LightGbmForecastModel"/>, e <c>TreinoProcessor.SaveModelAsync</c>
/// recebe esse tipo. <b>Ao promover o hurdle a engine de produção, os dois estágios
/// precisam de persistência antes</b> — trocar só o engine faria o cast do processador
/// de treino estourar, e a fase de comparação passaria a baixar um modelo que não é o
/// que foi avaliado.
/// </para>
/// </summary>
public sealed class HurdleForecastModel : IForecastModel, IDisposable
{
    private readonly PredictionEngine<LightGbmInput, PortaoOutput>? _portao;
    private readonly PredictionEngine<LightGbmInput, LightGbmOutput> _quantidade;

    internal HurdleForecastModel(MLContext mlContext, ITransformer? portao, ITransformer quantidade)
    {
        _portao = portao is null
            ? null
            : mlContext.Model.CreatePredictionEngine<LightGbmInput, PortaoOutput>(portao);
        _quantidade = mlContext.Model.CreatePredictionEngine<LightGbmInput, LightGbmOutput>(quantidade);
    }

    public double Predict(FeatureVector features)
    {
        var input = LightGbmInput.From(features);

        // Portão nulo = o treino não tinha um único dia zerado, então P(vende) = 1 e
        // o hurdle degenera no regressor. Ver HurdleForecastEngine.Fit.
        var p = _portao is null ? 1.0 : _portao.Predict(input).Probability;
        var q = _quantidade.Predict(input).Score;

        // Clamp por dia, e não depois de somar: unidades negativas não existem, e
        // deixar um dia negativo abater um dia positivo faria a taxa de demanda
        // ficar abaixo do que qualquer dia isolado justifica.
        var previsto = p * q;
        return previsto > 0 ? previsto : 0;
    }

    public void Dispose()
    {
        _portao?.Dispose();
        _quantidade.Dispose();
    }
}

/// <summary>Saída do portão. A coluna que interessa é a probabilidade calibrada.</summary>
public sealed class PortaoOutput
{
    public float Probability { get; set; }
}
