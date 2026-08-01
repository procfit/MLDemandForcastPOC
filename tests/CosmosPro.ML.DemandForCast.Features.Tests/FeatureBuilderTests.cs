using CosmosPro.ML.DemandForCast.Features;
using CosmosPro.ML.DemandForCast.Features.Models;

namespace CosmosPro.ML.DemandForCast.Features.Tests;

public sealed class FeatureBuilderTests
{
    private static readonly DateOnly Origem = new(2025, 1, 1);

    /// <summary>Série densa onde Quantidade[i] = i (o offset em dias desde a origem).</summary>
    private static List<DailyObservation> SerieIndexada(int dias, Func<int, DailyObservation>? customizer = null)
    {
        var list = new List<DailyObservation>(dias);
        for (int i = 0; i < dias; i++)
        {
            var obs = new DailyObservation
            {
                Data = Origem.AddDays(i),
                LojaId = 1,
                Sku = "SKU1",
                Quantidade = i,
                PrecoUnitario = 10m,
                Categoria = "OTC",
                PrincipioAtivo = "Dipirona",
                ClasseAbc = "A",
                UF = "SP",
                Regiao = "Sudeste",
                PerfilLoja = "Bairro",
            };
            list.Add(customizer is null ? obs : customizer(i) with
            {
                Data = obs.Data, LojaId = obs.LojaId, Sku = obs.Sku,
            });
        }
        return list;
    }

    [Fact]
    public void Lags_referenciam_exatamente_D_menos_k()
    {
        var serie = SerieIndexada(60);
        var fvs = new FeatureBuilder().BuildSeries(serie).ToList();

        // Primeiro alvo válido: HistoricoMinimoDias = max(28, 7+28-1=34) = 34.
        var primeiro = fvs[0];
        primeiro.Data.Should().Be(Origem.AddDays(34));

        // Para o alvo no índice d, qtd[i] = i, então Lag7 = d-7, Lag14 = d-14...
        foreach (var fv in fvs)
        {
            int d = fv.Data.DayNumber - Origem.DayNumber;
            fv.Lag7.Should().Be(d - 7);
            fv.Lag14.Should().Be(d - 14);
            fv.Lag21.Should().Be(d - 21);
            fv.Lag28.Should().Be(d - 28);
        }
    }

    [Fact]
    public void Nenhuma_feature_de_historico_usa_dados_dentro_do_lead_time()
    {
        // Série base com zeros até o dia D-1; só o dia D-1 (dentro do lead time)
        // recebe um pico gigante. Se alguma feature de histórico o capturar, vaza.
        const int dias = 50;
        const int alvoIdx = 45;
        var pico = 99999m;

        var serie = SerieIndexada(dias, i => new DailyObservation
        {
            Data = Origem, LojaId = 1, Sku = "SKU1",
            // pico só nos dias entre D-6 e D-1 (dentro do lead time de 7)
            Quantidade = (i >= alvoIdx - 6 && i <= alvoIdx - 1) ? pico : 1m,
            PrecoUnitario = 10m,
        });

        var fv = new FeatureBuilder().BuildSeries(serie).Single(f => f.Data == Origem.AddDays(alvoIdx));

        // Todas as features de histórico devem refletir o baseline (1), nunca o pico.
        fv.Lag7.Should().Be(1m);
        fv.Lag14.Should().Be(1m);
        fv.RollMean7.Should().Be(1m);
        fv.RollMean28.Should().Be(1m);
        fv.RollMax28.Should().Be(1m, "o pico está dentro do lead time e não pode vazar para rolling/max");
    }

    [Fact]
    public void RollMean7_eh_a_media_dos_7_dias_terminando_em_D_menos_leadtime()
    {
        var serie = SerieIndexada(60);
        var fv = new FeatureBuilder().BuildSeries(serie).Single(f => f.Data == Origem.AddDays(40));

        // d=40, leadTime=7 → janela termina no índice 33, volta 7 dias: 27..33.
        // qtd[i]=i → média de 27..33 = 30.
        fv.RollMean7.Should().Be(30m);
    }

    [Fact]
    public void RollMax28_pega_o_maior_da_janela_respeitando_lead_time()
    {
        var serie = SerieIndexada(60);
        var fv = new FeatureBuilder().BuildSeries(serie).Single(f => f.Data == Origem.AddDays(40));

        // d=40 → janela 28 dias termina em 33: índices 6..33. Máx = 33.
        fv.RollMax28.Should().Be(33m);
    }

    [Fact]
    public void Dia_em_ruptura_marca_IsValidTarget_false()
    {
        var serie = SerieIndexada(50, i => new DailyObservation
        {
            Data = Origem, LojaId = 1, Sku = "SKU1",
            Quantidade = 0,                       // venda observada 0
            EmRuptura = i == 40,                  // mas por ruptura no dia 40
            PrecoUnitario = 10m,
        });

        var fvs = new FeatureBuilder().BuildSeries(serie).ToList();
        var rupturaDay = fvs.Single(f => f.Data == Origem.AddDays(40));
        var normalDay = fvs.Single(f => f.Data == Origem.AddDays(41));

        rupturaDay.IsValidTarget.Should().BeFalse();
        normalDay.IsValidTarget.Should().BeTrue();
    }

    [Fact]
    public void Densifica_gaps_preenchendo_dias_sem_venda_com_zero()
    {
        // Série esparsa: vendas só em alguns dias. Lags devem tratar gaps como 0.
        var serie = new List<DailyObservation>
        {
            Obs(0, 5m), Obs(34, 7m), Obs(41, 3m),
        };

        var fvs = new FeatureBuilder().BuildSeries(serie).ToList();
        // Range 0..41 = 42 dias densos; alvos a partir de d=34.
        var alvo41 = fvs.Single(f => f.Data == Origem.AddDays(41));

        // Lag7 do dia 41 = qtd[34] = 7. Demais dias do histórico são 0.
        alvo41.Lag7.Should().Be(7m);
        alvo41.Lag14.Should().Be(0m, "dia 27 não tinha venda → densificado para 0");
    }

    [Fact]
    public void DiasDesdeUltimaPromo_conta_ate_o_limite_do_lead_time()
    {
        var serie = SerieIndexada(50, i => new DailyObservation
        {
            Data = Origem, LojaId = 1, Sku = "SKU1",
            Quantidade = 1m,
            EmPromocao = i == 30,   // promoção no dia 30
            PrecoUnitario = 10m,
        });

        // alvo no dia 40, leadTime 7 → olha histórico até dia 33. Última promo: dia 30.
        // dias desde = 40 - 30 = 10.
        var fv = new FeatureBuilder().BuildSeries(serie).Single(f => f.Data == Origem.AddDays(40));
        fv.DiasDesdeUltimaPromo.Should().Be(10);
    }

    [Fact]
    public void Calendario_e_feriado_refletem_o_dia_alvo()
    {
        // 2025-01-01 + 34 dias = 2025-02-04 (terça). Vamos achar um feriado conhecido.
        // Tiradentes 2025-04-21 (segunda). Origem + 110 dias = 2025-04-21.
        var serie = SerieIndexada(140);
        var fvs = new FeatureBuilder().BuildSeries(serie).ToList();

        var tiradentes = fvs.Single(f => f.Data == new DateOnly(2025, 4, 21));
        tiradentes.Feriado.Should().BeTrue();
        tiradentes.Mes.Should().Be(4);
        tiradentes.DiaDoMes.Should().Be(21);

        var sabado = fvs.First(f => f.Data.DayOfWeek == DayOfWeek.Saturday);
        sabado.FimDeSemana.Should().BeTrue();
    }

    [Fact]
    public void Build_agrupa_por_Sku_e_Loja_independentemente()
    {
        var s1 = SerieIndexada(40); // SKU1 loja 1
        var s2 = SerieIndexada(40).Select(o => o with { Sku = "SKU2", LojaId = 2 }).ToList();

        var fvs = new FeatureBuilder().Build(s1.Concat(s2)).ToList();

        fvs.Should().Contain(f => f.Sku == "SKU1" && f.LojaId == 1);
        fvs.Should().Contain(f => f.Sku == "SKU2" && f.LojaId == 2);
        // Cada série gera (40 - 34) = 6 alvos.
        fvs.Count(f => f.Sku == "SKU1").Should().Be(6);
        fvs.Count(f => f.Sku == "SKU2").Should().Be(6);
    }

    [Fact]
    public void Preco_congelado_nao_deixa_remarcacao_do_dia_alvo_entrar_na_linha()
    {
        // Serie em rampa (10, 11, 12...) ate 04/02 (indice 34) e remarcada para 4,00
        // dali em diante. O corte cai em 04/02: nenhum dia-alvo a partir do corte pode
        // carregar o preco remarcado, porque o desconto so foi conhecido no proprio dia
        // da venda. A rampa (em vez de um preco pre-corte fixo) faz a media rolling da
        // ancora discriminar tambem o COMPRIMENTO da janela: com preco fixo, qualquer
        // janela pre-corte — certa ou errada — daria a mesma media, e um bug de janela
        // passaria batido.
        const int diaDoCorte = 34;
        var corte = Origem.AddDays(diaDoCorte);

        var serie = SerieIndexada(45, i => new DailyObservation
        {
            Data = Origem, LojaId = 1, Sku = "SKU1",
            Quantidade = 1m,
            PrecoUnitario = i >= diaDoCorte ? 4m : 10m + i,
        });

        var cfg = new FeatureConfig { PrecoCongeladoAPartirDe = corte };
        var fvs = new FeatureBuilder(cfg).BuildSeries(serie).ToList();

        // Ancora esperada, calculada independente do FeatureBuilder: ultimo preco antes
        // do corte (indice 33) e a media rolling de 28 dias terminando nele (6..33).
        var precoAncora = 10m + (diaDoCorte - 1);
        var mediaJanela = Enumerable.Range(diaDoCorte - 28, 28).Select(i => 10m + i).Average();
        var relativoAncora = precoAncora / mediaJanela;

        fvs.Should().NotBeEmpty();
        fvs.Should().OnlyContain(f => f.Data >= corte);
        fvs.Should().OnlyContain(f => f.PrecoUnitario == precoAncora,
            "o preco do dia-alvo tem de ser o ultimo conhecido ANTES do corte");
        fvs.Should().NotContain(f => f.PrecoUnitario == 4m);
        fvs.Should().OnlyContain(f => f.PrecoRelativoMedia == relativoAncora);
    }

    [Fact]
    public void Sem_congelamento_o_preco_realizado_do_dia_alvo_entra_na_linha()
    {
        // O congelamento e opt-in: o caminho de treino continua vendo o preco realizado.
        const int diaDoCorte = 34;

        var serie = SerieIndexada(45, i => new DailyObservation
        {
            Data = Origem, LojaId = 1, Sku = "SKU1",
            Quantidade = 1m,
            PrecoUnitario = i >= diaDoCorte ? 4m : 10m,
        });

        var fvs = new FeatureBuilder().BuildSeries(serie).ToList();

        fvs.Should().OnlyContain(f => f.PrecoUnitario == 4m);
        fvs.Should().Contain(f => f.PrecoRelativoMedia < 1m);
    }

    [Fact]
    public void Config_com_lag_menor_que_lead_time_eh_rejeitada()
    {
        var cfg = new FeatureConfig { LeadTimeDias = 7, Lags = [3, 14] };
        var act = () => new FeatureBuilder(cfg);
        act.Should().Throw<ArgumentException>().WithMessage("*leakage*");
    }

    private static DailyObservation Obs(int dayOffset, decimal qtd) => new()
    {
        Data = Origem.AddDays(dayOffset),
        LojaId = 1,
        Sku = "SKU1",
        Quantidade = qtd,
        PrecoUnitario = 10m,
        Categoria = "OTC",
        PrincipioAtivo = "Dipirona",
        ClasseAbc = "A",
        UF = "SP",
        Regiao = "Sudeste",
        PerfilLoja = "Bairro",
    };
}
