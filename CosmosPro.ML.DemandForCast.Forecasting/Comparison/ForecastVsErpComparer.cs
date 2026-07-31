using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Forecasting.Comparison;

/// <summary>
/// Camada A do comparativo F13: previsao do ERP (<c>SugestoesCompraItens.DemandaDia</c>)
/// contra previsao do ML, ambas julgadas pela mesma venda real. Mesma grandeza,
/// mesma data, mesma unidade (unidades/dia) — nao depende de arredondamento de
/// embalagem nem de posicao de estoque, ao contrario da quantidade de compra.
///
/// <para>
/// Duas invariantes sao verificadas e falham alto, porque sem elas o numero nao
/// significa nada:
/// <list type="bullet">
/// <item><b>Populacao</b> — so entram os pares (sugestao, loja, sku) que o ERP avaliou;
/// o comparador nunca alarga o conjunto, e recusa dia-alvo de outra loja/sku.</item>
/// <item><b>Informacao</b> — a observacao mais recente que alimenta um dia-alvo
/// (<c>D - LeadTimeDias</c>) precisa ser estritamente anterior a <c>DataHora</c> da
/// sugestao. Caso contrario o ML preveria sabendo o que o ERP nao sabia.</item>
/// </list>
/// Alem delas, a populacao precisa ser homogenea em rede e em <c>TipoCalculo</c>:
/// os dois metodos do ERP sao baselines distintos e duas redes sao dois casos.
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

        if (_opt.Ruptura == RupturaTratamento.ExcluirPar)
            return item.Dias.Any(d => !d.Features.IsValidTarget) ? [] : [.. item.Dias];

        return [.. item.Dias.Where(d => d.Features.IsValidTarget)];
    }

    private void ValidarItem(ComparisonItem item)
    {
        // O corte cai na data da sugestao: exigir informacao ESTRITAMENTE anterior
        // significa que nem o proprio dia da sugestao pode alimentar a previsao —
        // as vendas dele so se fecham depois do instante em que o ERP calculou.
        var corte = DateOnly.FromDateTime(item.DataHora);

        foreach (var dia in item.Dias)
        {
            var f = dia.Features;

            if (f.LojaId != item.LojaId || !string.Equals(f.Sku, item.Sku, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Regra de populacao violada: o par (sugestao {item.SugestaoId}, loja {item.LojaId}, " +
                    $"sku {item.Sku}) recebeu um dia-alvo de (loja {f.LojaId}, sku {f.Sku}).",
                    ParamPopulacao);

            var observacaoAte = f.Data.AddDays(-_opt.LeadTimeDias);
            if (observacaoAte >= corte)
                throw new ArgumentException(
                    $"Regra de informacao violada: o dia-alvo {f.Data:yyyy-MM-dd} de (loja {f.LojaId}, " +
                    $"sku {f.Sku}) e alimentado por observacao ate {observacaoAte:yyyy-MM-dd}, que nao e " +
                    $"anterior a DataHora da sugestao ({corte:yyyy-MM-dd}).",
                    ParamPopulacao);
        }
    }

    private static void ValidarHomogeneidade(IReadOnlyList<ComparisonItem> populacao)
    {
        if (populacao.Count == 0) return;

        var rede = populacao[0].RedeId;
        var tipo = populacao[0].TipoCalculo;

        foreach (var item in populacao)
        {
            if (item.RedeId != rede)
                throw new ArgumentException(
                    $"A populacao mistura as redes {rede} e {item.RedeId}. Com duas redes o estudo e " +
                    "multi-caso, nao amostra — o resultado sai sempre por rede.",
                    ParamPopulacao);

            if (item.TipoCalculo != tipo)
                throw new ArgumentException(
                    $"A populacao mistura TipoCalculo {tipo} e {item.TipoCalculo}. Sao baselines " +
                    "distintos do ERP; media entre eles nao significa nada.",
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

    // Mesmas dimensoes de WalkForwardBacktest (Categoria, ClasseAbc, Loja, UF) mais a
    // curva que o proprio ERP atribuiu — e o eixo em que o metodo antigo se parametriza,
    // logo o eixo em que se espera que ele seja mais competitivo.
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, T>> AgregarPorDimensao<T>(
        List<Par> pares, Func<IEnumerable<Par>, T> agregador) =>
        new Dictionary<string, IReadOnlyDictionary<string, T>>
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
    /// Um dia-alvo qualquer do par, usado so para ler a hierarquia (categoria, curva
    /// ABC, UF) — que e constante dentro de um par (loja, sku).
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
