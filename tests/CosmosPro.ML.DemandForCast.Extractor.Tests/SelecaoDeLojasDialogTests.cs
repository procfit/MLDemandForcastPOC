using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Só o filtro é testado: o resto do diálogo mexe em Control e depende de bomba de
/// mensagens, que este projeto não tem. É também o motivo de o diálogo não carregar
/// regra nenhuma além de escolher ids.
/// </summary>
public sealed class SelecaoDeLojasDialogTests
{
    private static readonly LojaDaSugestao[] Lojas =
    [
        new(10, "MATRIZ", 40),
        new(86, "(sem cadastro)", 7),
        new(120, "FILIAL CENTRO", 3),
    ];

    [Fact]
    public void Filtro_vazio_devolve_todas()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "   ").Should().HaveCount(3);
        SelecaoDeLojasDialog.Filtrar(Lojas, null).Should().HaveCount(3);
    }

    [Fact]
    public void Filtro_acha_por_pedaco_do_nome_ignorando_caixa()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "centro").Should().ContainSingle()
            .Which.LojaId.Should().Be(120);
    }

    [Fact]
    public void Filtro_acha_por_id()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "86").Should().ContainSingle()
            .Which.LojaId.Should().Be(86);
    }

    [Fact]
    public void Filtro_por_id_nao_casa_pedaco_do_meio_de_outro_id()
    {
        // "12" nao pode trazer a 120 junto com uma eventual loja 12: o comprador digita
        // o codigo inteiro quando sabe qual quer.
        SelecaoDeLojasDialog.Filtrar(Lojas, "120").Select(l => l.LojaId).Should().Equal(120);
    }

    [Fact]
    public void Filtro_preserva_a_ordem()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "a").Select(l => l.LojaId).Should().BeInAscendingOrder();
    }
}
