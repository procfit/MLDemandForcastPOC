using CosmosPro.ML.DemandForCast.Web;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// A tela não pode declarar vencedor entre dois números que ela mostra iguais.
///
/// <para>
/// O defeito relatado pelo patrocinador em 07/09/2026: MAE exibido como <c>0,08</c> nos dois
/// lados e a frase dizendo "portanto, o seu ERP apresentou menor erro médio". A conta estava
/// certa — o veredito compara os valores crus — e a exibição escondia a diferença em que ele se
/// apoia. Estes testes travam a precisão pelo par, e não por um número fixo de casas: aumentar o
/// padrão para três ou quatro só empurraria a colisão para o próximo par de valores próximos.
/// </para>
/// </summary>
public sealed class ComparacaoFormatoTests
{
    [Fact]
    public void O_caso_relatado_deixa_de_mostrar_dois_numeros_iguais()
    {
        const double pbs = 0.0812;
        const double ml = 0.0849;

        // A premissa do defeito: com duas casas os dois viram o mesmo texto.
        ComparacaoFormato.Unidades(pbs, 2).Should().Be(ComparacaoFormato.Unidades(ml, 2));

        var casas = ComparacaoFormato.CasasParaDistinguir(pbs, ml, ComparacaoFormato.Unidades);

        ComparacaoFormato.Unidades(pbs, casas).Should().NotBe(
            ComparacaoFormato.Unidades(ml, casas),
            "se a tela nomeia um vencedor, a diferença tem de estar visível na mesma linha");
    }

    /// <summary>
    /// A propriedade que interessa, para qualquer par: valores distintos nunca produzem textos
    /// iguais dentro do teto. É o que impede o defeito de voltar num par que ninguém previu.
    /// </summary>
    [Theory]
    [InlineData(0.0812, 0.0849)]
    [InlineData(0.0812, 0.0813)]
    [InlineData(1.0, 1.00004)]
    [InlineData(129.9, 129.90001)]
    [InlineData(0.5, 0.5001)]
    public void Valores_distintos_nunca_viram_textos_iguais(double a, double b)
    {
        var casas = ComparacaoFormato.CasasParaDistinguir(a, b, ComparacaoFormato.Unidades);

        casas.Should().BeInRange(ComparacaoFormato.MinimoDeCasas, ComparacaoFormato.MaximoDeCasas);
        ComparacaoFormato.Unidades(a, casas).Should().NotBe(ComparacaoFormato.Unidades(b, casas));
    }

    [Fact]
    public void Escolhe_a_MENOR_precisao_que_distingue()
    {
        ComparacaoFormato.CasasParaDistinguir(0.0812, 0.0849, ComparacaoFormato.Unidades)
            .Should().Be(3, "0,081 e 0,085 ja se distinguem na terceira casa");

        ComparacaoFormato.CasasParaDistinguir(0.0812, 0.0813, ComparacaoFormato.Unidades)
            .Should().Be(4, "aqui a diferenca so aparece na quarta");
    }

    /// <summary>
    /// Sem par a distinguir, o mínimo: casas extras num empate ou num "não apurado" seriam
    /// ruído, e sugeririam precisão onde não há comparação nenhuma.
    /// </summary>
    [Fact]
    public void Valores_iguais_ou_ausentes_ficam_no_minimo()
    {
        ComparacaoFormato.CasasParaDistinguir(0.08, 0.08, ComparacaoFormato.Unidades)
            .Should().Be(ComparacaoFormato.MinimoDeCasas);
        ComparacaoFormato.CasasParaDistinguir(null, 0.08, ComparacaoFormato.Unidades)
            .Should().Be(ComparacaoFormato.MinimoDeCasas);
        ComparacaoFormato.CasasParaDistinguir(0.08, null, ComparacaoFormato.Unidades)
            .Should().Be(ComparacaoFormato.MinimoDeCasas);
    }

    /// <summary>
    /// Diferença abaixo do que a leitura consegue mostrar para no teto, em vez de escalar sem
    /// limite: passado esse ponto nenhuma decisão de compra usa a diferença.
    /// </summary>
    [Fact]
    public void Diferenca_indistinguivel_para_no_teto()
    {
        ComparacaoFormato.CasasParaDistinguir(0.08, 0.0800000001, ComparacaoFormato.Unidades)
            .Should().Be(ComparacaoFormato.MaximoDeCasas);
    }

    /// <summary>
    /// O WAPE tem o mesmo defeito em potencial, e a mesma correção. <c>P</c> já multiplica por
    /// 100, então a escala de casas é deslocada — o teste existe para a conversão não se perder.
    /// </summary>
    [Fact]
    public void Percentual_tambem_distingue_o_par()
    {
        const double pbs = 1.2988;
        const double ml = 1.2654;

        var casas = ComparacaoFormato.CasasParaDistinguir(pbs, ml, ComparacaoFormato.Percentual);

        ComparacaoFormato.Percentual(pbs, casas).Should().NotBe(
            ComparacaoFormato.Percentual(ml, casas));
        ComparacaoFormato.Percentual(pbs, 2).Should().Contain("%");
    }
}
