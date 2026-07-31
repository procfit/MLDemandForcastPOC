using CosmosPro.ML.DemandForCast.Purchasing.Comparison;

namespace CosmosPro.ML.DemandForCast.Purchasing.Tests;

public sealed class HumanOverrideReportTests
{
    private static int _proximoId = 1;

    private static HumanOverrideItem Item(
        decimal compraSugerida,
        decimal compraAutorizada,
        decimal? precoCompra = null,
        string? curva = null,
        int lojaId = 1,
        string sku = "SKU1") => new()
        {
            RedeId = 1,
            SugestaoId = _proximoId++,
            LojaId = lojaId,
            Sku = sku,
            Curva = curva,
            CompraSugerida = compraSugerida,
            CompraAutorizada = compraAutorizada,
            PrecoCompra = precoCompra,
        };

    // --- Populacao vazia e sem overrides --------------------------------------

    [Fact]
    public void Populacao_vazia_nao_lanca_e_fracoes_saem_nulas()
    {
        var result = HumanOverrideReport.Compute([]);

        result.ItensNaPopulacao.Should().Be(0);
        result.NaoPonderado.FracaoOverride.Should().BeNull();
        result.NaoPonderado.DesvioRelativoMedioAbsoluto.Should().BeNull();
        result.Ponderado.Should().BeNull();
        result.PorCurva.Should().BeEmpty();
    }

    [Fact]
    public void Sem_nenhum_override_fracao_sai_zero_sem_dividir_por_zero()
    {
        var populacao = new[]
        {
            Item(10m, 10m, precoCompra: 5m),
            Item(20m, 20m, precoCompra: 5m),
            Item(30m, 30m, precoCompra: 5m),
        };

        var result = HumanOverrideReport.Compute(populacao);

        result.NaoPonderado.FracaoOverride.Should().Be(0.0);
        result.NaoPonderado.Vetos.Should().Be(0m);
        result.NaoPonderado.Adicoes.Should().Be(0m);
        result.NaoPonderado.DesvioRelativoMedioAbsoluto.Should().Be(0.0);
        result.NaoPonderado.DesvioRelativoMedioAssinado.Should().Be(0.0);
    }

    // --- Direcao: overrides que se cancelam -----------------------------------

    [Fact]
    public void Overrides_que_se_cancelam_nao_leem_como_ausencia_de_intervencao()
    {
        // +50% numa linha, -50% na outra: o desvio assinado medio fica perto de zero,
        // mas o absoluto tem de continuar em 0,5 - e' o unico jeito de nao confundir
        // "sem intervencao" com "intervencao que se cancela no agregado".
        var populacao = new[]
        {
            Item(100m, 150m, precoCompra: 1m),
            Item(100m, 50m, precoCompra: 1m),
        };

        var result = HumanOverrideReport.Compute(populacao);

        result.NaoPonderado.FracaoOverride.Should().Be(1.0);
        result.NaoPonderado.DesvioRelativoMedioAssinado.Should().BeApproximately(0.0, 1e-9);
        result.NaoPonderado.DesvioRelativoMedioAbsoluto.Should().BeApproximately(0.5, 1e-9);
    }

    // --- Veto e adicao: buckets proprios, sem contaminar ajuste comum ---------

    [Fact]
    public void Veto_e_adicao_contam_no_proprio_bucket_e_nao_como_ajuste_comum()
    {
        var veto = Item(20m, 0m, precoCompra: 2m);
        var adicao = Item(0m, 15m, precoCompra: 2m);
        var ajusteComum = Item(10m, 12m, precoCompra: 2m);

        var result = HumanOverrideReport.Compute([veto, adicao, ajusteComum]);

        result.NaoPonderado.Vetos.Should().Be(1m);
        result.NaoPonderado.Adicoes.Should().Be(1m);
        result.NaoPonderado.AjustesParaCima.Should().Be(1m);
        result.NaoPonderado.AjustesParaBaixo.Should().Be(0m);
        result.NaoPonderado.ComOverride.Should().Be(3m);

        // Adicao (CompraSugerida = 0) nao tem denominador; veto e ajuste comum tem.
        result.NaoPonderado.ComDenominador.Should().Be(2m);
    }

    // --- Ponderacao por valor diverge da nao ponderada ------------------------

    [Fact]
    public void Ponderado_diverge_do_nao_ponderado_quando_override_concentra_em_item_caro()
    {
        var barato = Item(100m, 100m, precoCompra: 1m); // sem override
        var caro = Item(100m, 150m, precoCompra: 1000m); // +50%, valor 1000x maior

        var result = HumanOverrideReport.Compute([barato, caro]);

        result.NaoPonderado.DesvioRelativoMedioAbsoluto.Should().BeApproximately(0.25, 1e-9);

        result.Ponderado.Should().NotBeNull();
        // valor barato = 1*100 = 100; valor caro = 1000*100 = 100_000.
        // media ponderada = (100*0 + 100_000*0.5) / 100_100 ~= 0,4995.
        result.Ponderado!.DesvioRelativoMedioAbsoluto.Should().BeApproximately(0.4995, 0.0005);
        result.Ponderado.DesvioRelativoMedioAbsoluto.Should()
            .BeGreaterThan(result.NaoPonderado.DesvioRelativoMedioAbsoluto!.Value);
    }

    // --- PrecoCompra nulo: excluido do ponderado, contado -----------------------

    [Fact]
    public void Linha_sem_preco_e_excluida_do_ponderado_mas_contada()
    {
        var semPreco = Item(10m, 15m, precoCompra: null);
        var comPreco = Item(10m, 10m, precoCompra: 5m);

        var result = HumanOverrideReport.Compute([semPreco, comPreco]);

        result.ItensSemPreco.Should().Be(1);
        result.Ponderado.Should().NotBeNull();
        result.Ponderado!.Base.Should().Be(50m); // so' o item com preco entra: 5 * 10
        result.Ponderado.ComOverride.Should().Be(0m);
    }

    [Fact]
    public void Populacao_inteira_sem_preco_nao_produz_numero_ponderado_bogus()
    {
        var populacao = new[]
        {
            Item(10m, 15m, precoCompra: null),
            Item(5m, 5m, precoCompra: null),
        };

        var result = HumanOverrideReport.Compute(populacao);

        result.ItensSemPreco.Should().Be(2);
        result.Ponderado.Should().BeNull();
    }

    // --- Quebra por curva --------------------------------------------------------

    [Fact]
    public void Quebra_por_curva_isola_curva_muito_alterada_de_curva_estavel()
    {
        var populacao = new[]
        {
            Item(10m, 20m, precoCompra: 1m, curva: "A"),
            Item(10m, 20m, precoCompra: 1m, curva: "A"),
            Item(10m, 10m, precoCompra: 1m, curva: "B"),
            Item(10m, 10m, precoCompra: 1m, curva: "B"),
        };

        var result = HumanOverrideReport.Compute(populacao);

        result.PorCurva["A"].NaoPonderado.FracaoOverride.Should().Be(1.0);
        result.PorCurva["B"].NaoPonderado.FracaoOverride.Should().Be(0.0);
    }

    [Fact]
    public void Curva_nula_ou_em_branco_cai_no_balde_sem_classificacao()
    {
        var populacao = new[]
        {
            Item(10m, 10m, curva: null),
            Item(10m, 10m, curva: "   "),
            Item(10m, 10m, curva: "A"),
        };

        var result = HumanOverrideReport.Compute(populacao);

        result.PorCurva[HumanOverrideReport.CurvaSemClassificacao].Itens.Should().Be(2);
        result.PorCurva["A"].Itens.Should().Be(1);
    }

    // --- Validacao -----------------------------------------------------------

    [Fact]
    public void Quantidade_negativa_lanca()
    {
        var populacao = new[] { Item(-1m, 0m) };

        var acao = () => HumanOverrideReport.Compute(populacao);

        acao.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Item_duplicado_lanca()
    {
        var item = Item(10m, 10m);
        var duplicado = item with { };

        var acao = () => HumanOverrideReport.Compute([item, duplicado]);

        acao.Should().Throw<ArgumentException>();
    }
}
