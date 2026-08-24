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
        decimal vendaDia, double mlDia, int loja, string sku, int dias = Dias, int diaEmRuptura = -1,
        Func<int, double>? mlPorDia = null) =>
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
            mlPorDia?.Invoke(i) ?? mlDia))];

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
        DateOnly? treinadoAte = null,
        Func<int, double>? mlPorDia = null) => new()
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
            Dias = Janela(vendaDia, mlDia, loja, sku, diasNaJanela, diaEmRuptura, mlPorDia),
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
    public void Divergencia_logo_acima_da_tolerancia_e_divergente()
    {
        // 0,002 contra tolerancia 0,001: o portao tem de fechar no primeiro passo alem
        // dela, nao so quando a divergencia e grosseira.
        var result = new DecisionComparer().Compare([Item(2m, compraSugerida: 10.002m, vendaDia: 2m, mlDia: 2.0)]);

        result.Reconciliacao.Divergentes.Should().Be(1);
        result.DetalheReconciliacao.Single().Status.Should().Be(StatusReconciliacao.Divergente);
    }

    [Fact]
    public void Divergencia_media_olha_so_os_itens_que_divergem()
    {
        // Dois reconciliam e um erra por 89. Media sobre os tentados daria ~29,67 e
        // sugeriria erro pequeno espalhado; a resposta certa e "quando erra, erra 89".
        var result = new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, sku: "SKU1"),
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, sku: "SKU2"),
            Item(2m, compraSugerida: 99m, vendaDia: 2m, mlDia: 2.0, sku: "SKU3"),
        ]);

        result.Reconciliacao.DivergenciaAbsMedia.Should().Be(89m);
        result.Reconciliacao.DivergenciaAbsMaxima.Should().Be(89m);
    }

    [Fact]
    public void Reconciliacao_expoe_curva_fator_e_diferenca_assinada()
    {
        // 1,5/dia x 7 = 10,5 - 4 = 6,5. Nossa formula arredonda para cima: 10. Um ERP que
        // arredondasse para o multiplo mais proximo gravaria 5. A assinatura dessa hipotese
        // e divergencia de um lado so e de no maximo um pacote.
        var result = new DecisionComparer().Compare(
            [Item(1.5m, compraSugerida: 5m, vendaDia: 2m, mlDia: 2.0, fatorEmbalagem: 5m, curva: "B")]);

        var detalhe = result.DetalheReconciliacao.Single();
        detalhe.Curva.Should().Be("B");
        detalhe.FatorEmbalagem.Should().Be(5m);
        detalhe.CompraRecalculada.Should().Be(10m);
        detalhe.DiferencaAssinada.Should().Be(5m);
        detalhe.Divergencia.Should().Be(5m);
        detalhe.DiferencaAssinada.Should().BePositive();
        detalhe.DiferencaAssinada.Should().BeLessThanOrEqualTo(detalhe.FatorEmbalagem!.Value);
    }

    [Fact]
    public void Reconciliacao_quebra_por_curva_expondo_sobrevivencia_seletiva()
    {
        // A taxa global de 50% nao diz que a curva C inteira ficou de fora; a quebra diz.
        var result = new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, curva: "A", sku: "SKU1"),
            Item(2m, compraSugerida: 99m, vendaDia: 2m, mlDia: 2.0, curva: "C", sku: "SKU2"),
        ]);

        result.Reconciliacao.TaxaConcordancia.Should().Be(0.5);
        result.Reconciliacao.PorCurva["A"].TaxaConcordancia.Should().Be(1.0);
        result.Reconciliacao.PorCurva["A"].DivergenciaAbsMedia.Should().Be(0m);
        result.Reconciliacao.PorCurva["C"].TaxaConcordancia.Should().Be(0.0);
        result.Reconciliacao.PorCurva["C"].Divergentes.Should().Be(1);
        result.Reconciliacao.PorCurva["C"].DivergenciaAbsMedia.Should().Be(89m);
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
        // Sem considerar pendentes a posicao de COMPRA cai para 3 e as duas compras sobem 1.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 11m, vendaDia: 2m, mlDia: 3.0, consideraPedidosPendentes: false)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        var item = result.Detalhe.Single();
        item.CompraErp.Should().Be(11m);
        item.CompraMl.Should().Be(18m);
    }

    [Fact]
    public void Pendentes_ignorados_na_compra_continuam_contando_no_desfecho()
    {
        // A flag governa a aritmetica da compra, nao a pontuacao: 1 unidade em transito
        // chega e atende a janela. Venda real 14; ERP compra 11 e fica com 3 + 1 + 11 = 15,
        // logo sobra 1 — nao zero. Ignorar o pendente tambem aqui esconderia compra em
        // excesso nos dois bracos.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 11m, vendaDia: 2m, mlDia: 3.0, consideraPedidosPendentes: false)]);

        var item = result.Detalhe.Single();
        item.VendaRealJanela.Should().Be(14m);
        item.ExcessoErp.Should().Be(1m);
        item.ExcessoMl.Should().Be(8m);
        result.Erp.ExcessoUnidades.Should().Be(1m);
        result.Ml.ExcessoUnidades.Should().Be(8m);
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
    public void Dias_de_reposicao_ignora_o_estoque_de_seguranca()
    {
        // O eSeg entra so no tipo 1; no tipo 2 a formula continua demanda x dias.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 2, estoqueMaximo: 20m, estoqueSeguranca: 6m)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.Detalhe.Single().CompraMl.Should().Be(17m);
    }

    [Fact]
    public void Emax_eseg_reescala_so_a_parcela_acima_do_estoque_de_seguranca()
    {
        // eMax 20 com eSeg 6 a 2,0/dia; o ML preve 3,0/dia (r = 1,5). Reescalar so a
        // parcela variavel da 6 + 14 x 1,5 = 27 -> compra 23. Reescalar o nivel inteiro
        // daria 30 e amplificaria a discordancia do ML em eSeg x (r - 1) = 3 unidades.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: 6m)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.Detalhe.Single().CompraErp.Should().Be(16m);
        result.Detalhe.Single().CompraMl.Should().Be(23m);
    }

    [Fact]
    public void Emax_eseg_no_modo_proporcional_reescala_o_nivel_inteiro()
    {
        // A hipotese alternativa, mantida explicita para reportar a sensibilidade:
        // 20 x 1,5 = 30 -> compra 26.
        var opt = new DecisionOptions { ReescalaTipo1 = ReescalaEstoqueMaximo.Proporcional };
        var result = new DecisionComparer(opt).Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: 6m)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.Detalhe.Single().CompraMl.Should().Be(26m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void Emax_eseg_sem_estoque_de_seguranca_recai_na_reescala_proporcional(double? eSeg)
    {
        // Sem componente fixo declarado as duas hipoteses coincidem: 20 x 1,5 = 30 -> 26.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: (decimal?)eSeg)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
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

    // --- Horizonte do braco ML ----------------------------------------------

    [Theory]
    [InlineData((short)15, 26)]
    [InlineData((short)30, 56)]
    public void Cobertura_alem_do_horizonte_extrapola_a_taxa_em_vez_de_excluir_o_item(short dias, int compra)
    {
        // Coberturas de 15 e 30 dias sao correntes no PBS e o pipeline preve 7. Antes o item
        // era excluido e a camada terminava vazia — era isso que fazia a tela dizer "sem
        // calculo do ML" nas 20.153 linhas. Agora a taxa dos 7 dias validos alimenta a MESMA
        // multiplicacao que o ERP faz pela cobertura (demandaDia x DiasEstoque), e o
        // esticamento e reportado em vez de escondido.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: compra, vendaDia: 2m, mlDia: 2.0,
                  diasEstoque: dias, diasNaJanela: dias)]);

        result.Reconciliacao.Reconciliados.Should().Be(1);
        result.ItensComparados.Should().Be(1);
        result.ForaDoHorizonteMl.Should().BeEmpty("cobertura longa deixou de excluir item");

        result.ItensComTaxaExtrapolada.Should().Be(1);
        result.DiasDeTaxaMinimo.Should().Be(7);
        result.FatorMaximoDeExtrapolacao.Should().BeApproximately((decimal)dias / 7m, 0.001m);

        // Taxa do ML igual a do ERP produz a MESMA quantidade: e o que prova que a
        // extrapolacao passa pela formula do ERP, e nao por uma conta paralela.
        result.Detalhe.Single().CompraMl.Should().Be(compra);
        result.MotivoTaxaExtrapolada.Should().NotBeNull();
    }

    [Fact]
    public void A_taxa_do_ml_ignora_a_previsao_dos_dias_alem_do_horizonte()
    {
        // O ponto sensivel da extrapolacao. Os dias alem do horizonte TEM previsao no item —
        // o chamador monta a janela inteira —, e ela e alimentada por observacao posterior ao
        // corte. Se entrarem na taxa, a regra de informacao cai por dentro: sem excecao, sem
        // aviso, com numero plausivel na tela. Aqui os 8 ultimos dias preveem 1000/dia; se
        // vazarem para a taxa, a compra do ML explode em vez de dar 26.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 26m, vendaDia: 2m, mlDia: 2.0,
                  diasEstoque: 15, diasNaJanela: 15,
                  mlPorDia: i => i < 7 ? 2.0 : 1000.0)]);

        result.ItensComparados.Should().Be(1);
        result.Detalhe.Single().CompraMl.Should().Be(26m,
            "a taxa sai dos 7 primeiros dias (2,0/dia x 15 dias - 4 de posicao), nao dos 1000/dia dos dias que violam a regra de informacao");
    }

    [Fact]
    public void Sem_item_fora_do_horizonte_o_motivo_nao_e_inventado()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0)]);

        result.ForaDoHorizonteMl.Should().BeEmpty();
        result.MotivoForaDoHorizonteMl.Should().BeNull(
            "sem item recusado nao ha horizonte declarado a citar, e um texto fixo aqui afirmaria o que este resultado nao sabe");
    }

    [Fact]
    public void Item_dentro_e_item_alem_do_horizonte_convivem_e_so_um_conta_como_extrapolado()
    {
        var result = new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0, sku: "SKU1"),
            Item(2m, compraSugerida: 26m, vendaDia: 2m, mlDia: 3.0, sku: "SKU2",
                 diasEstoque: 15, diasNaJanela: 15),
        ]);

        result.ItensComparados.Should().Be(2);
        result.ItensComTaxaExtrapolada.Should().Be(1, "SKU1 cobre exatamente o horizonte");
        result.FatorMaximoDeExtrapolacao.Should().BeApproximately(15m / 7m, 0.001m);
    }

    [Fact]
    public void Horizonte_declarado_maior_permite_comparar_a_cobertura_inteira()
    {
        // Contrato satisfeito: previsao de 15 dias a frente exige features com lead time 15.
        // 2,0/dia x 15 = 30 - 4 = 26 no ERP; o ML preve 3,0/dia -> 45 - 4 = 41.
        var opt = new DecisionOptions { HorizonteMaximoMl = 15, LeadTimeDias = 15 };
        var result = new DecisionComparer(opt).Compare(
            [Item(2m, compraSugerida: 26m, vendaDia: 2m, mlDia: 3.0,
                  diasEstoque: 15, diasNaJanela: 15)]);

        result.ForaDoHorizonteMl.Should().BeEmpty();
        result.ItensComparados.Should().Be(1);
        var item = result.Detalhe.Single();
        item.DiasAvaliados.Should().Be(15);
        item.CompraErp.Should().Be(26m);
        item.CompraMl.Should().Be(41m);
        item.VendaRealJanela.Should().Be(30m);
    }

    [Fact]
    public void Horizonte_declarado_alem_do_lead_time_das_features_estoura()
    {
        // Prometer 30 dias de horizonte sobre features de lead time 7 devolveria a falha
        // como vazamento de informacao no dia mais distante.
        var acao = () => new DecisionComparer(new DecisionOptions { HorizonteMaximoMl = 30 });

        acao.Should().Throw<ArgumentException>().WithMessage("*LeadTimeDias*");
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
        // Populacao vazia nao tem taxa de concordancia nenhuma para reportar - nula, nao
        // zero: zero seria "tentamos e todo mundo divergiu", que e uma alegacao diferente
        // de "nao havia nada para reconciliar".
        result.Reconciliacao.TaxaConcordancia.Should().BeNull();
        result.Utilidade.Should().Be(UtilidadeComparacao.PopulacaoVazia);
        result.VitoriaPorDimensao.Should().BeEmpty();
    }

    // --- Utilidade do resultado: comparacao que nao comparou nada ------------

    [Fact]
    public void Populacao_toda_extrapolada_continua_utilizavel_e_declara_o_esticamento()
    {
        // Cenario real: horizonte 7 contra sugestoes PBS de 30 dias. Antes a execucao inteira
        // saia ForaDoHorizonteMl e nada era comparado. Agora compara, e o que precisa ser lido
        // junto do resultado e o fator de extrapolacao, nao a ausencia de numero.
        var result = new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 56m, vendaDia: 2m, mlDia: 2.0, sku: "SKU1", diasEstoque: 30, diasNaJanela: 30),
            Item(2m, compraSugerida: 26m, vendaDia: 2m, mlDia: 2.0, sku: "SKU2", diasEstoque: 15, diasNaJanela: 15),
        ]);

        result.ItensComparados.Should().Be(2);
        result.Reconciliacao.Reconciliados.Should().Be(2);
        result.ForaDoHorizonteMl.Should().BeEmpty();

        result.Utilidade.Should().Be(UtilidadeComparacao.Utilizavel);
        result.ItensComTaxaExtrapolada.Should().Be(2);
        result.FatorMaximoDeExtrapolacao.Should().BeApproximately(30m / 7m, 0.001m,
            "o fator reportado e o pior da execucao, nao a media");
        result.Reconciliacao.TaxaConcordancia.Should().NotBeNull();
    }

    [Fact]
    public void Populacao_toda_descartada_por_ruptura_nao_e_utilizavel_e_nao_finge_100_por_cento()
    {
        // Mesma armadilha, outro portao: tudo reconcilia e esta dentro do horizonte, mas
        // toda janela teve ruptura e a politica default descarta o item inteiro.
        var result = new DecisionComparer().Compare(
        [
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, sku: "SKU1", diaEmRuptura: 0),
            Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0, sku: "SKU2", diaEmRuptura: 0),
        ]);

        result.ItensComparados.Should().Be(0);
        result.Reconciliacao.Reconciliados.Should().Be(2);
        result.ItensDescartadosPorRuptura.Should().Be(2);
        result.ForaDoHorizonteMl.Should().BeEmpty();

        result.Utilidade.Should().Be(UtilidadeComparacao.DescartadoPorRuptura);
        result.Reconciliacao.TaxaConcordancia.Should().BeNull();
    }

    [Fact]
    public void Populacao_que_nao_reconcilia_nada_e_identificavel_sem_ficar_null_indevidamente()
    {
        // Aqui a taxa 0,0 e o proprio alarme - nao deve virar nula so porque nada foi
        // comparado, senao o portao perderia o unico numero que hoje ja denuncia falha.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 99m, vendaDia: 2m, mlDia: 2.0)]);

        result.Utilidade.Should().Be(UtilidadeComparacao.ReconciliacaoDivergente);
        result.Reconciliacao.TaxaConcordancia.Should().Be(0.0);
    }

    [Fact]
    public void Item_comparavel_e_sempre_utilizavel()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 2.0)]);

        result.Utilidade.Should().Be(UtilidadeComparacao.Utilizavel);
    }

    // --- Minor B: fallback silencioso para a formula rejeitada ---------------

    [Fact]
    public void Estoque_de_seguranca_nulo_conta_o_fallback_para_a_formula_rejeitada()
    {
        // Sem eSeg declarado a formula degenera na Proporcional (a que foi rejeitada por
        // amplificar a discordancia do ML) mesmo com SegurancaFixa selecionado - isso tem
        // de aparecer contado, nao so silenciosamente acontecer.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: null)]);

        result.ItensComFallbackEstoqueSeguranca.Should().Be(1);
    }

    [Fact]
    public void Estoque_de_seguranca_positivo_nao_conta_fallback()
    {
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: 6m)]);

        result.ItensComFallbackEstoqueSeguranca.Should().Be(0);
    }

    [Fact]
    public void Modo_proporcional_selecionado_nao_conta_como_fallback()
    {
        // Sem SegurancaFixa pedido, nao ha formula rejeitada sendo restaurada as
        // escondidas - o proprio modo escolhido ja e o proporcional.
        var opt = new DecisionOptions { ReescalaTipo1 = ReescalaEstoqueMaximo.Proporcional };
        var result = new DecisionComparer(opt).Compare(
            [Item(2m, compraSugerida: 16m, vendaDia: 2m, mlDia: 3.0,
                  tipoCalculo: 1, estoqueMaximo: 20m, estoqueSeguranca: null)]);

        result.ItensComFallbackEstoqueSeguranca.Should().Be(0);
    }

    [Fact]
    public void Dias_de_reposicao_nao_conta_fallback_de_estoque_de_seguranca()
    {
        // TipoCalculo 2 nao usa eSeg; o conceito de fallback nao se aplica a ele.
        var result = new DecisionComparer().Compare(
            [Item(2m, compraSugerida: 10m, vendaDia: 2m, mlDia: 3.0, tipoCalculo: 2)]);

        result.ItensComFallbackEstoqueSeguranca.Should().Be(0);
    }
}
