using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class ExtractionWindowTests
{
    private static readonly DateOnly Hoje = new(2026, 7, 28);

    /// <summary>
    /// A cobertura destes testes cabe no horizonte do ML de propósito — não porque um valor
    /// maior seria recusado (não é mais: ele extrai com ressalva), mas para que estes casos
    /// falem do que testam (histórico, cobertura terminada, limite exato) sem arrastar junto
    /// a asserção da ressalva, que tem casos próprios abaixo.
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
    /// Cobertura maior que o horizonte do modelo <b>não</b> impede a extração. A recusa que
    /// morava aqui partia de "todo item ficaria fora do horizonte e a comparação sairia
    /// vazia", e isso não é verdade: a camada A pontua <c>min(cobertura, lead time)</c> dias
    /// (<c>ComparacaoProcessor</c>) e sobrevive inteira. Quem cai é só a camada B, que já tem
    /// motivo próprio e texto de comprador do outro lado
    /// (<c>SessaoResultadoMontador.MotivoMlIndisponivel</c>).
    /// <para>
    /// A frase que a recusa economizava custava, na prática, toda sugestão de uma rede cujo
    /// ciclo de reposição é de 15 a 30 dias — que é o ciclo corrente do PBS.
    /// </para>
    /// </summary>
    [Fact]
    public void Cobertura_alem_do_horizonte_do_ml_extrai_com_ressalva()
    {
        var j = ExtractionWindow.Derive(
            new DateOnly(2026, 3, 10), ExtractionWindow.HorizonteMaximoMlDias + 1, Hoje);

        j.Viavel.Should().BeTrue("a camada A pontua os primeiros dias e continua valendo");
        j.MotivoInviabilidade.Should().BeNull();
        j.Ressalva.Should().Contain("em branco",
            "o comprador precisa saber qual coluna vem vazia antes de esperar por ela");
    }

    [Fact]
    public void Cobertura_exatamente_no_horizonte_ainda_e_viavel()
    {
        var j = ExtractionWindow.Derive(
            new DateOnly(2026, 3, 10), ExtractionWindow.HorizonteMaximoMlDias, Hoje);

        j.Viavel.Should().BeTrue("o teto é inclusivo — prever 7 dias é o que o pipeline entrega");
    }

    /// <summary>
    /// Ressalva em toda extração é ressalva que ninguém lê. Dentro do horizonte as duas
    /// camadas produzem número, e não há nada a avisar.
    /// </summary>
    [Fact]
    public void Cobertura_dentro_do_horizonte_nao_traz_ressalva()
    {
        var j = ExtractionWindow.Derive(new DateOnly(2026, 3, 10), CoberturaViavel, Hoje);

        j.Ressalva.Should().BeNull();
    }

    /// <summary>
    /// Janela recusada não acumula ressalva: o comprador tem uma decisão a tomar (escolher
    /// outra sugestão), e um segundo texto ao lado do motivo competiria com ela.
    /// </summary>
    [Fact]
    public void Janela_recusada_nao_traz_ressalva()
    {
        var recenteDemais = ExtractionWindow.Derive(new DateOnly(2026, 7, 25), 30, Hoje);

        recenteDemais.Viavel.Should().BeFalse();
        recenteDemais.Ressalva.Should().BeNull();
    }
}
