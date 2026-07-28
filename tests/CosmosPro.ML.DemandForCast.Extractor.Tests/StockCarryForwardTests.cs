using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class StockCarryForwardTests
{
    private static readonly DateOnly Dia1 = new(2026, 1, 1);
    private static DateOnly Dia(int n) => Dia1.AddDays(n - 1);

    private static StockMovement Mov(int loja, string sku, int dia, decimal saldo) =>
        new(loja, sku, Dia(dia), saldo);

    [Fact]
    public void Repete_o_ultimo_saldo_nos_dias_sem_movimento()
    {
        var movimentos = new[] { Mov(1, "A", 1, 10m), Mov(1, "A", 4, 3m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(4)).ToArray();

        densa.Select(m => (m.Data, m.Saldo)).Should().Equal(
            (Dia(1), 10m),
            (Dia(2), 10m),
            (Dia(3), 10m),
            (Dia(4), 3m));
    }

    [Fact]
    public void Estende_a_serie_ate_o_fim_do_periodo_apos_o_ultimo_movimento()
    {
        var movimentos = new[] { Mov(1, "A", 1, 7m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(3)).ToArray();

        densa.Should().HaveCount(3);
        densa.Should().OnlyContain(m => m.Saldo == 7m);
        densa[^1].Data.Should().Be(Dia(3));
    }

    [Fact]
    public void Troca_de_serie_fecha_a_anterior_ate_o_fim_do_periodo()
    {
        var movimentos = new[] { Mov(1, "A", 1, 5m), Mov(1, "B", 2, 8m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(3)).ToArray();

        densa.Where(m => m.Sku == "A").Select(m => m.Data)
            .Should().Equal(Dia(1), Dia(2), Dia(3));
        densa.Where(m => m.Sku == "B").Select(m => m.Data)
            .Should().Equal(Dia(2), Dia(3));
    }

    [Fact]
    public void Troca_de_loja_tambem_inicia_nova_serie()
    {
        var movimentos = new[] { Mov(1, "A", 1, 5m), Mov(2, "A", 1, 9m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(2)).ToArray();

        densa.Where(m => m.LojaId == 1).Should().HaveCount(2);
        densa.Where(m => m.LojaId == 2).Should().HaveCount(2);
    }

    [Fact]
    public void Nao_emite_dias_anteriores_ao_primeiro_movimento()
    {
        // Saldo antes do primeiro lançamento é desconhecido; assumir zero
        // inventaria uma ruptura que não existiu.
        var movimentos = new[] { Mov(1, "A", 3, 4m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(4)).ToArray();

        densa.Should().HaveCount(2);
        densa[0].Data.Should().Be(Dia(3));
    }

    [Fact]
    public void Saldo_zero_e_propagado_para_a_ruptura_ficar_visivel()
    {
        var movimentos = new[] { Mov(1, "A", 1, 0m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(3)).ToArray();

        densa.Should().HaveCount(3);
        densa.Should().OnlyContain(m => m.Saldo == 0m);
    }

    [Fact]
    public void Duplicata_de_data_mantem_o_ultimo_lancamento()
    {
        var movimentos = new[] { Mov(1, "A", 1, 10m), Mov(1, "A", 1, 2m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(1)).ToArray();

        densa.Should().ContainSingle();
        densa[0].Saldo.Should().Be(2m);
    }

    [Fact]
    public void Sequencia_vazia_devolve_vazio()
    {
        StockCarryForward.Densify([], Dia(5)).Should().BeEmpty();
    }

    [Fact]
    public void Movimento_apos_o_fim_do_periodo_nao_gera_dias_extras()
    {
        var movimentos = new[] { Mov(1, "A", 5, 1m) };

        var densa = StockCarryForward.Densify(movimentos, Dia(3)).ToArray();

        densa.Should().ContainSingle();
        densa[0].Data.Should().Be(Dia(5));
    }
}
