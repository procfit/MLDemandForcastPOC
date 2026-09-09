using CosmosPro.ML.DemandForCast.Worker.Mercado;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// Qual mês da IQVIA a sessão compara. Errar isto não dá erro: dá um alerta que parece
/// certo e é circular — se o mês escolhido contém as consequências da própria sugestão, a
/// afirmação "o alerta teria avisado o comprador" deixa de valer, e é justamente essa
/// afirmação que a dissertação sustenta.
/// </summary>
public sealed class MercadoMesResolverTests
{
    private static DateOnly Mes(int ano, int mes) => new(ano, mes, 1);

    [Fact]
    public void Com_um_arquivo_so_carregado_cai_no_espelho_do_ano_anterior()
    {
        // O relatório mensal da IQVIA traz o mês corrente e o mesmo mês do ano anterior.
        // Sugestão de junho/2026 com só esse arquivo tem de usar junho/2025.
        var cobertos = new[] { Mes(2025, 6), Mes(2026, 6) };

        MercadoMesResolver.Resolver(cobertos, new DateOnly(2026, 6, 10))
            .Should().Be(Mes(2025, 6));
    }

    [Fact]
    public void Com_a_serie_empilhada_usa_o_mes_imediatamente_anterior()
    {
        // Conforme a rede envia mais relatórios, a mesma regra passa a escolher o mês
        // recente sozinha, sem mudança de código.
        var cobertos = new[] { Mes(2025, 6), Mes(2026, 4), Mes(2026, 5), Mes(2026, 6) };

        MercadoMesResolver.Resolver(cobertos, new DateOnly(2026, 6, 10))
            .Should().Be(Mes(2026, 5));
    }

    [Fact]
    public void O_mes_da_propria_sugestao_nunca_e_escolhido()
    {
        MercadoMesResolver.Resolver([Mes(2026, 6)], new DateOnly(2026, 6, 1))
            .Should().BeNull();
    }

    [Fact]
    public void Mes_posterior_a_sugestao_nunca_e_escolhido()
    {
        MercadoMesResolver.Resolver([Mes(2026, 7), Mes(2026, 8)], new DateOnly(2026, 6, 30))
            .Should().BeNull();
    }

    [Fact]
    public void O_corte_e_o_primeiro_dia_do_mes_da_sugestao()
    {
        // Sugestão em 01/06/2026: maio serve, junho não. O dia da sugestão dentro do mês
        // não muda a escolha -- o que corta é o mês.
        var cobertos = new[] { Mes(2026, 5), Mes(2026, 6) };

        MercadoMesResolver.Resolver(cobertos, new DateOnly(2026, 6, 1))
            .Should().Be(Mes(2026, 5));
        MercadoMesResolver.Resolver(cobertos, new DateOnly(2026, 6, 30))
            .Should().Be(Mes(2026, 5));
    }

    [Fact]
    public void Sem_cobertura_nenhuma_devolve_nulo()
    {
        MercadoMesResolver.Resolver([], new DateOnly(2026, 6, 10)).Should().BeNull();
    }

    [Fact]
    public void A_ordem_da_cobertura_nao_importa()
    {
        // A cobertura vem de uma agregação, sem ordenação garantida.
        var desordenado = new[] { Mes(2026, 5), Mes(2025, 6), Mes(2026, 3) };

        MercadoMesResolver.Resolver(desordenado, new DateOnly(2026, 6, 10))
            .Should().Be(Mes(2026, 5));
    }

    // --- o mês cabe no histórico de estoque? (regra B3) -------------------------

    /// <summary>
    /// <b>A regressão que este teste existe para impedir, e que rodou em produção.</b>
    ///
    /// <para>
    /// O cenário é o da execução que o patrocinador reportou: sugestão de 27/07/2026,
    /// histórico de estoque de 27/07/2025 a 12/08/2026, mês comparado junho/2026. A guarda
    /// antiga comparava o mês com o <b>dia da sugestão</b> em vez do começo do histórico --
    /// dois campos chamados <c>JanelaInicio</c> com significados diferentes --, e como o mês
    /// comparado é sempre estritamente anterior ao mês da sugestão, ela era verdadeira
    /// SEMPRE. A ruptura saía nula para todo item de toda sessão, e a tela mostrava "estoque
    /// não apurado" nas 8.221 linhas.
    /// </para>
    /// </summary>
    [Fact]
    public void O_mes_comparado_cabe_no_historico_mesmo_sendo_anterior_a_sugestao()
    {
        var diaDaSugestao = new DateOnly(2026, 7, 27);
        var primeiro = new DateOnly(2025, 7, 27);
        var ultimo = new DateOnly(2026, 8, 12);

        var mes = MercadoMesResolver.Resolver([Mes(2025, 6), Mes(2026, 6)], diaDaSugestao);

        mes.Should().Be(Mes(2026, 6));
        MercadoMesResolver.CabeNoHistorico(mes!.Value, primeiro, ultimo)
            .Should().BeTrue("junho/2026 está inteiro dentro de 27/07/2025 a 12/08/2026");
        mes.Value.Should().BeBefore(diaDaSugestao,
            "e continua anterior à sugestão -- é a regra do resolver, não um impedimento");
    }

    /// <summary>
    /// Mês que começa antes do primeiro snapshot não é apurável: os dias que faltam
    /// contariam como se tivessem estoque, e um item com ruptura real sairia como
    /// <c>SemCausa</c>.
    /// </summary>
    [Fact]
    public void Mes_que_comeca_antes_do_historico_nao_cabe()
    {
        MercadoMesResolver.CabeNoHistorico(
            Mes(2025, 7), new DateOnly(2025, 7, 27), new DateOnly(2026, 8, 12))
            .Should().BeFalse("o histórico começa no dia 27, então 01 a 26/07 não têm snapshot");
    }

    /// <summary>O outro extremo: mês que termina depois do último snapshot também não cabe.</summary>
    [Fact]
    public void Mes_que_termina_depois_do_historico_nao_cabe()
    {
        MercadoMesResolver.CabeNoHistorico(
            Mes(2026, 8), new DateOnly(2025, 7, 27), new DateOnly(2026, 8, 12))
            .Should().BeFalse("o histórico para em 12/08, e 13 a 31/08 ficariam sem snapshot");
    }

    [Fact]
    public void Mes_exatamente_nas_bordas_cabe()
    {
        MercadoMesResolver.CabeNoHistorico(
            Mes(2026, 6), new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30))
            .Should().BeTrue();
    }
}
