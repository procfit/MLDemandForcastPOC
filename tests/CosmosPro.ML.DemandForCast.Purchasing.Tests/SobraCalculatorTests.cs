using CosmosPro.ML.DemandForCast.Purchasing.Comparison;

namespace CosmosPro.ML.DemandForCast.Purchasing.Tests;

public sealed class SobraCalculatorTests
{
    // --- Casos do brief, adaptados para o parametro de pedidos pendentes -----

    [Fact]
    public void Sobra_e_o_que_comprou_mais_estoque_menos_o_que_vendeu()
    {
        var s = SobraCalculator.Calcular(
            comprado: 100, estoqueInicial: 20, pedidosPendentes: 0, vendido: 80, precoCompra: 3.50m);

        s.Unidades.Should().Be(40);
        s.Valor.Should().Be(140m);
    }

    [Fact]
    public void Nao_existe_sobra_negativa()
    {
        var s = SobraCalculator.Calcular(
            comprado: 10, estoqueInicial: 0, pedidosPendentes: 0, vendido: 50, precoCompra: 2m);

        s.Unidades.Should().Be(0, "vender mais que o disponivel nao gera sobra negativa — " +
                                  "gera ruptura, medida separadamente");
        s.Valor.Should().Be(0);
    }

    // --- A correcao: pedidos pendentes entram na posicao, como em DecisionComparer ---

    [Fact]
    public void Pedidos_pendentes_entram_na_posicao_igual_ao_braco_do_DecisionComparer()
    {
        // Mesma logica de PosicaoParaPontuacao + compra no DecisionComparer: mercadoria em
        // transito chega e atende a janela, sem depender de ConsideraPedidosPendentes.
        var s = SobraCalculator.Calcular(
            comprado: 50, estoqueInicial: 20, pedidosPendentes: 30, vendido: 80, precoCompra: 3.50m);

        s.Unidades.Should().Be(20); // 50 + 20 + 30 - 80
        s.Valor.Should().Be(70m);
    }

    [Fact]
    public void Sem_pedidos_pendentes_a_venda_absorve_a_posicao_sem_gerar_sobra_negativa()
    {
        var s = SobraCalculator.Calcular(
            comprado: 10, estoqueInicial: 0, pedidosPendentes: 40, vendido: 50, precoCompra: 2m);

        s.Unidades.Should().Be(0);
        s.Valor.Should().Be(0);
    }

    // --- Preco nulo ou zero nao pode virar valor bugado -----------------------

    [Fact]
    public void Preco_nulo_produz_valor_zero_sem_lancar()
    {
        var s = SobraCalculator.Calcular(
            comprado: 100, estoqueInicial: 20, pedidosPendentes: 0, vendido: 80, precoCompra: null);

        s.Unidades.Should().Be(40);
        s.Valor.Should().Be(0m);
    }

    [Fact]
    public void Preco_zero_produz_valor_zero()
    {
        var s = SobraCalculator.Calcular(
            comprado: 100, estoqueInicial: 20, pedidosPendentes: 0, vendido: 80, precoCompra: 0m);

        s.Unidades.Should().Be(40);
        s.Valor.Should().Be(0m);
    }

    // --- Arredondamento monetario: nunca mais preciso que centavos ------------

    [Fact]
    public void Valor_nao_sai_com_mais_precisao_que_centavos()
    {
        // 33 unidades x 0,145 = 4,785 — o meio exato entre 4,78 e 4,79. A regra escolhida e
        // arredondar para cima em vez de para o par mais proximo (banker's rounding), porque
        // dinheiro de manchete precisa ser previsivel para o usuario leigo.
        var s = SobraCalculator.Calcular(
            comprado: 33, estoqueInicial: 0, pedidosPendentes: 0, vendido: 0, precoCompra: 0.145m);

        s.Unidades.Should().Be(33);
        s.Valor.Should().Be(4.79m);
    }

    // --- Sobra ATRIBUIVEL A COMPRA -------------------------------------------

    [Fact]
    public void Compra_zero_nao_deixa_sobra_atribuivel_por_mais_cheia_que_a_prateleira_esteja()
    {
        // O caso que motivou a segunda medida. Na grade real, o ERP comprou 207 unidades e a
        // prateleira terminou com 4.194 de excesso: cobrar isso da decisao de compra e cobrar
        // dela o que ela nao fez. Comprou zero, causou zero.
        var total = SobraCalculator.Calcular(
            comprado: 0, estoqueInicial: 100, pedidosPendentes: 0, vendido: 10, precoCompra: 2m);
        var daCompra = SobraCalculator.CalcularDaCompra(
            comprado: 0, estoqueInicial: 100, pedidosPendentes: 0, vendido: 10, precoCompra: 2m);

        total.Unidades.Should().Be(90, "a prateleira de fato terminou com 90");
        daCompra.Unidades.Should().Be(0);
        daCompra.Valor.Should().Be(0m);
    }

    [Fact]
    public void Venda_consome_primeiro_a_posicao_que_ja_existia()
    {
        // Estoque previo 10 (+2 em transito) cobre a venda de 8 inteira: nenhuma das 5
        // compradas foi necessaria.
        var s = SobraCalculator.CalcularDaCompra(
            comprado: 5, estoqueInicial: 10, pedidosPendentes: 2, vendido: 8, precoCompra: 3m);

        s.Unidades.Should().Be(5);
        s.Valor.Should().Be(15m);
    }

    [Fact]
    public void So_o_que_a_venda_ultrapassa_da_posicao_previa_e_atribuido_a_compra()
    {
        // Posicao previa 12, venda 20: 8 unidades sairam da compra de 10, sobram 2.
        var s = SobraCalculator.CalcularDaCompra(
            comprado: 10, estoqueInicial: 10, pedidosPendentes: 2, vendido: 20, precoCompra: 4m);

        s.Unidades.Should().Be(2);
        s.Valor.Should().Be(8m);
    }

    [Fact]
    public void Sobra_da_compra_nunca_excede_a_sobra_total()
    {
        // Invariante das duas medidas: a parcela atribuivel nunca e maior que o todo, e a
        // diferenca entre elas e exatamente o estoque herdado que sobrou.
        var total = SobraCalculator.Calcular(
            comprado: 10, estoqueInicial: 50, pedidosPendentes: 0, vendido: 20, precoCompra: null);
        var daCompra = SobraCalculator.CalcularDaCompra(
            comprado: 10, estoqueInicial: 50, pedidosPendentes: 0, vendido: 20, precoCompra: null);

        total.Unidades.Should().Be(40);
        daCompra.Unidades.Should().Be(10);
        daCompra.Unidades.Should().BeLessThanOrEqualTo(total.Unidades);
    }
}
