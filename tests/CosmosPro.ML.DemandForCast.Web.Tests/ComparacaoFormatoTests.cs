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
    /// <summary>
    /// Relatado pelo patrocinador em 27/09/2026 nos quadros da HYPERA e da EMS: os dois MAE
    /// aparecem diferentes na linha e a diferença ao lado sai como <b>0,00</b>.
    ///
    /// <para>
    /// É o efeito colateral da correção de 07/09: a precisão passou a ser derivada do PAR, e
    /// aplicada à DIFERENÇA — que é, por construção, menor que a distância entre os dois
    /// valores. HYPERA: 0,084892 e 0,087327 já se distinguem com 2 casas, logo a diferença
    /// (0,002435) foi formatada com 2 casas e virou "0,00".
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.084892, 0.087327)]   // HYPERA 126122
    [InlineData(0.183260, 0.186788)]   // EMS 126479
    // A EXELTIS 125700 NAO entra aqui de proposito: a diferenca dela (0,006190) aparecia
    // como "0,01", nao como "0,00" -- a premissa deste teste nao vale para ela. O que ela
    // exercita e a magnitude, e esta em Diferenca_sai_com_dois_algarismos_significativos.
    public void Diferenca_visivel_nunca_e_exibida_como_zero(double pbs, double ml)
    {
        var diferenca = Math.Abs(pbs - ml);

        // A premissa do defeito: a precisão que distingue o par zera a diferença.
        var casasDoPar = ComparacaoFormato.CasasParaDistinguir(pbs, ml, ComparacaoFormato.Unidades);
        ComparacaoFormato.Unidades(diferenca, casasDoPar).Should().Be(
            ComparacaoFormato.Unidades(0, casasDoPar), "é este o defeito que se corrige");

        var casas = ComparacaoFormato.CasasParaDiferenca(diferenca);

        ComparacaoFormato.Unidades(diferenca, casas).Should().NotBe(
            ComparacaoFormato.Unidades(0, casas),
            "uma diferença que existe não pode ser exibida como zero");
    }

    /// <summary>Diferença de facto nula continua sendo zero — não se inventa precisão.</summary>
    [Fact]
    public void Diferenca_nula_fica_no_minimo_de_casas()
    {
        ComparacaoFormato.CasasParaDiferenca(0)
            .Should().Be(ComparacaoFormato.MinimoDeCasas);
    }

    /// <summary>
    /// A diferença é exibida com DOIS algarismos significativos. Só "deixar de ser zero"
    /// arredondaria 0,006190 para 0,01 — 62% de erro num número que sustenta o veredito.
    ///
    /// <para>
    /// A asserção é sobre o NÚMERO DE CASAS, e não sobre o texto formatado: o separador
    /// decimal depende da cultura (vírgula aqui, ponto no runner invariante do CI), e um
    /// literal com vírgula passa na máquina do desenvolvedor e quebra no CI. Mesma
    /// armadilha anotada em <c>ComparisonApiClientTests</c>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.006190, 4)]   // EXELTIS 125700 — o caso que "so nao ser zero" estragava
    [InlineData(0.002435, 4)]   // HYPERA 126122
    [InlineData(0.003528, 4)]   // EMS 126479
    [InlineData(0.05, 3)]
    [InlineData(1.5, 2)]        // piso: dois algarismos ja cabem no minimo de casas
    [InlineData(1e-9, 6)]       // teto: precisao alem disso nao serve a leitura
    public void Diferenca_sai_com_dois_algarismos_significativos(double diferenca, int casasEsperadas)
    {
        ComparacaoFormato.CasasParaDiferenca(diferenca).Should().Be(casasEsperadas);
    }

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
