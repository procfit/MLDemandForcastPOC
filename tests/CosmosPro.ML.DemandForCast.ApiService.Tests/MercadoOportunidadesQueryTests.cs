using CosmosPro.ML.DemandForCast.ApiService.Mercado;

namespace CosmosPro.ML.DemandForCast.ApiService.Tests;

/// <summary>
/// O corte de relevância da regra A2, isolado da consulta para ser afirmado sem banco.
///
/// <para>
/// <b>A2 não é refinamento, é o que torna A1 utilizável.</b> Medido no arquivo real de
/// junho/2026: sem corte, a regra devolve 44.874 avisos, e o comprador fecha a tela e não
/// volta. Com corte de 200 unidades no bairro, sobram 156 avisos e 116 produtos — uma lista
/// que alguém de fato olha.
/// </para>
/// </summary>
public sealed class MercadoOportunidadesQueryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(201, true)]
    public void O_corte_e_inclusivo_no_valor_configurado(int unidades, bool entra)
    {
        MercadoOportunidadesQuery.PassaNoCorte(unidades, corteMinimo: 200m).Should().Be(entra);
    }

    [Fact]
    public void Corte_zero_aceita_qualquer_venda_mas_nao_a_ausencia_de_venda()
    {
        // Corte zero é "sem filtro de relevância", usado por teste e por quem quer a lista
        // bruta. Mas item com zero unidade não é oportunidade: o mercado não vendeu nada
        // dele no bairro, e oferecê-lo seria sugerir cadastrar o que ninguém compra.
        MercadoOportunidadesQuery.PassaNoCorte(0m, corteMinimo: 0m).Should().BeFalse();
        MercadoOportunidadesQuery.PassaNoCorte(1m, corteMinimo: 0m).Should().BeTrue();
    }

    [Fact]
    public void O_corte_padrao_e_duzentas_unidades()
    {
        // Calibrado em junho/2026 para render ~156 avisos e ~116 produtos. Se alguém mexer
        // neste número sem refazer a calibração, a tela volta a ser inutilizável por volume
        // (200 -> 50 dá 1.331 avisos) ou some (200 -> 1000 dá 4).
        MercadoOportunidadesQuery.CorteMinimoPadrao.Should().Be(200m);
    }
}
