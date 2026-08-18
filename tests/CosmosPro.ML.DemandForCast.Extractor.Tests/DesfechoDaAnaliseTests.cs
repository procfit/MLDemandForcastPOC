using System.Globalization;
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O que a tela mostra depois de analisar a sugestão selecionada. Vive fora do
/// <c>MainForm</c> porque é a regra que decide se o botão Extrair liga — e um
/// <c>Form</c> não é instanciável num teste.
/// </summary>
public sealed class DesfechoDaAnaliseTests
{
    private static readonly DateOnly Hoje = new(2026, 7, 28);
    private static readonly DateOnly Sugestao = new(2026, 3, 10);

    private static SugestaoContagem Contagem(int diasCobertura) =>
        new(SugestaoId: 42, QtdLinhas: 12_345, QtdLojas: 30, DiasCoberturaMax: diasCobertura);

    private static DesfechoDaAnalise Analisar(int diasCobertura) =>
        DesfechoDaAnalise.De(
            Contagem(diasCobertura),
            ExtractionWindow.Derive(Sugestao, diasCobertura, Hoje));

    /// <summary>
    /// A contagem sai com o separador de milhar da <b>cultura corrente</b>, e isso é o
    /// certo: é rótulo de tela para um comprador brasileiro, não CSV — a cultura invariante
    /// é obrigação do <c>CsvZipWriter</c>, cujo leitor do outro lado é o Worker.
    /// <para>
    /// Roda nas duas culturas de propósito. A asserção nasceu escrita com o ponto do pt-BR
    /// da máquina de quem a escreveu, passou local e <b>quebrou o pipeline em main</b>, onde
    /// o runner é en-US e o separador é vírgula. Fixar a cultura torna o teste determinístico;
    /// rodar nas duas impede que a próxima asserção de texto formatado volte a depender de
    /// onde ela foi escrita.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("pt-BR", "12.345 itens")]
    [InlineData("en-US", "12,345 itens")]
    public void Dentro_do_horizonte_o_rotulo_traz_itens_janela_e_cobertura_sem_aviso(
        string cultura, string itensEsperados)
    {
        var anterior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultura);
        try
        {
            var d = Analisar(5);

            d.PodeExtrair.Should().BeTrue();
            d.Sinal.Should().Be(SinalDaAnalise.Viavel);
            d.Rotulo.Should().Contain(itensEsperados).And.Contain("30 loja(s)")
                .And.Contain("10/03/2025").And.Contain("15/03/2026")
                .And.Contain("cobertura 5 dia(s)");
            d.Aviso.Should().BeNull();
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }

    /// <summary>
    /// O ponto da mudança: cobertura longa continua extraível. Se este teste virar
    /// vermelho, o comprador de uma rede com ciclo de 30 dias voltou a não conseguir
    /// exportar nenhuma sugestão.
    /// </summary>
    [Fact]
    public void Cobertura_longa_mantem_o_extrair_ligado_e_devolve_o_aviso_por_extenso()
    {
        var d = Analisar(30);

        d.PodeExtrair.Should().BeTrue();
        d.Sinal.Should().Be(SinalDaAnalise.Ressalva);
        d.Aviso.Should().Contain("em branco");
    }

    /// <summary>
    /// O aviso não entra no rótulo: ele tem 320×30 e o texto de itens + janela +
    /// cobertura já ocupa as duas linhas. Sai pelo log, que tem espaço e vai também
    /// para o arquivo do dia.
    /// </summary>
    [Fact]
    public void O_aviso_fica_fora_do_rotulo_que_nao_tem_espaco_para_ele()
    {
        var d = Analisar(30);

        d.Rotulo.Should().NotContain("em branco");
        d.Rotulo.Should().Contain("cobertura 30 dia(s)");
    }

    [Fact]
    public void Recusa_desliga_o_extrair_e_poe_o_motivo_no_rotulo()
    {
        var d = DesfechoDaAnalise.De(
            Contagem(0), ExtractionWindow.Derive(Sugestao, 0, Hoje));

        d.PodeExtrair.Should().BeFalse();
        d.Sinal.Should().Be(SinalDaAnalise.Recusa);
        d.Rotulo.Should().Contain("não declara dias de cobertura");
        d.Aviso.Should().BeNull();
    }
}
