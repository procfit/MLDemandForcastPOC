using CosmosPro.ML.DemandForCast.Features.Models;
using Microsoft.ML;
using Microsoft.ML.Trainers.LightGbm;

namespace CosmosPro.ML.DemandForCast.Forecasting.Engines;

/// <summary>
/// Motor de dois estágios ("hurdle") para demanda com excesso de zeros:
/// <c>P(vende no dia) x E[unidades | vendeu]</c>. Dois LightGBM sobre as MESMAS
/// features de F5 — um classificador binário e um regressor ajustado apenas nos
/// dias com venda.
///
/// <para>
/// <b>Por que existe.</b> No histórico farma a esmagadora maioria das linhas
/// diárias é zero. O <see cref="LightGbmForecastEngine"/> ajusta um único
/// regressor por erro quadrático sobre todas elas, e a média condicional que ele
/// aprende é puxada para baixo pelos zeros: o modelo acerta o "não vende" e
/// subestima o "vende". O hurdle separa as duas perguntas, de modo que o segundo
/// estágio nunca vê um zero e portanto não é puxado por eles.
/// </para>
///
/// <para>
/// <b>Por que não trocar o objetivo do LightGBM.</b> A resposta usual para excesso
/// de zeros é trocar a função de perda (Tweedie, Poisson). O ML.NET 4.0.2 <b>não
/// expõe</b> o parâmetro <c>objective</c> do LightGBM: nem
/// <c>LightGbmRegressionTrainer.Options</c> nem a <c>OptionsBase</c> herdada têm
/// essa chave, e o objetivo fica fixo em erro quadrático. O <c>Poisson</c> que o
/// ML.NET oferece é um trainer linear separado
/// (<c>LbfgsPoissonRegression</c>) — outra classe de modelo, sem as interações que
/// a árvore captura, então uma derrota dele não diria nada sobre a perda. O hurdle
/// é a forma de atacar o excesso de zeros que cabe dentro do ML.NET, sem sidecar e
/// sem pacote novo.
/// </para>
///
/// <para>
/// <b>Medido em dado real</b> (histórico da rede, walk-forward de 4 janelas × 28
/// dias), o viés do regressor único cresce com a esparsidade — −1,3% a 66,7% de
/// dias-item sem venda, −4,6% a 74,5%, <b>−7,7% a 86,6%</b>, que é o catálogo
/// inteiro — e ali o hurdle o reduz a −2,2%, ganhando também em WAPE, MAE e RMSE. É
/// a confirmação de que o problema é a média condicional puxada pelos zeros, e não
/// intermitência.
/// </para>
///
/// <para>
/// <b>Não é uniformemente melhor, e a ressalva importa.</b> No recorte dos 1.200 SKUs
/// de maior volume o regressor único já é quase não-enviesado (−1,3%) e o hurdle passa
/// do alvo (+3,2%); o MAPE dele também fica ligeiramente pior em todos os recortes. O
/// ganho aparece onde o excesso de zeros morde — que é onde o catálogo de verdade
/// vive. Números por recorte no README §6, F17.
/// </para>
///
/// <para>
/// <b>Não é o engine de produção</b>, e a escolha depende de medir a DECISÃO de
/// compra (ruptura e sobra), não só o erro de previsão: menor WAPE é compatível com
/// comprar menos do que a loja precisa. Ver a nota em <c>TreinoProcessor</c> e o
/// limite de persistência em <see cref="HurdleForecastModel"/>.
/// </para>
/// </summary>
public sealed class HurdleForecastEngine : IForecastEngine
{
    private readonly LightGbmHyperparameters _hp;

    public HurdleForecastEngine(LightGbmHyperparameters? hyperparameters = null)
    {
        _hp = hyperparameters ?? new LightGbmHyperparameters();
    }

    public string Name => "lightgbm-hurdle";

    public IForecastModel Fit(IReadOnlyList<FeatureVector> trainSet)
    {
        var mlContext = new MLContext(seed: _hp.Seed);
        var rows = trainSet.Select(LightGbmInput.From).ToList();

        var positivos = rows.Count(r => r.Vendeu);

        // Série que nunca vendeu no treino: não há segundo estágio a ajustar, e o
        // classificador teria uma classe só. A previsão honesta é zero.
        if (positivos == 0)
            return new ZeroModel();

        var todas = mlContext.Data.LoadFromEnumerable(rows);
        var soPositivas = mlContext.Data.LoadFromEnumerable(rows.Where(r => r.Vendeu).ToList());

        // Sem nenhum dia zerado o portão é constante em 1, e o LightGBM binário
        // recusaria um rótulo de classe única. O hurdle degenera exatamente no
        // regressor simples — que é o comportamento certo, não um caso de erro.
        var portao = positivos == rows.Count
            ? null
            : Featurizacao(mlContext)
                .Append(mlContext.BinaryClassification.Trainers.LightGbm(OpcoesDoPortao()))
                .Fit(todas);

        var quantidade = Featurizacao(mlContext)
            .Append(mlContext.Regression.Trainers.LightGbm(OpcoesDaQuantidade()))
            .Fit(soPositivas);

        return new HurdleForecastModel(mlContext, portao, quantidade);
    }

    private static IEstimator<ITransformer> Featurizacao(MLContext mlContext)
    {
        var oneHotPairs = LightGbmInput.CategoricalColumns
            .Select(c => new InputOutputColumnPair($"{c}_oh", c))
            .ToArray();

        var featureColumns = LightGbmInput.NumericColumns
            .Concat(LightGbmInput.CategoricalColumns.Select(c => $"{c}_oh"))
            .ToArray();

        return mlContext.Transforms.Categorical
            .OneHotEncoding(oneHotPairs)
            .Append(mlContext.Transforms.Concatenate("Features", featureColumns))
            .AppendCacheCheckpoint(mlContext);
    }

    /// <summary>
    /// Opções do portão. <b><see cref="LightGbmBinaryTrainer.Options.UnbalancedSets"/>
    /// fica em false de propósito</b>, apesar de "vendeu" ser a classe minoritária:
    /// o que o hurdle multiplica é uma PROBABILIDADE, e reponderar a classe rara
    /// descalibra justamente ela — a acurácia balanceada melhoraria e a previsão de
    /// unidades subiria em bloco, porque cada dia passaria a receber um P(vende)
    /// inflado. Aqui calibração vale mais que separação.
    /// </summary>
    private LightGbmBinaryTrainer.Options OpcoesDoPortao() => new()
    {
        LabelColumnName = nameof(LightGbmInput.Vendeu),
        FeatureColumnName = "Features",
        NumberOfLeaves = _hp.NumberOfLeaves,
        NumberOfIterations = _hp.NumberOfIterations,
        LearningRate = _hp.LearningRate,
        MinimumExampleCountPerLeaf = _hp.MinimumExampleCountPerLeaf,
        UnbalancedSets = false,
    };

    private LightGbmRegressionTrainer.Options OpcoesDaQuantidade() => new()
    {
        LabelColumnName = nameof(LightGbmInput.Label),
        FeatureColumnName = "Features",
        NumberOfLeaves = _hp.NumberOfLeaves,
        NumberOfIterations = _hp.NumberOfIterations,
        LearningRate = _hp.LearningRate,
        MinimumExampleCountPerLeaf = _hp.MinimumExampleCountPerLeaf,
    };

    private sealed class ZeroModel : IForecastModel
    {
        public double Predict(FeatureVector features) => 0;
    }
}
