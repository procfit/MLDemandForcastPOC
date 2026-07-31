using CosmosPro.ML.DemandForCast.Features.Models;

namespace CosmosPro.ML.DemandForCast.Features;

/// <summary>
/// Constrói <see cref="FeatureVector"/> a partir de séries diárias de
/// <see cref="DailyObservation"/>. Regra central: nenhuma feature de histórico
/// (lags/rolling) usa dados mais recentes que D - LeadTimeDias — anti-leakage
/// rígido (CLAUDE.md §6). Calendário e promoção do próprio dia-alvo D são
/// tratados como conhecidos (planejados com antecedência).
///
/// <para>
/// O preço é o ponto sensível: o que chega aqui é o preço médio REALIZADO da venda
/// do dia, não um preço de tabela. Serve para treino, mas para avaliação contra um
/// baseline que não dispunha dessa informação use
/// <see cref="FeatureConfig.PrecoCongeladoAPartirDe"/>.
/// </para>
/// </summary>
public sealed class FeatureBuilder(FeatureConfig? config = null)
{
    private readonly FeatureConfig _cfg = Configure(config);

    private static FeatureConfig Configure(FeatureConfig? config)
    {
        var cfg = config ?? new FeatureConfig();
        cfg.Validate();
        return cfg;
    }

    /// <summary>
    /// Gera features para uma coleção de observações que pode misturar vários
    /// SKUs/lojas. Agrupa por (Sku, LojaId), densifica gaps e produz uma linha por
    /// dia-alvo com histórico suficiente.
    /// </summary>
    public IEnumerable<FeatureVector> Build(IEnumerable<DailyObservation> observations)
    {
        foreach (var grupo in observations.GroupBy(o => (o.Sku, o.LojaId)))
        {
            foreach (var fv in BuildSeries(grupo))
                yield return fv;
        }
    }

    /// <summary>
    /// Gera features para UMA série (mesmo SKU+loja). A série é densificada
    /// (dias faltantes viram Quantidade 0) entre a menor e a maior data.
    /// </summary>
    public IEnumerable<FeatureVector> BuildSeries(IEnumerable<DailyObservation> serie)
    {
        var ordered = serie.OrderBy(o => o.Data).ToList();
        if (ordered.Count == 0) yield break;

        var densa = Densify(ordered);

        // Índice por offset de dia a partir do início, para lookup O(1) dos lags.
        var inicio = densa[0].Data;
        // Posição i corresponde a inicio.AddDays(i).
        var qtd = densa.Select(o => o.Quantidade).ToArray();

        int historicoMin = _cfg.HistoricoMinimoDias;

        var corteDePreco = _cfg.PrecoCongeladoAPartirDe;
        var ancora = corteDePreco is { } corte ? AncoraDePreco(densa, corte, _cfg.RollingLongo) : default;

        for (int d = historicoMin; d < densa.Count; d++)
        {
            var alvo = densa[d];

            var lag7 = QtdAt(qtd, d, _cfg.Lags[0]);
            var lags = _cfg.Lags.Select(l => QtdAt(qtd, d, l)).ToArray();

            // Janela rolling termina em D - LeadTimeDias (inclusive) e volta N dias.
            var (mean7, _, _) = RollingStats(qtd, d, _cfg.LeadTimeDias, _cfg.RollingCurto);
            var (mean28, std28, max28) = RollingStats(qtd, d, _cfg.LeadTimeDias, _cfg.RollingLongo);

            var congelado = corteDePreco is { } c && alvo.Data >= c;

            var precoMediaRecente = RollingPrecoMedia(densa, d, _cfg.LeadTimeDias, _cfg.RollingLongo);
            var precoAlvo = congelado ? ancora.Preco : alvo.PrecoUnitario;
            var precoRel = congelado
                ? ancora.Relativo
                : (precoMediaRecente > 0 ? alvo.PrecoUnitario / precoMediaRecente : 1m);

            yield return new FeatureVector
            {
                Data = alvo.Data,
                LojaId = alvo.LojaId,
                Sku = alvo.Sku,
                Target = alvo.Quantidade,
                IsValidTarget = !alvo.EmRuptura,

                Lag7 = lags.Length > 0 ? lags[0] : 0,
                Lag14 = lags.Length > 1 ? lags[1] : 0,
                Lag21 = lags.Length > 2 ? lags[2] : 0,
                Lag28 = lags.Length > 3 ? lags[3] : 0,

                RollMean7 = mean7,
                RollMean28 = mean28,
                RollStd28 = std28,
                RollMax28 = max28,

                DiaDaSemana = (int)alvo.Data.DayOfWeek,
                DiaDoMes = alvo.Data.Day,
                Mes = alvo.Data.Month,
                FimDeSemana = alvo.Data.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                Feriado = BrazilianHolidays.IsHoliday(alvo.Data),

                EmPromocao = alvo.EmPromocao,
                DiasDesdeUltimaPromo = DiasDesdeUltimaPromo(densa, d, _cfg.LeadTimeDias),
                PrecoUnitario = precoAlvo,
                PrecoRelativoMedia = precoRel,

                Categoria = alvo.Categoria,
                PrincipioAtivo = alvo.PrincipioAtivo,
                ClasseAbc = alvo.ClasseAbc,
                UF = alvo.UF,
                Regiao = alvo.Regiao,
                PerfilLoja = alvo.PerfilLoja,
            };
        }
    }

    /// <summary>Preenche dias faltantes entre a primeira e última data com Quantidade 0.</summary>
    private static List<DailyObservation> Densify(List<DailyObservation> ordered)
    {
        var first = ordered[0];
        var last = ordered[^1];
        var byDate = new Dictionary<DateOnly, DailyObservation>();
        foreach (var o in ordered) byDate[o.Data] = o; // último vence em duplicata

        var result = new List<DailyObservation>();
        for (var dia = first.Data; dia <= last.Data; dia = dia.AddDays(1))
        {
            if (byDate.TryGetValue(dia, out var obs))
            {
                result.Add(obs);
            }
            else
            {
                // Dia sem registro = sem venda. Herda atributos estáticos da série.
                result.Add(new DailyObservation
                {
                    Data = dia,
                    LojaId = first.LojaId,
                    Sku = first.Sku,
                    Quantidade = 0,
                    EmRuptura = false,
                    EmPromocao = false,
                    PrecoUnitario = 0,
                    Categoria = first.Categoria,
                    PrincipioAtivo = first.PrincipioAtivo,
                    ClasseAbc = first.ClasseAbc,
                    UF = first.UF,
                    Regiao = first.Regiao,
                    PerfilLoja = first.PerfilLoja,
                });
            }
        }
        return result;
    }

    private static decimal QtdAt(decimal[] qtd, int targetIdx, int lagDias)
    {
        int idx = targetIdx - lagDias;
        return idx >= 0 ? qtd[idx] : 0m;
    }

    /// <summary>
    /// Estatísticas (média, desvio populacional, máximo) de uma janela de
    /// <paramref name="window"/> dias terminando em targetIdx - offset (inclusive).
    /// </summary>
    private static (decimal Mean, decimal Std, decimal Max) RollingStats(
        decimal[] qtd, int targetIdx, int offset, int window)
    {
        int fim = targetIdx - offset;          // índice mais recente permitido
        int ini = fim - window + 1;
        if (fim < 0) return (0, 0, 0);
        if (ini < 0) ini = 0;

        decimal soma = 0, max = 0;
        int n = 0;
        for (int i = ini; i <= fim; i++)
        {
            soma += qtd[i];
            if (qtd[i] > max) max = qtd[i];
            n++;
        }
        if (n == 0) return (0, 0, 0);
        var mean = soma / n;

        decimal somaSq = 0;
        for (int i = ini; i <= fim; i++)
        {
            var diff = qtd[i] - mean;
            somaSq += diff * diff;
        }
        var std = (decimal)Math.Sqrt((double)(somaSq / n));
        return (mean, std, max);
    }

    /// <summary>
    /// Estado de preço da série no instante do corte: último preço praticado
    /// estritamente antes de <paramref name="corte"/> e sua razão contra a média
    /// rolling que termina no último dia antes do corte. Os dois valores são
    /// carregados para a frente em todo dia-alvo a partir do corte, então nenhuma
    /// informação de preço posterior ao corte alcança a linha de features.
    ///
    /// <para>
    /// Preço zero quando a série não tem nenhum dia com venda antes do corte — mesma
    /// leitura que um dia densificado sem venda.
    /// </para>
    ///
    /// <para>
    /// Muda o invariante da razão para o dia-alvo igual ao corte: no caminho sem
    /// congelamento a janela rolling de <c>PrecoRelativoMedia</c> termina em
    /// <c>D - LeadTimeDias</c>; aqui ela termina em <c>corte - 1</c>, que para
    /// <c>D == corte</c> é uma janela mais recente. É defensável — o ERP também
    /// calculou no instante do corte, não <c>LeadTimeDias</c> antes dele —, mas é uma
    /// mudança deliberada de qual janela alimenta a razão, não uma continuação do
    /// invariante do caminho de treino.
    /// </para>
    /// </summary>
    private static (decimal Preco, decimal Relativo) AncoraDePreco(
        List<DailyObservation> densa, DateOnly corte, int window)
    {
        int ultimo = -1;
        for (int i = densa.Count - 1; i >= 0; i--)
        {
            if (densa[i].Data < corte) { ultimo = i; break; }
        }
        if (ultimo < 0) return (0m, 1m);

        decimal preco = 0m;
        for (int i = ultimo; i >= 0; i--)
        {
            if (densa[i].PrecoUnitario > 0) { preco = densa[i].PrecoUnitario; break; }
        }

        var media = RollingPrecoMedia(densa, ultimo, 0, window);
        return (preco, media > 0 ? preco / media : 1m);
    }

    private static decimal RollingPrecoMedia(List<DailyObservation> densa, int targetIdx, int offset, int window)
    {
        int fim = targetIdx - offset;
        int ini = fim - window + 1;
        if (fim < 0) return 0;
        if (ini < 0) ini = 0;

        decimal soma = 0;
        int n = 0;
        for (int i = ini; i <= fim; i++)
        {
            // Só considera dias com preço > 0 (dias com venda); ignora zeros.
            if (densa[i].PrecoUnitario > 0)
            {
                soma += densa[i].PrecoUnitario;
                n++;
            }
        }
        return n > 0 ? soma / n : 0m;
    }

    private static int DiasDesdeUltimaPromo(List<DailyObservation> densa, int targetIdx, int offset)
    {
        int fim = targetIdx - offset;
        for (int i = fim; i >= 0; i--)
        {
            if (densa[i].EmPromocao)
                return targetIdx - i;
        }
        return -1; // nunca houve promoção no histórico conhecido
    }
}
