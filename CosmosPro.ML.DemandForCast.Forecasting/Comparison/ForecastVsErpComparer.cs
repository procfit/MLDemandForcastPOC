using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Forecasting.Comparison;

/// <summary>
/// Camada A do comparativo F13: previsão do ERP (<c>SugestoesCompraItens.DemandaDia</c>)
/// contra previsão do ML, ambas julgadas pela mesma venda real. Mesma grandeza,
/// mesma data, mesma unidade (unidades/dia) — não depende de arredondamento de
/// embalagem nem de posição de estoque, ao contrário da quantidade de compra.
///
/// <para>
/// Um ponto de erro por par, não por dia: ver <c>ComparisonResult.Unidade</c> antes de
/// comparar estes números com os do <c>WalkForwardBacktest</c>.
/// </para>
///
/// <para>
/// Invariantes verificadas, todas falhando alto, porque sem elas o número não
/// significa nada:
/// <list type="bullet">
/// <item><b>População</b> — só entram os pares (sugestão, loja, sku) que o ERP avaliou;
/// o comparador nunca alarga o conjunto, recusa dia-alvo de outra loja/sku, e recusa
/// par duplicado ou dia repetido dentro de um par.</item>
/// <item><b>Informação</b> — a observação mais recente que alimenta as features de
/// histórico de um dia-alvo (<c>D - LeadTimeDias</c>) precisa ser estritamente anterior
/// a <c>DataHora</c> da sugestão, e o modelo precisa ter sido treinado até uma data
/// também estritamente anterior. Caso contrário o ML preveria sabendo o que o ERP não
/// sabia.</item>
/// <item><b>Números utilizáveis</b> — previsão não finita (NaN/infinito) em qualquer
/// braço interrompe a execução em vez de virar derrota silenciosa daquele braço.</item>
/// <item><b>Hierarquia constante</b> — categoria, classe ABC e UF precisam ser as
/// mesmas em todos os dias do par, senão a quebra por dimensão dependeria da ordem em
/// que o chamador passou os dias.</item>
/// </list>
/// Além delas, a população precisa ser homogênea em rede e em <c>TipoCalculo</c>:
/// os dois métodos do ERP são baselines distintos e duas redes são dois casos.
/// </para>
///
/// <para>
/// <b>O que este comparador NÃO consegue verificar:</b> ele recebe previsões prontas e
/// não enxerga as features que as produziram. A checagem <c>D - LeadTimeDias</c> cobre
/// lags e rolling, que é o que F5 amarra ao lead time. Calendário e promoção do próprio
/// D são planejados e legitimamente conhecidos. <b>Preço não é</b>: o
/// <c>PrecoUnitario</c> que o <c>FeatureBuilder</c> coloca na linha do dia-alvo é o
/// preço médio realizado da venda daquele dia, então uma remarcação não planejada
/// entraria como informação do futuro na previsão do próprio dia pontuado. Quem monta a
/// população é responsável por gerar as features com
/// <c>FeatureConfig.PrecoCongeladoAPartirDe = DateOnly.FromDateTime(DataHora)</c>; o
/// comparador não tem como conferir isso a partir de um escalar de previsão.
/// </para>
/// </summary>
public sealed class ForecastVsErpComparer(ComparisonOptions? options = null)
{
    private const string NomeErp = "erp-pbs";
    private const string NomeMl = "ml";
    private const string ParamPopulacao = "populacao";

    private readonly ComparisonOptions _opt = options ?? new ComparisonOptions();

    public ComparisonResult Compare(IReadOnlyList<ComparisonItem> populacao)
    {
        ValidarHomogeneidade(populacao);

        var pares = new List<Par>(populacao.Count);
        var descartados = 0;

        foreach (var item in populacao)
        {
            ValidarItem(item);

            var dias = DiasPontuaveis(item);
            if (dias.Count == 0)
            {
                descartados++;
                continue;
            }

            var real = dias.Average(d => (double)d.Features.Target);
            var ml = Math.Max(0, dias.Average(d => d.PrevisaoMl));
            var erp = Math.Max(0, item.DemandaDiaErp);

            var erroErp = Math.Abs(erp - real);
            var erroMl = Math.Abs(ml - real);

            pares.Add(new Par(item, dias[0].Features, dias.Count, real, erp, ml, erroErp, erroMl,
                Julgar(erroMl, erroErp)));
        }

        var erpArm = new ArmResult(
            NomeErp,
            ForecastMetrics.Compute(pares.Select(p => (p.Real, p.Erp)).ToList()),
            AgregarPorDimensao(pares, g => ForecastMetrics.Compute(g.Select(p => (p.Real, p.Erp)).ToList())));

        var mlArm = new ArmResult(
            NomeMl,
            ForecastMetrics.Compute(pares.Select(p => (p.Real, p.Ml)).ToList()),
            AgregarPorDimensao(pares, g => ForecastMetrics.Compute(g.Select(p => (p.Real, p.Ml)).ToList())));

        return new ComparisonResult(
            pares.Count,
            descartados,
            UnidadeMetrica.ErroPorParNaJanela,
            erpArm,
            mlArm,
            Placar(pares),
            AgregarPorDimensao(pares, Placar),
            pares.Select(p => new ParComparado(
                p.Item.SugestaoId, p.Item.LojaId, p.Item.Sku, p.Dias,
                p.Real, p.Erp, p.Ml, p.ErroErp, p.ErroMl, p.Resultado)).ToList());
    }

    private ResultadoPar Julgar(double erroMl, double erroErp)
    {
        if (Math.Abs(erroMl - erroErp) <= _opt.EmpateTolerancia) return ResultadoPar.Empate;
        return erroMl < erroErp ? ResultadoPar.VitoriaMl : ResultadoPar.VitoriaErp;
    }

    private List<DiaAvaliado> DiasPontuaveis(ComparisonItem item)
    {
        if (_opt.Ruptura == RupturaTratamento.Incluir)
            return [.. item.Dias];

        if (_opt.Ruptura == RupturaTratamento.ExcluirDia)
            return [.. item.Dias.Where(d => d.Features.IsValidTarget)];

        return item.Dias.Any(d => !d.Features.IsValidTarget) ? [] : [.. item.Dias];
    }

    private void ValidarItem(ComparisonItem item)
    {
        // O corte cai na data da sugestão: exigir informação ESTRITAMENTE anterior
        // significa que nem o próprio dia da sugestão pode alimentar a previsão —
        // as vendas dele só se fecham depois do instante em que o ERP calculou.
        var corte = DateOnly.FromDateTime(item.DataHora);

        if (item.ModeloTreinadoAte >= corte)
            throw new ArgumentException(
                $"Regra de informação violada: o modelo do par (sugestão {item.SugestaoId}, " +
                $"loja {item.LojaId}, sku {item.Sku}) foi treinado até " +
                $"{item.ModeloTreinadoAte:yyyy-MM-dd}, que não é anterior à DataHora da sugestão " +
                $"({corte:yyyy-MM-dd}). Um modelo ajustado sobre o período avaliado não é um " +
                "concorrente do ERP, é um oráculo.",
                ParamPopulacao);

        if (!double.IsFinite(item.DemandaDiaErp))
            throw new ArgumentException(
                $"Previsão não finita ({item.DemandaDiaErp}) do braço ERP no par " +
                $"(sugestão {item.SugestaoId}, loja {item.LojaId}, sku {item.Sku}).",
                ParamPopulacao);

        var datas = new HashSet<DateOnly>();

        foreach (var dia in item.Dias)
        {
            var f = dia.Features;

            if (f.LojaId != item.LojaId || !string.Equals(f.Sku, item.Sku, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Regra de população violada: o par (sugestão {item.SugestaoId}, loja {item.LojaId}, " +
                    $"sku {item.Sku}) recebeu um dia-alvo de (loja {f.LojaId}, sku {f.Sku}).",
                    ParamPopulacao);

            if (!datas.Add(f.Data))
                throw new ArgumentException(
                    $"Regra de população violada: o par (sugestão {item.SugestaoId}, loja {item.LojaId}, " +
                    $"sku {item.Sku}) repete o dia-alvo {f.Data:yyyy-MM-dd}. Dia repetido pesa duas vezes " +
                    "na média da janela e desloca o par sem que nada apareça no resultado.",
                    ParamPopulacao);

            if (!double.IsFinite(dia.PrevisaoMl))
                throw new ArgumentException(
                    $"Previsão não finita ({dia.PrevisaoMl}) do braço ML no dia-alvo {f.Data:yyyy-MM-dd} " +
                    $"do par (sugestão {item.SugestaoId}, loja {item.LojaId}, sku {item.Sku}). " +
                    "Comparar contra NaN faria o par virar vitória do ERP em silêncio.",
                    ParamPopulacao);

            ValidarAtributoConstante(item, f, "Categoria", item.Dias[0].Features.Categoria, f.Categoria);
            ValidarAtributoConstante(item, f, "ClasseAbc", item.Dias[0].Features.ClasseAbc, f.ClasseAbc);
            ValidarAtributoConstante(item, f, "UF", item.Dias[0].Features.UF, f.UF);

            var observacaoAte = f.Data.AddDays(-_opt.LeadTimeDias);
            if (observacaoAte >= corte)
                throw new ArgumentException(
                    $"Regra de informação violada: o dia-alvo {f.Data:yyyy-MM-dd} de (loja {f.LojaId}, " +
                    $"sku {f.Sku}) é alimentado por observação até {observacaoAte:yyyy-MM-dd}, que não é " +
                    $"anterior à DataHora da sugestão ({corte:yyyy-MM-dd}).",
                    ParamPopulacao);
        }
    }

    // A hierarquia do par é lida de um dia só; se ela variar dentro da janela — uma
    // reclassificação ABC no meio, por exemplo — o par cairia no balde que o chamador
    // ordenou primeiro, e a quebra por dimensão deixaria de reproduzir sobre os mesmos
    // dados.
    private static void ValidarAtributoConstante(
        ComparisonItem item, FeatureVector f, string atributo, string esperado, string encontrado)
    {
        if (string.Equals(esperado, encontrado, StringComparison.Ordinal)) return;

        throw new ArgumentException(
            $"Regra de população violada: o par (sugestão {item.SugestaoId}, loja {item.LojaId}, " +
            $"sku {item.Sku}) tem {atributo} \"{esperado}\" em um dia e \"{encontrado}\" no dia-alvo " +
            $"{f.Data:yyyy-MM-dd}. A quebra por dimensão depende desse atributo ser constante no par.",
            ParamPopulacao);
    }

    private static void ValidarHomogeneidade(IReadOnlyList<ComparisonItem> populacao)
    {
        if (populacao.Count == 0) return;

        var rede = populacao[0].RedeId;
        var tipo = populacao[0].TipoCalculo;
        var chaves = new HashSet<(long, int, string)>();

        foreach (var item in populacao)
        {
            if (item.RedeId != rede)
                throw new ArgumentException(
                    $"A população mistura as redes {rede} e {item.RedeId}. Com duas redes o estudo é " +
                    "multi-caso, não amostra — o resultado sai sempre por rede.",
                    ParamPopulacao);

            if (item.TipoCalculo != tipo)
                throw new ArgumentException(
                    $"A população mistura TipoCalculo {tipo} e {item.TipoCalculo}. São baselines " +
                    "distintos do ERP; média entre eles não significa nada.",
                    ParamPopulacao);

            if (!chaves.Add((item.SugestaoId, item.LojaId, item.Sku)))
                throw new ArgumentException(
                    $"A população repete o par (sugestão {item.SugestaoId}, loja {item.LojaId}, " +
                    $"sku {item.Sku}). Par duplicado pesa duas vezes na taxa de vitória e nas " +
                    "métricas — sintoma de join que multiplicou linhas.",
                    ParamPopulacao);
        }
    }

    private static WinRate Placar(IEnumerable<Par> pares)
    {
        int n = 0, ml = 0, erp = 0, empates = 0;
        foreach (var p in pares)
        {
            n++;
            switch (p.Resultado)
            {
                case ResultadoPar.VitoriaMl: ml++; break;
                case ResultadoPar.VitoriaErp: erp++; break;
                default: empates++; break;
            }
        }
        return new WinRate(n, ml, erp, empates);
    }

    // Mesmas dimensões de WalkForwardBacktest (Categoria, ClasseAbc, Loja, UF) mais a
    // curva que o próprio ERP atribuiu — é o eixo em que o método antigo se parametriza,
    // logo o eixo em que se espera que ele seja mais competitivo. Sem par pontuado sai
    // dicionário vazio, igual ao EmptyDims() do backtest.
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, T>> AgregarPorDimensao<T>(
        List<Par> pares, Func<IEnumerable<Par>, T> agregador) =>
        pares.Count == 0
            ? new Dictionary<string, IReadOnlyDictionary<string, T>>()
            : new Dictionary<string, IReadOnlyDictionary<string, T>>
            {
                ["Categoria"] = Agrupar(pares, p => p.Amostra.Categoria, agregador),
                ["ClasseAbc"] = Agrupar(pares, p => p.Amostra.ClasseAbc, agregador),
                ["Loja"] = Agrupar(pares, p => p.Item.LojaId.ToString(), agregador),
                ["UF"] = Agrupar(pares, p => p.Amostra.UF, agregador),
                ["CurvaErp"] = Agrupar(pares, p => p.Item.Curva, agregador),
            };

    private static IReadOnlyDictionary<string, T> Agrupar<T>(
        List<Par> pares, Func<Par, string> keySelector, Func<IEnumerable<Par>, T> agregador) =>
        pares.GroupBy(keySelector)
             .Where(g => !string.IsNullOrEmpty(g.Key))
             .ToDictionary(g => g.Key, g => agregador(g));

    /// <param name="Amostra">
    /// Um dia-alvo qualquer do par, usado só para ler a hierarquia (categoria, curva
    /// ABC, UF). A constância desses atributos dentro do par é verificada em
    /// <c>ValidarItem</c>, não presumida.
    /// </param>
    private readonly record struct Par(
        ComparisonItem Item,
        FeatureVector Amostra,
        int Dias,
        double Real,
        double Erp,
        double Ml,
        double ErroErp,
        double ErroMl,
        ResultadoPar Resultado);
}
