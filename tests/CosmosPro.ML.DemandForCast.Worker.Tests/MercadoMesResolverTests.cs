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
}
