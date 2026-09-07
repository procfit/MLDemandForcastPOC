using System.Text.Json;
using CosmosPro.ML.DemandForCast.Worker.Training;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// O resultado do treino declara <b>duas</b> coisas diferentes: qual engine teve o
/// menor WAPE no backtest e qual engine foi de fato salvo e vai servir a comparacao
/// e a sugestao de compra.
///
/// <para>
/// Elas divergem, e isso nao e defeito: o backtest mede erro por unidade e a decisao
/// de compra depende tambem do volume total. Um engine que encolhe as previsoes na
/// direcao do zero melhora o WAPE numa serie com muitos dias sem venda e compra menos
/// do que a loja precisa. Enquanto a escolha nao estiver feita com numero de dado real
/// na mao, quem e salvo e o LightGBM de sempre — e a tela tem de mostrar os dois campos,
/// senao o trofeu do backtest passa por declaracao de producao.
/// </para>
/// </summary>
public sealed class TrainingResultTests
{
    private static TrainingResult Resultado(string? modeloSalvo) => new(
        GeradoEm: DateTimeOffset.UnixEpoch,
        SkusUsados: 10,
        TotalObservacoes: 100,
        TotalFeatures: 90,
        Folds: 4,
        TestWindowDias: 28,
        Engines: [],
        MelhorEngine: "lightgbm-hurdle",
        ModeloSalvo: modeloSalvo);

    [Fact]
    public void Menor_wape_e_modelo_salvo_viajam_como_campos_distintos()
    {
        var json = JsonSerializer.Serialize(Resultado("lightgbm"), TrainingResultJson.Options);
        var volta = JsonSerializer.Deserialize<TrainingResult>(json, TrainingResultJson.Options)!;

        volta.MelhorEngine.Should().Be("lightgbm-hurdle");
        volta.ModeloSalvo.Should().Be("lightgbm");
        volta.ModeloSalvo.Should().NotBe(volta.MelhorEngine,
            "o caso que interessa e justamente o de divergencia; se um campo fosse derivado "
            + "do outro a tela nao teria como avisar que o vencedor do backtest nao e o que serve");
    }

    /// <summary>
    /// Treino gravado antes deste campo existir tem de continuar abrindo. Nulo aqui
    /// significa "esta execucao nao declarou", e a tela omite o selo em vez de afirmar
    /// um engine que ela nao sabe qual foi.
    /// </summary>
    [Fact]
    public void Resultado_antigo_sem_o_campo_desserializa_com_modelo_salvo_nulo()
    {
        const string antigo = """
            {
              "geradoEm": "1970-01-01T00:00:00+00:00",
              "skusUsados": 10,
              "totalObservacoes": 100,
              "totalFeatures": 90,
              "folds": 4,
              "testWindowDias": 28,
              "engines": [],
              "melhorEngine": "lightgbm"
            }
            """;

        var volta = JsonSerializer.Deserialize<TrainingResult>(antigo, TrainingResultJson.Options)!;

        volta.MelhorEngine.Should().Be("lightgbm");
        volta.ModeloSalvo.Should().BeNull();
    }
}
