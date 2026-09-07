using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Engines;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Forecasting.Tests;

/// <summary>
/// O motor de dois estagios contra o regressor simples.
///
/// <para>
/// <b>A serie destes testes e ESPARSA, e isso nao e detalhe.</b> O dataset de
/// <see cref="LightGbmForecastEngineTests"/> vende todos os dias — e util para
/// verificar que o pipeline treina, e inutil para qualquer afirmacao sobre excesso
/// de zeros, que e o problema todo aqui. E a mesma razao pela qual o skew de preco
/// documentado no FeatureBuilder passou por uma suite verde: teste com venda diaria
/// esconde exatamente o que o dado real tem de mais caracteristico.
/// </para>
/// </summary>
public sealed class HurdleForecastEngineTests
{
    private static readonly DateOnly Origem = new(2025, 1, 1);

    private static readonly LightGbmHyperparameters FastHp = new()
    {
        NumberOfIterations = 40,
        NumberOfLeaves = 16,
        MinimumExampleCountPerLeaf = 5,
    };

    /// <summary>
    /// Painel esparso com giro heterogeneo, como o de farma: poucos SKUs de classe
    /// A vendendo quase todo dia e muitos de classe C vendendo raramente. Os lags e
    /// as rolling sao derivados da serie de fato gerada — janela terminando em
    /// D-7, o mesmo lead time do <c>FeatureBuilder</c> —, entao o sinal que o modelo
    /// enxerga e o historico verdadeiro, e nao um numero plantado.
    /// </summary>
    private static List<FeatureVector> PainelEsparso(int dias = 200, int seed = 11)
    {
        var rng = new Random(seed);
        var perfis = new (string Sku, string Classe, double ProbVenda, int QtdBase)[]
        {
            ("SKU-A1", "A", 0.92, 12), ("SKU-A2", "A", 0.85, 9),
            ("SKU-B1", "B", 0.45, 5),  ("SKU-B2", "B", 0.38, 4),
            ("SKU-C1", "C", 0.10, 2),  ("SKU-C2", "C", 0.07, 2),
            ("SKU-C3", "C", 0.05, 1),  ("SKU-C4", "C", 0.04, 1),
        };

        var saida = new List<FeatureVector>();

        foreach (var (sku, classe, prob, qtdBase) in perfis)
        {
            var serie = new decimal[dias];
            for (int t = 0; t < dias; t++)
            {
                var data = Origem.AddDays(t);
                var fds = data.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                // Fim de semana levanta a chance de vender e o tamanho da venda; e o
                // sinal que o portao pode aprender.
                var p = Math.Min(1.0, prob * (fds ? 1.4 : 1.0));
                serie[t] = rng.NextDouble() < p
                    ? Math.Max(1, qtdBase + (fds ? 3 : 0) + rng.Next(-2, 3))
                    : 0m;
            }

            for (int t = 35; t < dias; t++)
            {
                var data = Origem.AddDays(t);
                var fds = data.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

                saida.Add(new FeatureVector
                {
                    Data = data,
                    LojaId = 1,
                    Sku = sku,
                    Target = serie[t],
                    IsValidTarget = true,
                    Lag7 = serie[t - 7],
                    Lag14 = serie[t - 14],
                    Lag21 = serie[t - 21],
                    Lag28 = serie[t - 28],
                    RollMean7 = Media(serie, t - 13, t - 7),
                    RollMean28 = Media(serie, t - 34, t - 7),
                    RollStd28 = 0m,
                    RollMax28 = Max(serie, t - 34, t - 7),
                    DiaDaSemana = (int)data.DayOfWeek,
                    DiaDoMes = data.Day,
                    Mes = data.Month,
                    FimDeSemana = fds,
                    PrecoUnitario = 10m,
                    PrecoRelativoMedia = 1m,
                    Categoria = "OTC",
                    PrincipioAtivo = "Dipirona",
                    ClasseAbc = classe,
                    UF = "SP",
                    Regiao = "Sudeste",
                    PerfilLoja = "Bairro",
                });
            }
        }

        return saida;
    }

    private static decimal Media(decimal[] s, int de, int ate)
    {
        decimal soma = 0; int n = 0;
        for (int i = Math.Max(0, de); i <= ate; i++) { soma += s[i]; n++; }
        return n == 0 ? 0m : soma / n;
    }

    private static decimal Max(decimal[] s, int de, int ate)
    {
        decimal max = 0;
        for (int i = Math.Max(0, de); i <= ate; i++) if (s[i] > max) max = s[i];
        return max;
    }

    [Fact]
    public void Name_do_engine_eh_lightgbm_hurdle()
    {
        new HurdleForecastEngine().Name.Should().Be("lightgbm-hurdle");
    }

    /// <summary>
    /// O rotulo do portao nao pode estar entre as features — seria entregar a
    /// resposta ao regressor. Estrutural de proposito: um teste de erro nao acusaria
    /// isso, ele acusaria um WAPE excelente.
    /// </summary>
    [Fact]
    public void Vendeu_nao_eh_feature()
    {
        LightGbmInput.NumericColumns.Should().NotContain(nameof(LightGbmInput.Vendeu));
        LightGbmInput.CategoricalColumns.Should().NotContain(nameof(LightGbmInput.Vendeu));
    }

    [Fact]
    public void Previsoes_sao_nao_negativas()
    {
        var dados = PainelEsparso();
        using var modelo = (HurdleForecastModel)new HurdleForecastEngine(FastHp).Fit(dados);

        foreach (var fv in dados)
            modelo.Predict(fv).Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Treino sem um unico dia de venda: nao ha segundo estagio a ajustar e o portao
    /// teria uma classe so. Tem de devolver zero, e nao estourar.
    /// </summary>
    [Fact]
    public void Serie_que_nunca_vendeu_preve_zero()
    {
        var dados = PainelEsparso().Select(f => f with { Target = 0m }).ToList();
        var modelo = new HurdleForecastEngine(FastHp).Fit(dados);

        modelo.Predict(dados[0]).Should().Be(0);
    }

    /// <summary>
    /// Treino sem nenhum dia zerado: o portao e constante e o hurdle degenera no
    /// regressor. Comportamento esperado, e nao caso de erro.
    /// </summary>
    [Fact]
    public void Serie_que_vendeu_todo_dia_degenera_no_regressor()
    {
        var dados = PainelEsparso()
            .Select(f => f.Target > 0m ? f : f with { Target = 1m })
            .ToList();

        using var modelo = (HurdleForecastModel)new HurdleForecastEngine(FastHp).Fit(dados);

        modelo.Predict(dados[0]).Should().BeGreaterThan(0);
    }

    /// <summary>
    /// O experimento, como teste: em serie esparsa o hurdle tem de bater o regressor
    /// simples em WAPE, medido em walk-forward — nunca em treino, onde os dois
    /// decoram. Se esta assercao cair, a conclusao do estudo caiu com ela.
    /// </summary>
    [Fact]
    public void Hurdle_bate_o_regressor_simples_em_serie_esparsa()
    {
        var dados = PainelEsparso(dias: 260);
        var backtest = new WalkForwardBacktest(new WalkForwardOptions
        {
            NumberOfFolds = 3,
            TestWindowDays = 21,
            MinTrainDays = 60,
        });

        var simples = backtest.Run(new LightGbmForecastEngine(FastHp), dados);
        var hurdle = backtest.Run(new HurdleForecastEngine(FastHp), dados);

        hurdle.Global.N.Should().Be(simples.Global.N, "os dois arcos precisam ser pontuados na mesma populacao");
        hurdle.Global.Wape.Should().BeLessThan(simples.Global.Wape);
    }
}
