using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Comparison;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;

namespace CosmosPro.ML.DemandForCast.Purchasing.Tests;

public sealed class DecisionComparerTests
{
    // Sugestao calculada em 01/03; a janela coberta pela compra sao os DiasEstoque dias
    // a partir dai. Com LeadTimeDias 7 o dia-alvo mais distante licito e 07/03.
    private static readonly DateTime DataHora = new(2025, 3, 1, 8, 0, 0);

    private static readonly DateOnly TreinadoAte = new(2025, 2, 28);

    private const short Dias = 7;

    private static DateOnly Dia(int offset) => new DateOnly(2025, 3, 1).AddDays(offset);

    private static IReadOnlyList<DiaAvaliado> Janela(
        decimal vendaDia, double mlDia, int loja, string sku, int dias = Dias, int diaEmRuptura = -1) =>
        [.. Enumerable.Range(0, dias).Select(i => new DiaAvaliado(
            new FeatureVector
            {
                Data = Dia(i),
                LojaId = loja,
                Sku = sku,
                Target = vendaDia,
                IsValidTarget = i != diaEmRuptura,
                Categoria = "OTC",
                ClasseAbc = "A",
                UF = "SP",
            },
            mlDia))];

    private static DecisionItem Item(
        decimal demandaDiaErp,
        decimal compraSugerida,
        decimal vendaDia,
        double mlDia,
        byte tipoCalculo = 2,
        decimal estoqueSaldo = 3m,
        decimal pedidosPendentes = 1m,
        decimal? estoqueMaximo = null,
        decimal? estoqueSeguranca = null,
        decimal? fatorEmbalagem = null,
        decimal? precoCompra = null,
        short diasEstoque = Dias,
        int diasNaJanela = Dias,
        int diaEmRuptura = -1,
        bool consideraPedidosPendentes = true,
        int loja = 1,
        string sku = "SKU1",
        string curva = "A",
        int redeId = 1,
        long sugestaoId = 1,
        DateOnly? treinadoAte = null) => new()
        {
            RedeId = redeId,
            SugestaoId = sugestaoId,
            DataHora = DataHora,
            ModeloTreinadoAte = treinadoAte ?? TreinadoAte,
            PrecoCongeladoAPartirDe = DateOnly.FromDateTime(DataHora),
            TipoCalculo = tipoCalculo,
            LojaId = loja,
            Sku = sku,
            Curva = curva,
            DemandaDiaErp = demandaDiaErp,
            EstoqueSaldo = estoqueSaldo,
            PedidosPendentes = pedidosPendentes,
            ConsideraPedidosPendentes = consideraPedidosPendentes,
            DiasEstoque = diasEstoque,
            EstoqueMaximo = estoqueMaximo,
            EstoqueSeguranca = estoqueSeguranca,
            CompraSugerida = compraSugerida,
            PrecoCompra = precoCompra,
            FatorEmbalagem = fatorEmbalagem,
            Dias = Janela(vendaDia, mlDia, loja, sku, diasNaJanela, diaEmRuptura),
        };

    // --- Reconciliacao: o portao de validade ---------------------------------

    [Fact]
    public void Reconcilia_item_bem_formado_de_dias_de_reposicao()
    {
        // 2,0/dia x 7 dias = 14; posicao 3 + 1 = 4; compra = 10 = o que o ERP gravou.
        var result = new DecisionComparer().Compare([Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0)]);

        result.Reconciliacao.Itens.Should().Be(1);
        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.Reconciliacao.TaxaConcordancia.Should().Be(1.0);
        result.Reconciliacao.DivergenciaAbsMaxima.Should().Be(0m);
        result.DetalheReconciliacao.Single().Status.Should().Be(StatusReconciliacao.Reconciliado);
        result.ItensComparados.Should().Be(1);
    }

    [Fact]
    public void Item_que_nao_reconcilia_fica_identificavel_e_fora_do_agregado()
    {
        // A formula produz 10 e o ERP gravou 99: nao modelamos a regra dele para esta
        // linha, entao ela nao pode contaminar a comparacao.
        var result = new DecisionComparer().Compare([Item(2m, compraSugerida: 99m, vendaDia: 2m, mlDia: 3.0)]);

        var detalhe = result.DetalheReconciliacao.Single();
        detalhe.Status.Should().Be(StatusReconciliacao.Divergente);
        detalhe.CompraSugeridaErp.Should().Be(99m);
        detalhe.CompraRecalculada.Should().Be(10m);
        detalhe.Divergencia.Should().Be(89m);

        result.Reconciliacao.Reconciliados.Should().Be(0);
        result.Reconciliacao.Divergentes.Should().Be(1);
        result.Reconciliacao.TaxaConcordancia.Should().Be(0.0);
        result.ItensComparados.Should().Be(0);
        result.Detalhe.Should().BeEmpty();
        result.Vitoria.N.Should().Be(0);
    }

    [Fact]
    public void Divergencia_dentro_da_tolerancia_reconcilia()
    {
        var result = new DecisionComparer().Compare([Item(2m, compraSugerida: 10.0005m, vendaDia: 2m, mlDia: 2.0)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
    }

    [Fact]
    public void Reconciliacao_cobre_toda_a_populacao_inclusive_o_que_a_ruptura_descarta()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, diaEmRuptura: 3)]);

        result.Reconciliacao.Itens.Should().Be(1);
        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.ItensDescartadosPorRuptura.Should().Be(1);
        result.ItensComparados.Should().Be(0);
    }

    // --- Troca so a demanda --------------------------------------------------

    [Fact]
    public void Previsao_maior_do_ml_gera_quantidade_maior_e_o_excesso_sai_contra_a_venda_real()
    {
        // ERP 2,0/dia -> compra 10; ML 3,0/dia -> 21 - 4 = 17. Venda real 3,0/dia x 7 = 21.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 3m, mlDia: 3.0, precoCompra: 2.5m)]);

        var item = result.Detalhe.Single();
        item.CompraErp.Should().Be(10m);
        item.CompraMl.Should().Be(17m);
        item.CompraMl.Should().BeGreaterThan(item.CompraErp);
        item.VendaRealJanela.Should().Be(21m);

        // ERP: posicao 4 + 10 = 14 contra 21 vendidos -> faltam 7.
        item.FaltaErp.Should().Be(7m);
        item.ExcessoErp.Should().Be(0m);
        // ML: posicao 4 + 17 = 21 -> serve exatamente.
        item.FaltaMl.Should().Be(0m);
        item.ExcessoMl.Should().Be(0m);
        item.Resultado.Should().Be(ResultadoPar.VitoriaMl);

        result.Erp.FaltaUnidades.Should().Be(7m);
        result.Erp.VendaPerdida.Unidades.Should().Be(7m);
        result.Erp.VendaPerdida.Valor.Should().Be(17.5m);
        result.Ml.FaltaUnidades.Should().Be(0m);
        result.Vitoria.VitoriasMl.Should().Be(1);
        result.Vitoria.TaxaVitoriaMl.Should().Be(1.0);
    }

    [Fact]
    public void Erp_vence_quando_a_decisao_dele_serve_melhor_a_venda_real()
    {
        // ML exagera (6,0/dia -> compra 38) e a venda real fica em 2,0/dia x 7 = 14,
        // exatamente o que a posicao do ERP cobre.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 6.0, precoCompra: 2m)]);

        var item = result.Detalhe.Single();
        item.CompraMl.Should().Be(38m);
        item.ExcessoErp.Should().Be(0m);
        item.FaltaErp.Should().Be(0m);
        item.ExcessoMl.Should().Be(28m);
        item.Resultado.Should().Be(ResultadoPar.VitoriaErp);

        result.Ml.ExcessoUnidades.Should().Be(28m);
        result.Ml.ExcessoValor.Should().Be(56m);
        result.Ml.ValorComprado.Should().Be(76m);
        result.Vitoria.VitoriasErp.Should().Be(1);
        result.Vitoria.TaxaVitoriaMl.Should().Be(0.0);
    }

    [Fact]
    public void Previsoes_iguais_dao_empate_e_empate_nao_e_vitoria()
    {
        var result = new DecisionComparer().Compare([Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0)]);

        result.Detalhe.Single().Resultado.Should().Be(ResultadoPar.Empate);
        result.Vitoria.Should().BeEquivalentTo(new WinRate(1, 0, 0, 1));
    }

    [Fact]
    public void Posicao_de_estoque_e_pedidos_pendentes_sao_os_mesmos_nos_dois_bracos()
    {
        // Sem considerar pendentes a posicao cai para 3 e as duas compras sobem 1.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 11m, vendaDia: 2m, mlDia: 3.0, consideraPedidosPendentes: false)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        var item = result.Detalhe.Single();
        item.CompraErp.Should().Be(11m);
        item.CompraMl.Should().Be(18m);
    }

    // --- Embalagem -----------------------------------------------------------

    [Fact]
    public void Arredondamento_de_embalagem_se_aplica_igual_nos_dois_bracos()
    {
        // ERP 1,9/dia -> 13,3 - 4 = 9,3 -> 10 (multiplo de 5).
        // ML  2,1/dia -> 14,7 - 4 = 10,7 -> 15.
        var result = new DecisionComparer().Compare(
            [Item(1.9m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.1, fatorEmbalagem: 5m)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        var item = result.Detalhe.Single();
        item.CompraErp.Should().Be(10m);
        item.CompraMl.Should().Be(15m);
        (item.CompraErp % 5m).Should().Be(0m);
        (item.CompraMl % 5m).Should().Be(0m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void Fator_de_embalagem_nulo_ou_zero_nao_divide_por_zero(double? fator)
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0, fatorEmbalagem: (decimal?)fator)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.Detalhe.Single().CompraErp.Should().Be(10m);
        result.Detalhe.Single().CompraMl.Should().Be(17m);
    }

    // --- Os dois tipos de calculo -------------------------------------------

    [Fact]
    public void Dias_de_reposicao_com_eSeg_e_eMax_zerados_e_processado_normalmente()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 2, estoqueMaximo: 0m, estoqueSeguranca: 0m)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.ItensComparados.Should().Be(1);
    }

    [Fact]
    public void Emax_eseg_repoe_ate_o_estoque_maximo_gravado()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 2.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: 6m)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.Detalhe.Single().CompraErp.Should().Be(16m);
    }

    [Fact]
    public void Emax_eseg_reescala_o_estoque_maximo_pela_demanda_do_ml()
    {
        // eMax 20 a 2,0/dia; o ML preve 3,0/dia -> eMax equivalente 30 -> compra 26.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: 6m)]);

        result.Detalhe.Single().CompraMl.Should().Be(26m);
    }

    [Fact]
    public void Emax_eseg_com_demanda_do_erp_zerada_marca_braco_ml_indeterminado()
    {
        var result = new DecisionComparer().Compare(
            [Item(0m, compraSugerida: 0m, vendaDia: 1m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 0m, estoqueSeguranca: 0m)]);

        result.DetalheReconciliacao.Single().Status.Should().Be(StatusReconciliacao.BracoMlIndeterminado);
        result.Reconciliacao.BracoMlIndeterminado.Should().Be(1);
        result.ItensComparados.Should().Be(0);
    }

    [Fact]
    public void Emax_eseg_sem_estoque_maximo_estoura()
    {
        var acao = () => new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 2.0, tipoCalculo: 1, estoqueMaximo: null)]);

        acao.Should().Throw<ArgumentException>().WithMessage("*EstoqueMaximo*");
    }

    [Fact]
    public void Tipo_de_calculo_nao_modelado_estoura_em_vez_de_produzir_numero()
    {
        var acao = () => new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, tipoCalculo: 3)]);

        acao.Should().Throw<ArgumentException>().WithMessage("*TipoCalculo*");
    }

    [Fact]
    public void Populacao_que_mistura_tipos_de_calculo_estoura()
    {
        var acao = () => new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, tipoCalculo: 2),
            Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 2.0, tipoCalculo: 1,
                 estoqueMaximo: 20m, sku: "SKU2"),
        ]);

        acao.Should().Throw<ArgumentException>().WithMessage("*TipoCalculo*");
    }

    [Fact]
    public void Populacao_que_mistura_redes_estoura()
    {
        var acao = () => new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0),
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, redeId: 2, sku: "SKU2"),
        ]);

        acao.Should().Throw<ArgumentException>().WithMessage("*rede*");
    }

    [Fact]
    public void Item_repetido_na_populacao_estoura()
    {
        var acao = () => new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0),
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0),
        ]);

        acao.Should().Throw<ArgumentException>();
    }

    // --- Ruptura: a politica da camada A ------------------------------------

    [Fact]
    public void Ruptura_na_janela_descarta_o_item_inteiro_por_padrao()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0, diaEmRuptura: 0)]);

        result.ItensDescartadosPorRuptura.Should().Be(1);
        result.ItensComparados.Should().Be(0);
        result.Detalhe.Should().BeEmpty();
    }

    [Fact]
    public void Incluir_ruptura_e_sensibilidade_e_pontua_a_venda_observada()
    {
        var opt = new DecisionOptions { Ruptura = RupturaTratamento.Incluir };
        var result = new DecisionComparer(opt).Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0, diaEmRuptura: 0)]);

        result.ItensComparados.Should().Be(1);
        result.Detalhe.Single().VendaRealJanela.Should().Be(14m);
    }

    [Fact]
    public void Excluir_dia_nao_e_suportado_na_camada_de_decisao()
    {
        var acao = () => new DecisionComparer(new DecisionOptions { Ruptura = RupturaTratamento.ExcluirDia });

        acao.Should().Throw<ArgumentException>().WithMessage("*ExcluirDia*");
    }

    // --- Regras herdadas da camada A ----------------------------------------

    [Fact]
    public void Modelo_treinado_ate_data_nao_anterior_a_sugestao_estoura()
    {
        var acao = () => new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0,
                  treinadoAte: DateOnly.FromDateTime(DataHora))]);

        acao.Should().Throw<ArgumentException>().WithMessage("*informação*");
    }

    [Fact]
    public void Janela_com_menos_dias_que_a_cobertura_da_compra_estoura()
    {
        // Comprar para 7 dias e pontuar contra a venda de 5 mostraria excesso nos dois
        // bracos so por causa do recorte.
        var acao = () => new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, diasNaJanela: 5)]);

        acao.Should().Throw<ArgumentException>().WithMessage("*DiasEstoque*");
    }

    [Fact]
    public void Previsao_nao_finita_do_ml_estoura()
    {
        var acao = () => new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: double.NaN)]);

        acao.Should().Throw<ArgumentException>();
    }

    // --- Agregados -----------------------------------------------------------

    [Fact]
    public void Resultado_quebra_a_taxa_de_vitoria_por_curva_e_por_loja()
    {
        var result = new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 3m, mlDia: 3.0, curva: "A", loja: 1),
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 6.0, curva: "C", loja: 2, sku: "SKU2"),
        ]);

        result.ItensComparados.Should().Be(2);
        result.VitoriaPorDimensao["CurvaErp"]["A"].VitoriasMl.Should().Be(1);
        result.VitoriaPorDimensao["CurvaErp"]["C"].VitoriasErp.Should().Be(1);
        result.VitoriaPorDimensao["Loja"]["2"].VitoriasErp.Should().Be(1);
    }

    [Fact]
    public void Item_sem_preco_de_compra_e_contado_a_parte()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 3m, mlDia: 3.0, precoCompra: null)]);

        result.ItensSemPrecoCompra.Should().Be(1);
        result.Erp.VendaPerdida.Valor.Should().Be(0m);
        result.Erp.FaltaUnidades.Should().Be(7m);
    }

    [Fact]
    public void Metricas_de_posicao_contra_venda_reaproveitam_o_ForecastMetrics()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 3m, mlDia: 3.0)]);

        result.Erp.PosicaoVsVenda.N.Should().Be(1);
        result.Erp.PosicaoVsVenda.Mae.Should().BeApproximately(7.0, 1e-9);
        result.Erp.PosicaoVsVenda.Wape.Should().BeApproximately(7.0 / 21.0, 1e-9);
        result.Ml.PosicaoVsVenda.Mae.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Populacao_vazia_devolve_resultado_vazio_sem_estourar()
    {
        var result = new DecisionComparer().Compare([]);

        result.ItensNaPopulacao.Should().Be(0);
        result.ItensComparados.Should().Be(0);
        result.Reconciliacao.TaxaConcordancia.Should().Be(0.0);
        result.VitoriaPorDimensao.Should().BeEmpty();
    }
}
