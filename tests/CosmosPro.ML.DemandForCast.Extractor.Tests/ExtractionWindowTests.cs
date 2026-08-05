using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class ExtractionWindowTests
{
    private static readonly DateOnly Hoje = new(2026, 7, 28);

    /// <summary>
    /// A cobertura destes testes cabe no horizonte do ML de propósito. Eles usavam 30 dias,
    /// que era a cobertura típica quando ela era lida do <c>DIAS_CURVA</c> do cabeçalho — e
    /// 30 excede os 7 dias que o pipeline prevê, então a janela agora recusa. Usar um valor
    /// dentro do horizonte é o que mantém estes casos falando sobre o que eles testam
    /// (histórico, cobertura terminada, limite exato) em vez de baterem no teto.
    /// </summary>
    private const int CoberturaViavel = 5;

    [Fact]
    public void Janela_cobre_historico_de_treino_antes_e_a_cobertura_depois()
    {
        var j = ExtractionWindow.Derive(
            dataSugestao: new DateOnly(2026, 3, 10), diasCobertura: CoberturaViavel, hoje: Hoje);

        j.Viavel.Should().BeTrue();
        j.Inicio.Should().Be(new DateOnly(2025, 3, 10),
            "12 meses de historico antes de T para o modelo aprender sazonalidade");
        j.Fim.Should().Be(new DateOnly(2026, 3, 15),
            "T + dias de cobertura, para julgar quem acertou");
    }

    [Fact]
    public void Sugestao_recente_demais_e_inviavel_com_motivo()
    {
        var j = ExtractionWindow.Derive(
            dataSugestao: new DateOnly(2026, 7, 25), diasCobertura: CoberturaViavel, hoje: Hoje);

        j.Viavel.Should().BeFalse();
        j.MotivoInviabilidade.Should().Contain("ainda não aconteceram");
    }

    [Fact]
    public void Limite_e_exatamente_hoje_menos_cobertura()
    {
        // A cobertura precisa ter terminado. T + cobertura == hoje ja serve.
        var limite = Hoje.AddDays(-CoberturaViavel);

        ExtractionWindow.Derive(limite, CoberturaViavel, Hoje).Viavel.Should().BeTrue();
        ExtractionWindow.Derive(limite.AddDays(1), CoberturaViavel, Hoje).Viavel.Should().BeFalse();
    }

    /// <summary>
    /// Aconteceu em produção: a sugestão 21682 (eMax/eSeg) tinha os cinco
    /// <c>DIAS_CURVA_*</c> zerados, a janela terminou no próprio dia da sugestão e o extrator
    /// seguiu — 879 MB, 309 milhões de linhas de estoque diário, e nenhum dia posterior contra
    /// o qual pontuar quem acertou.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Cobertura_nao_positiva_e_inviavel_porque_nao_existe_gabarito(int cobertura)
    {
        var j = ExtractionWindow.Derive(new DateOnly(2026, 3, 10), cobertura, Hoje);

        j.Viavel.Should().BeFalse();
        j.MotivoInviabilidade.Should().Contain("não declara dias de cobertura");
    }

    /// <summary>
    /// Cobertura maior que o horizonte do modelo não é erro do ERP: é sugestão que o nosso
    /// lado não sabe pontuar. Recusar na seleção troca a extração inteira — minutos, centenas
    /// de MB e um import — por uma frase.
    /// </summary>
    [Fact]
    public void Cobertura_alem_do_horizonte_do_ml_e_inviavel_com_motivo()
    {
        var j = ExtractionWindow.Derive(
            new DateOnly(2026, 3, 10), ExtractionWindow.HorizonteMaximoMlDias + 1, Hoje);

        j.Viavel.Should().BeFalse();
        j.MotivoInviabilidade.Should().Contain("modelo prevê no máximo");
    }

    [Fact]
    public void Cobertura_exatamente_no_horizonte_ainda_e_viavel()
    {
        var j = ExtractionWindow.Derive(
            new DateOnly(2026, 3, 10), ExtractionWindow.HorizonteMaximoMlDias, Hoje);

        j.Viavel.Should().BeTrue("o teto é inclusivo — prever 7 dias é o que o pipeline entrega");
    }
}
