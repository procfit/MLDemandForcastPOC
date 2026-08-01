using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class ExtractionWindowTests
{
    private static readonly DateOnly Hoje = new(2026, 7, 28);

    [Fact]
    public void Janela_cobre_historico_de_treino_antes_e_a_cobertura_depois()
    {
        var j = ExtractionWindow.Derive(
            dataSugestao: new DateOnly(2026, 3, 10), diasCobertura: 30, hoje: Hoje);

        j.Viavel.Should().BeTrue();
        j.Inicio.Should().Be(new DateOnly(2025, 3, 10),
            "12 meses de historico antes de T para o modelo aprender sazonalidade");
        j.Fim.Should().Be(new DateOnly(2026, 4, 9),
            "T + dias de cobertura, para julgar quem acertou");
    }

    [Fact]
    public void Sugestao_recente_demais_e_inviavel_com_motivo()
    {
        var j = ExtractionWindow.Derive(
            dataSugestao: new DateOnly(2026, 7, 25), diasCobertura: 30, hoje: Hoje);

        j.Viavel.Should().BeFalse();
        j.MotivoInviabilidade.Should().Contain("ainda não aconteceram");
    }

    [Fact]
    public void Limite_e_exatamente_hoje_menos_cobertura()
    {
        // A cobertura precisa ter terminado. T + cobertura == hoje ja serve.
        var limite = Hoje.AddDays(-30);

        ExtractionWindow.Derive(limite, 30, Hoje).Viavel.Should().BeTrue();
        ExtractionWindow.Derive(limite.AddDays(1), 30, Hoje).Viavel.Should().BeFalse();
    }
}
