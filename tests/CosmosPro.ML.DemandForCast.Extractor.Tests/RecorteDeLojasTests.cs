using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O recorte é o que sustenta a promessa de confidencialidade: se ele deixar passar
/// uma loja, ela sai no ZIP e chega a quem a rede não autorizou.
/// </summary>
public sealed class RecorteDeLojasTests
{
    private static readonly ParLojaSku[] Pares =
    [
        new(10, "111"), new(10, "222"),
        new(20, "222"), new(20, "333"),
        new(30, "444"),
    ];

    [Fact]
    public void Sem_escolha_traz_todas_as_lojas_da_sugestao()
    {
        var recorte = RecorteDeLojas.Aplicar(Pares, escolhidas: null).Value;

        recorte.LojaIds.Should().Equal(10, 20, 30);
        recorte.Skus.Should().BeEquivalentTo(["111", "222", "333", "444"]);
        recorte.LojasNaSugestao.Should().Be(3);
    }

    [Fact]
    public void Escolha_reduz_as_lojas()
    {
        RecorteDeLojas.Aplicar(Pares, [10, 20]).Value.LojaIds.Should().Equal(10, 20);
    }

    [Fact]
    public void Sku_que_so_existia_na_loja_descartada_sai_do_conjunto()
    {
        // O 444 só aparece na loja 30. Sem recalcular os SKUs a partir dos pares
        // filtrados, @skus continuaria pedindo o histórico dele -- dado de uma loja
        // que o comprador acabou de excluir.
        var recorte = RecorteDeLojas.Aplicar(Pares, [10, 20]).Value;

        recorte.Skus.Should().BeEquivalentTo(["111", "222", "333"]);
        recorte.Skus.Should().NotContain("444");
    }

    [Fact]
    public void Sku_compartilhado_sobrevive_se_ao_menos_uma_loja_ficou()
    {
        RecorteDeLojas.Aplicar(Pares, [20]).Value.Skus.Should().BeEquivalentTo(["222", "333"]);
    }

    [Fact]
    public void Total_de_lojas_da_sugestao_nao_muda_com_o_recorte()
    {
        // O manifesto declara "3 de 98": o denominador é a sugestão inteira, sempre.
        RecorteDeLojas.Aplicar(Pares, [10]).Value.LojasNaSugestao.Should().Be(3);
    }

    [Fact]
    public void Lojas_saem_ordenadas_para_o_comando_ser_estavel()
    {
        RecorteDeLojas.Aplicar(Pares, [30, 10]).Value.LojaIds.Should().Equal(10, 30);
    }

    [Fact]
    public void Id_repetido_na_escolha_nao_duplica_parametro()
    {
        RecorteDeLojas.Aplicar(Pares, [10, 10, 20]).Value.LojaIds.Should().Equal(10, 20);
    }

    [Fact]
    public void Lista_vazia_e_recusa_e_nao_significa_todas()
    {
        var resultado = RecorteDeLojas.Aplicar(Pares, []);

        resultado.IsFailed.Should().BeTrue();
        resultado.Errors.Single().Should().BeOfType<LojasNaoSelecionadasErro>();
    }

    [Fact]
    public void Loja_fora_da_sugestao_e_recusada_e_a_mensagem_nomeia_os_ids()
    {
        var resultado = RecorteDeLojas.Aplicar(Pares, [10, 99, 77]);

        resultado.IsFailed.Should().BeTrue();
        var erro = resultado.Errors.Single();
        erro.Should().BeOfType<LojaForaDaSugestaoErro>();
        erro.Message.Should().Contain("99").And.Contain("77");
        erro.Message.Should().NotContain("10");
    }

    [Fact]
    public void Sugestao_sem_par_nenhum_e_recusa()
    {
        RecorteDeLojas.Aplicar([], escolhidas: null).IsFailed.Should().BeTrue();
    }

    [Fact]
    public void Erros_do_recorte_nao_sao_transitorios()
    {
        new LojasNaoSelecionadasErro().Transitorio.Should().BeFalse();
        new LojaForaDaSugestaoErro([1]).Transitorio.Should().BeFalse();
    }
}
