using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Comparison;

namespace CosmosPro.ML.DemandForCast.Forecasting.Tests;

public sealed class ForecastVsErpComparerTests
{
    // Sugestao gerada em 01/03; com LeadTimeDias 7 os dias-alvo licitos vao ate 07/03
    // (a observacao mais recente que alimenta 07/03 e 28/02, anterior a DataHora).
    private static readonly DateTime DataHora = new(2025, 3, 1, 8, 0, 0);

    private static readonly DateOnly TreinadoAte = new(2025, 2, 28);

    // O padrao de producao descarta o par inteiro na primeira ruptura; os testes que
    // exercitam mascaramento por dia precisam pedir o modo de sensibilidade.
    private static readonly ComparisonOptions PorDia =
        new() { Ruptura = RupturaTratamento.ExcluirDia };

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
            ModeloTreinadoAte = TreinadoAte,
            PrecoCongeladoAPartirDe = DateOnly.FromDateTime(dataHora ?? DataHora),
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

        act.Should().Throw<ArgumentException>().WithMessage("*informa*");
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

        act.Should().Throw<ArgumentException>().WithMessage("*informa*");
    }

    [Fact]
    public void Modelo_treinado_ate_a_data_da_sugestao_falha_ruidosamente()
    {
        // Nenhuma outra checagem pega isso: as features do dia-alvo respeitam o lead
        // time, mas o AJUSTE do modelo viu o proprio periodo avaliado.
        var pop = new[] { Item(2.0, UmDia(3m, 3.0)) with { ModeloTreinadoAte = new DateOnly(2025, 3, 1) } };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*treinado at*");
    }

    [Fact]
    public void Modelo_treinado_ate_a_vespera_da_sugestao_eh_aceito()
    {
        var pop = new[] { Item(2.0, UmDia(3m, 3.0)) with { ModeloTreinadoAte = new DateOnly(2025, 2, 28) } };

        new ForecastVsErpComparer().Compare(pop).ParesAvaliados.Should().Be(1);
    }

    [Fact]
    public void Preco_congelado_divergente_da_data_da_sugestao_falha_ruidosamente()
    {
        // Sem esta checagem, uma populacao montada com FeatureConfig default (sem
        // congelamento) passaria calada por todas as demais validacoes.
        var pop = new[] { Item(2.0, UmDia(3m, 3.0)) with { PrecoCongeladoAPartirDe = new DateOnly(2025, 3, 2) } };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*congelamento*");
    }

    [Fact]
    public void Preco_congelado_igual_a_data_da_sugestao_eh_aceito()
    {
        var pop = new[] { Item(2.0, UmDia(3m, 3.0)) with { PrecoCongeladoAPartirDe = new DateOnly(2025, 3, 1) } };

        new ForecastVsErpComparer().Compare(pop).ParesAvaliados.Should().Be(1);
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

        act.Should().Throw<ArgumentException>().WithMessage("*popula*");
    }

    [Fact]
    public void Dia_repetido_dentro_do_par_falha_ruidosamente()
    {
        // Mesma data duas vezes: pesaria dobrado na media da janela sem aparecer
        // em nenhum campo do resultado.
        var pop = new[] { Item(2.0, [(0, 3m, 3.0, false), (0, 30m, 30.0, false)]) };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*repete o dia-alvo*");
    }

    [Fact]
    public void Par_duplicado_na_populacao_falha_ruidosamente()
    {
        var pop = new[]
        {
            Item(2.0, UmDia(3m, 3.0), sku: "A"),
            Item(2.0, UmDia(3m, 3.0), sku: "A"),
        };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*repete o par*");
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

    /// <summary>
    /// População homogênea, mas num método que não é nenhum dos dois do ERP. Antes só a
    /// validação da API barrava isso; a camada B já recusava, e as duas camadas precisam
    /// aceitar exatamente a mesma população.
    /// </summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)3)]
    public void Populacao_com_TipoCalculo_fora_de_1_e_2_falha_ruidosamente(byte tipo)
    {
        var pop = new[] { Item(2.0, UmDia(3m, 3.0), sku: "A") with { TipoCalculo = tipo } };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*não é modelado*");
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
    public void Populacao_vazia_devolve_resultado_neutro_com_dimensoes_vazias()
    {
        var result = new ForecastVsErpComparer().Compare([]);

        result.ParesAvaliados.Should().Be(0);
        result.Vitoria.N.Should().Be(0);
        result.Vitoria.TaxaVitoriaMl.Should().Be(0);
        result.Erp.Global.N.Should().Be(0);
        result.Ml.Global.N.Should().Be(0);

        // Mesma forma do WalkForwardBacktest.EmptyDims(): sem ponto, sem eixo.
        result.Ml.PorDimensao.Should().BeEmpty();
        result.Erp.PorDimensao.Should().BeEmpty();
        result.VitoriaPorDimensao.Should().BeEmpty();
    }

    // --- Previsao nao finita -------------------------------------------------

    [Fact]
    public void Previsao_NaN_do_ml_estoura_em_vez_de_virar_vitoria_do_erp()
    {
        // Math.Abs(NaN - x) <= tol e false, e NaN < x tambem: o par cairia como
        // VitoriaErp em silencio enquanto o MAE global virava NaN.
        var pop = new[] { Item(2.0, UmDia(3m, double.NaN), sku: "SKU_NAN") };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*n*o finita*SKU_NAN*");
    }

    [Fact]
    public void Previsao_infinita_do_ml_estoura()
    {
        var pop = new[] { Item(2.0, UmDia(3m, double.PositiveInfinity)) };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*n*o finita*");
    }

    [Fact]
    public void DemandaDia_nao_finita_do_erp_estoura()
    {
        var pop = new[] { Item(double.NaN, UmDia(3m, 3.0), sku: "SKU_ERP") };

        var act = () => new ForecastVsErpComparer().Compare(pop);

        act.Should().Throw<ArgumentException>().WithMessage("*n*o finita*SKU_ERP*");
    }

    // --- Hierarquia constante ------------------------------------------------

    [Fact]
    public void Classe_abc_que_muda_no_meio_da_janela_falha_ruidosamente()
    {
        // A curva ABC e recalculada periodicamente. Se a janela atravessar uma
        // reclassificacao, ler a hierarquia do primeiro dia faria o par cair no balde
        // que o chamador ordenou primeiro — e a quebra por ABC deixaria de reproduzir.
        var item = Item(2.0, UmDia(3m, 3.0)) with
        {
            Dias =
            [
                new DiaAvaliado(Fv(Dia(0), 3m, false, 1, "SKU1", "OTC", "A", "SP"), 3.0),
                new DiaAvaliado(Fv(Dia(1), 3m, false, 1, "SKU1", "OTC", "B", "SP"), 3.0),
            ],
        };

        var act = () => new ForecastVsErpComparer().Compare([item]);

        act.Should().Throw<ArgumentException>().WithMessage("*ClasseAbc*");
    }

    [Fact]
    public void Categoria_que_muda_no_meio_da_janela_falha_ruidosamente()
    {
        var item = Item(2.0, UmDia(3m, 3.0)) with
        {
            Dias =
            [
                new DiaAvaliado(Fv(Dia(0), 3m, false, 1, "SKU1", "OTC", "A", "SP"), 3.0),
                new DiaAvaliado(Fv(Dia(1), 3m, false, 1, "SKU1", "Controlado", "A", "SP"), 3.0),
            ],
        };

        var act = () => new ForecastVsErpComparer().Compare([item]);

        act.Should().Throw<ArgumentException>().WithMessage("*Categoria*");
    }

    [Fact]
    public void UF_que_muda_no_meio_da_janela_falha_ruidosamente()
    {
        var item = Item(2.0, UmDia(3m, 3.0)) with
        {
            Dias =
            [
                new DiaAvaliado(Fv(Dia(0), 3m, false, 1, "SKU1", "OTC", "A", "SP"), 3.0),
                new DiaAvaliado(Fv(Dia(1), 3m, false, 1, "SKU1", "OTC", "A", "MG"), 3.0),
            ],
        };

        var act = () => new ForecastVsErpComparer().Compare([item]);

        act.Should().Throw<ArgumentException>().WithMessage("*UF*");
    }

    // --- Ruptura -------------------------------------------------------------

    [Fact]
    public void ExcluirPar_eh_o_padrao_e_descarta_o_par_com_qualquer_ruptura()
    {
        // Sem isso os dois bracos seriam pontuados sobre conjuntos de dias diferentes:
        // o ML se reprojeta nos sobreviventes, o escalar do ERP nao tem como.
        var pop = new[] { Item(10.0, [(0, 10m, 10.0, false), (1, 0m, 10.0, true)]) };

        var result = new ForecastVsErpComparer().Compare(pop);

        result.ParesAvaliados.Should().Be(0);
        result.ParesDescartados.Should().Be(1);
    }

    [Fact]
    public void ExcluirDia_mascara_o_dia_nos_dois_bracos_como_sensibilidade()
    {
        // 10 vendidos no dia sem ruptura; dia em ruptura vendeu 0 (estoque zerado).
        // No modo de sensibilidade a demanda real do par e 10/dia e a media do ML
        // ignora o dia mascarado — mas so o ML consegue se reprojetar assim, por isso
        // este nao e o numero de manchete.
        var pop = new[] { Item(10.0, [(0, 10m, 10.0, false), (1, 0m, 100.0, true)]) };

        var result = new ForecastVsErpComparer(PorDia).Compare(pop);

        var par = result.Detalhe.Single();
        par.DiasAvaliados.Should().Be(1);
        par.DemandaDiaReal.Should().BeApproximately(10.0, 1e-9);
        par.DemandaDiaMl.Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void ExcluirDia_deixa_o_ml_escapar_do_erro_cometido_no_dia_descartado()
    {
        // O caso concreto que motiva ExcluirPar como padrao: o ML errou feio (20) no dia
        // que a ruptura removeu e acertou (8) nos sobreviventes; o ERP tem um escalar so
        // (10) para a janela inteira. Sob ExcluirDia o ML zera o erro; sob ExcluirPar
        // ninguem e pontuado num recorte que o outro nao ve.
        var dias = new (int, decimal, double, bool)[]
        {
            (0, 8m, 8.0, false), (1, 8m, 8.0, false), (2, 8m, 8.0, false),
            (3, 8m, 8.0, false), (4, 8m, 8.0, false),
            (5, 0m, 20.0, true), (6, 0m, 20.0, true),
        };

        var porDia = new ForecastVsErpComparer(PorDia).Compare([Item(10.0, dias)]);
        porDia.Detalhe.Single().ErroAbsMl.Should().BeApproximately(0, 1e-9);
        porDia.Detalhe.Single().Resultado.Should().Be(ResultadoPar.VitoriaMl);

        var padrao = new ForecastVsErpComparer().Compare([Item(10.0, dias)]);
        padrao.ParesAvaliados.Should().Be(0);
        padrao.ParesDescartados.Should().Be(1);
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

        var result = new ForecastVsErpComparer(PorDia).Compare(pop);

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

    // --- Unidade da metrica --------------------------------------------------

    [Fact]
    public void Resultado_declara_que_a_metrica_e_por_par_e_nao_por_dia()
    {
        // O backtest pontua um erro por (dia, loja, sku); aqui e um por par, com a
        // janela promediada. Sem o rotulo, os dois paineis seriam lidos como a mesma
        // medida — e a media encolhe a variancia, entao o MAE daqui sai menor de graca.
        var result = new ForecastVsErpComparer().Compare([Item(2.0, UmDia(3m, 3.0))]);

        result.Unidade.Should().Be(UnidadeMetrica.ErroPorParNaJanela);
        result.Unidade.Should().NotBe(UnidadeMetrica.ErroPorDia);
    }

    [Fact]
    public void Metrica_por_par_encolhe_o_erro_frente_a_metrica_por_dia()
    {
        // Mesma serie, mesmas previsoes: o ML erra +6 num dia e -6 no outro. Por dia o
        // MAE seria 6; promediado no par os erros se cancelam e o MAE vai a zero. E a
        // razao de existir UnidadeMetrica.
        var pop = new[] { Item(10.0, [(0, 10m, 16.0, false), (1, 10m, 4.0, false)]) };

        var result = new ForecastVsErpComparer().Compare(pop);

        result.Ml.Global.N.Should().Be(1, "um ponto por par, nao um por dia");
        result.Ml.Global.Mae.Should().BeApproximately(0, 1e-9);
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
