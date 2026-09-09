using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting;
using CosmosPro.ML.DemandForCast.Purchasing;

namespace CosmosPro.ML.DemandForCast.Purchasing.Tests;

/// <summary>
/// O adaptador que liga um modelo treinado ao simulador de compra.
///
/// <para>
/// <b>Ele aceita qualquer <see cref="IForecastModel"/>, e isso é o ponto.</b> A assinatura
/// pedia o tipo concreto do LightGBM de um estágio, o que impedia simular a DECISÃO DE COMPRA
/// com o motor de dois estágios — e é a decisão de compra, não o erro por unidade, que decide
/// se um motor merece virar produção. O adaptador nunca precisou de mais que
/// <c>Predict(FeatureVector)</c>.
/// </para>
/// </summary>
public sealed class LightGbmForecasterTests
{
    private static readonly DateOnly Dia = new(2026, 4, 20);

    /// <summary>Modelo de mentira: devolve o que lhe mandarem, sem ML.NET no caminho.</summary>
    private sealed class ModeloFixo(double valor) : IForecastModel
    {
        public double Predict(FeatureVector features) => valor;
    }

    private static FeatureVector Fv(string sku, int loja, DateOnly data) => new()
    {
        Data = data,
        LojaId = loja,
        Sku = sku,
        Target = 0m,
        IsValidTarget = true,
    };

    [Fact]
    public void Aceita_qualquer_modelo_e_nao_so_o_do_lightgbm()
    {
        // Compila com um IForecastModel que nao e do ML.NET — era o que a assinatura
        // concreta impedia, e o que travava a simulacao com o motor de dois estagios.
        var f = new LightGbmForecaster(new ModeloFixo(3.5), [Fv("SKU1", 1, Dia)]);

        f.Predict("SKU1", 1, Dia).Should().Be(3.5m);
    }

    /// <summary>
    /// Chave sem features devolve <b>zero</b>, e aqui zero é a resposta certa: o contrato de
    /// <see cref="IForecaster"/> pede isso para o início do histórico e para série não treinada.
    /// É o único lugar do sistema em que "não sei" vira zero — e vira porque o simulador precisa
    /// de um número para andar o dia, não porque a ausência foi esquecida.
    /// </summary>
    [Fact]
    public void Chave_sem_features_devolve_zero()
    {
        var f = new LightGbmForecaster(new ModeloFixo(9), [Fv("SKU1", 1, Dia)]);

        f.Predict("OUTRO", 1, Dia).Should().Be(0m);
        f.Predict("SKU1", 999, Dia).Should().Be(0m);
        f.Predict("SKU1", 1, Dia.AddDays(1)).Should().Be(0m);
    }

    /// <summary>
    /// Previsão negativa é truncada em zero: demanda negativa não existe, e o simulador a
    /// usaria como se a loja devolvesse unidades. O regressor por erro quadrático produz
    /// negativo justamente em série esparsa, que é a maioria do catálogo farma.
    /// </summary>
    [Fact]
    public void Previsao_negativa_vira_zero_e_nao_desconta()
    {
        var f = new LightGbmForecaster(new ModeloFixo(-4), [Fv("SKU1", 1, Dia)]);

        f.Predict("SKU1", 1, Dia).Should().Be(0m);
    }
}
