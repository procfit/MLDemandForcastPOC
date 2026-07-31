using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Comparison;

namespace CosmosPro.ML.DemandForCast.Forecasting.Tests;

public sealed class ForecastVsErpComparerTests
{
    // Sugestao gerada em 01/03; com LeadTimeDias 7 os dias-alvo licitos vao ate 07/03
    // (a observacao mais recente que alimenta 07/03 e 28/02, anterior a DataHora).
    private static readonly DateTime DataHora = new(2025, 3, 1, 8, 0, 0);

    private static DateOnly Dia(int offset) => new DateOnly(2025, 3, 1).AddDays(offset);

    private static FeatureVector Fv(
        DateOnly data, decimal vendaReal, bool ruptura, int loja, string sku,
        string categoria, string classeAbc, string uf) => new()
        {
            Data = data,
            LojaId = loja,
            Sku = sku,
            Target = vendaReal,
            IsValidTarget = !ruptura,
            Categoria = categoria,
            ClasseAbc = classeAbc,
            UF = uf,
        };

    private static ComparisonItem Item(
        double demandaDiaErp,
        IEnumerable<(int Offset, decimal Real, double Ml, bool Ruptura)> dias,
        int loja = 1,
        string sku = "SKU1",
        string categoria = "OTC",
        string classeAbc = "A",
        string curva = "A",
        long sugestaoId = 1,
        DateTime? dataHora = null) => new()
        {
            RedeId = 1,
            SugestaoId = sugestaoId,
            DataHora = dataHora ?? DataHora,
            TipoCalculo = 1,
            LojaId = loja,
            Sku = sku,
            Curva = curva,
            DemandaDiaErp = demandaDiaErp,
            Dias = dias.Select(d => new DiaAvaliado(
                Fv(Dia(d.Offset), d.Real, d.Ruptura, loja, sku, categoria, classeAbc, "SP"),
                d.Ml)).ToList(),
        };

    private static (int, decimal, double, bool)[] UmDia(decimal real, double ml, int offset = 0) =>
        [(offset, real, ml, false)];

    // --- Caso do brief -------------------------------------------------------

    [Fact]
    public void Ml_mais_perto_da_venda_real_vence_o_par()
    {
        // ERP previu 2,0/dia, ML previu 3,0/dia, venda real 3,0/dia.
        var result = new ForecastVsErpComparer().Compare([Item(2.0, UmDia(3m, 3.0))]);

        result.ParesAvaliados.Should().Be(1);
        result.Vitoria.VitoriasMl.Should().Be(1);
        result.Vitoria.TaxaVitoriaMl.Should().Be(1.0);
        result.Detalhe.Single().Resultado.Should().Be(ResultadoPar.VitoriaMl);
        result.Ml.Global.Mae.Should().BeApproximately(0, 1e-9);
        result.Erp.Global.Mae.Should().BeApproximately(1.0, 1e-9);
        result.Erp.Global.Wape.Should().BeApproximately(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Erp_melhor_que_o_ml_derruba_a_taxa_de_vitoria_abaixo_da_metade()
    {
        // 3 pares; o ERP erra menos em 2 deles. O comparador precisa reportar isso.
        var pop = new[]
        {
            Item(10.0, UmDia(10m, 4.0), sku: "A"),   // ERP exato, ML erra 6
            Item(10.0, UmDia(10m, 3.0), sku: "B"),   // ERP exato, ML erra 7
            Item(10.0, UmDia(2m, 2.0), sku: "C"),    // ML exato, ERP erra 8
        };

        var result = new ForecastVsErpComparer().Compare(pop);

        result.Vitoria.VitoriasMl.Should().Be(1);
        result.Vitoria.VitoriasErp.Should().Be(2);
        result.Vitoria.TaxaVitoriaMl.Should().BeApproximately(1.0 / 3.0, 1e-9);
        result.Erp.Global.Mae.Should().BeLessThan(result.Ml.Global.Mae);
    }

    [Fact]
    public void Empate_nao_conta_como_vitoria_de_ninguem()
    {
        // Erros absolutos identicos (ERP 2 abaixo, ML 2 acima da venda real).
        var result = new ForecastVsErpComparer().Compare([Item(8.0, UmDia(10m, 12.0))]);

        result.Detalhe.Single().Resultado.Should().Be(ResultadoPar.Empate);
        result.Vitoria.Empates.Should().Be(1);
        result.Vitoria.VitoriasMl.Should().Be(0);
        result.Vitoria.VitoriasErp.Should().Be(0);
        result.Vitoria.N.Should().Be(1);
        result.Vitoria.TaxaVitoriaMl.Should().Be(0);
    }

    [Fact]
    public void Tolerancia_de_empate_absorve_diferenca_irrelevante()
    {
        var opt = new ComparisonOptions { EmpateTolerancia = 0.01 };
        var result = new ForecastVsErpComparer(opt).Compare([Item(9.999, UmDia(10m, 10.001))]);

        result.Detalhe.Single().Resultado.Should().Be(ResultadoPar.Empate);
    }

    // --- Regra de informacao -------------------------------------------------

    [Fact]
    public void Dia_alvo_que_exige_informacao_a_partir_da_DataHora_falha_ruidosamente()
    {
        // Offset 7 => dia-alvo 08/03; com lead time 7 a feature enxerga 01/03,
        // que e o proprio dia da sugestao. Vazamento.
        var pop = new[] { Item(2.0, [(7, 3m, 3.0, false)]) };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*informacao*");
    }

    [Fact]
    public void Dia_alvo_no_limite_do_corte_de_informacao_eh_aceito()
    {
        // Offset 6 => dia-alvo 07/03; a feature enxerga ate 28/02, anterior a DataHora.
        var result = new ForecastVsErpComparer().Compare([Item(2.0, [(6, 3m, 3.0, false)])]);

        result.ParesAvaliados.Should().Be(1);
    }

    [Fact]
    public void Lead_time_menor_estreita_a_janela_licita()
    {
        // Quanto menor o lead time, mais recente e a informacao que alimenta cada
        // dia-alvo: com LeadTimeDias 1 o dia-alvo 07/03 enxerga 06/03, ja depois da
        // sugestao. O mesmo dia-alvo era licito com lead time 7.
        var opt = new ComparisonOptions { LeadTimeDias = 1 };
        var act = () => new ForecastVsErpComparer(opt).Compare([Item(2.0, [(6, 3m, 3.0, false)])]);

        act.Should().Throw<ArgumentException>().WithMessage("*informacao*");
    }

    // --- Regra de populacao --------------------------------------------------

    [Fact]
    public void Dia_de_outro_sku_falha_ruidosamente()
    {
        var item = Item(2.0, UmDia(3m, 3.0)) with
        {
            Dias = [new DiaAvaliado(Fv(Dia(0), 3m, false, 1, "OUTRO", "OTC", "A", "SP"), 3.0)],
        };

        var act = () => new ForecastVsErpComparer().Compare([item]);

        act.Should().Throw<ArgumentException>().WithMessage("*populacao*");
    }

    [Fact]
    public void Populacao_com_TipoCalculo_misturado_falha_ruidosamente()
    {
        var pop = new[]
        {
            Item(2.0, UmDia(3m, 3.0), sku: "A"),
            Item(2.0, UmDia(3m, 3.0), sku: "B") with { TipoCalculo = 2 },
        };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*TipoCalculo*");
    }

    [Fact]
    public void Populacao_com_redes_misturadas_falha_ruidosamente()
    {
        var pop = new[]
        {
            Item(2.0, UmDia(3m, 3.0), sku: "A"),
            Item(2.0, UmDia(3m, 3.0), sku: "B") with { RedeId = 2 },
        };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*rede*");
    }

    [Fact]
    public void Populacao_vazia_devolve_resultado_neutro()
    {
        var result = new ForecastVsErpComparer().Compare([]);

        result.ParesAvaliados.Should().Be(0);
        result.Vitoria.N.Should().Be(0);
        result.Vitoria.TaxaVitoriaMl.Should().Be(0);
        result.Erp.Global.N.Should().Be(0);
        result.Ml.Global.N.Should().Be(0);
    }

    // --- Ruptura -------------------------------------------------------------

    [Fact]
    public void Dia_em_ruptura_sai_do_calculo_dos_dois_bracos()
    {
        // 10 vendidos no dia sem ruptura; dia em ruptura vendeu 0 (estoque zerado).
        // A demanda real do par e 10/dia, nao 5/dia. E a media do ML tambem precisa
        // ignorar o dia mascarado, senao um braco e julgado em dias que o outro nao ve.
        var pop = new[] { Item(10.0, [(0, 10m, 10.0, false), (1, 0m, 100.0, true)]) };

        var result = new ForecastVsErpComparer().Compare(pop);

        var par = result.Detalhe.Single();
        par.DiasAvaliados.Should().Be(1);
        par.DemandaDiaReal.Should().BeApproximately(10.0, 1e-9);
        par.DemandaDiaMl.Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void ExcluirPar_descarta_o_par_inteiro_quando_ha_qualquer_ruptura()
    {
        var opt = new ComparisonOptions { Ruptura = RupturaTratamento.ExcluirPar };
        var pop = new[] { Item(10.0, [(0, 10m, 10.0, false), (1, 0m, 10.0, true)]) };

        var result = new ForecastVsErpComparer(opt).Compare(pop);

        result.ParesAvaliados.Should().Be(0);
        result.ParesDescartados.Should().Be(1);
    }

    [Fact]
    public void Incluir_pontua_a_ruptura_como_se_venda_fosse_demanda()
    {
        // Modo de sensibilidade: sabidamente enviesado para baixo, por isso nao e default.
        var opt = new ComparisonOptions { Ruptura = RupturaTratamento.Incluir };
        var pop = new[] { Item(10.0, [(0, 10m, 10.0, false), (1, 0m, 10.0, true)]) };

        var result = new ForecastVsErpComparer(opt).Compare(pop);

        result.Detalhe.Single().DemandaDiaReal.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void Par_com_a_janela_toda_em_ruptura_eh_descartado_e_contado()
    {
        var pop = new[]
        {
            Item(10.0, [(0, 0m, 10.0, true), (1, 0m, 10.0, true)], sku: "A"),
            Item(10.0, UmDia(10m, 10.0), sku: "B"),
        };

        var result = new ForecastVsErpComparer().Compare(pop);

        result.ParesAvaliados.Should().Be(1);
        result.ParesDescartados.Should().Be(1);
        result.Detalhe.Single().Sku.Should().Be("B");
    }

    // --- Agregacao por hierarquia --------------------------------------------

    [Fact]
    public void Ml_pode_vencer_no_global_e_perder_dentro_de_uma_categoria()
    {
        // OTC: ML acerta em cheio nos 3 pares (ERP erra 6 em cada). Controlado: o ERP
        // acerta e o ML erra 6. A media global favorece o ML e esconde a regressao;
        // a quebra por dimensao precisa expo-la.
        var pop = new[]
        {
            Item(4.0, UmDia(10m, 10.0), sku: "A", categoria: "OTC"),
            Item(4.0, UmDia(10m, 10.0), sku: "B", categoria: "OTC"),
            Item(4.0, UmDia(10m, 10.0), sku: "C", categoria: "OTC"),
            Item(10.0, UmDia(10m, 16.0), sku: "D", categoria: "Controlado"),
        };

        var result = new ForecastVsErpComparer().Compare(pop);

        result.Vitoria.TaxaVitoriaMl.Should().BeApproximately(0.75, 1e-9);
        result.Ml.Global.Mae.Should().BeLessThan(result.Erp.Global.Mae);

        result.Ml.PorDimensao.Should().ContainKey("Categoria");
        result.Ml.PorDimensao["Categoria"].Keys.Should().Contain(["OTC", "Controlado"]);
        result.Ml.PorDimensao["Categoria"]["Controlado"].Mae
            .Should().BeGreaterThan(result.Erp.PorDimensao["Categoria"]["Controlado"].Mae);

        result.VitoriaPorDimensao["Categoria"]["Controlado"].TaxaVitoriaMl.Should().Be(0);
        result.VitoriaPorDimensao["Categoria"]["OTC"].TaxaVitoriaMl.Should().Be(1);
    }

    [Fact]
    public void Dimensoes_seguem_a_mesma_forma_do_backtest_mais_a_curva_do_erp()
    {
        var pop = new[] { Item(2.0, UmDia(3m, 3.0), curva: "B") };

        var result = new ForecastVsErpComparer().Compare(pop);

        result.Ml.PorDimensao.Keys.Should().Contain(["Categoria", "ClasseAbc", "Loja", "UF", "CurvaErp"]);
        result.Ml.PorDimensao["Loja"].Keys.Should().Contain("1");
        result.Ml.PorDimensao["CurvaErp"].Keys.Should().Contain("B");
    }

    [Fact]
    public void Previsao_negativa_de_qualquer_braco_eh_clampada_em_zero()
    {
        var pop = new[] { Item(-5.0, UmDia(10m, -5.0)) };

        var result = new ForecastVsErpComparer().Compare(pop);

        var par = result.Detalhe.Single();
        par.DemandaDiaErp.Should().Be(0);
        par.DemandaDiaMl.Should().Be(0);
        par.Resultado.Should().Be(ResultadoPar.Empate);
    }

    [Fact]
    public void Nomes_dos_bracos_identificam_a_origem_de_cada_previsao()
    {
        var result = new ForecastVsErpComparer().Compare([Item(2.0, UmDia(3m, 3.0))]);

        result.Erp.Nome.Should().Be("erp-pbs");
        result.Ml.Nome.Should().Be("ml");
    }
}
