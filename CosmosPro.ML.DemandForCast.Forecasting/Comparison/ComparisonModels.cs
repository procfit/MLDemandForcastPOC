using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Forecasting.Comparison;

/// <summary>
/// Como um dia em ruptura (estoque zerado) entra na apuracao da demanda real.
/// Venda observada em dia de ruptura subestima a demanda — o item nao vendeu
/// porque nao estava la (CLAUDE.md §6).
/// </summary>
public enum RupturaTratamento
{
    /// <summary>
    /// Padrao. O dia sai da conta dos DOIS bracos: a demanda real e a media
    /// dos dias sem ruptura, e a previsao do ML e promediada sobre os mesmos dias.
    /// Mesma politica de mascaramento de <c>WalkForwardBacktest</c>.
    /// </summary>
    ExcluirDia,

    /// <summary>
    /// Leitura conservadora: qualquer ruptura na janela invalida o par inteiro.
    /// Util quando se suspeita que a ruptura deprimiu a demanda tambem nos dias
    /// vizinhos (cliente que nao achou e nao voltou).
    /// </summary>
    ExcluirPar,

    /// <summary>
    /// Analise de sensibilidade apenas: pontua a venda observada como se fosse
    /// demanda. Sabidamente enviesado para baixo — nunca use como resultado.
    /// </summary>
    Incluir
}

public sealed record ComparisonOptions
{
    /// <summary>
    /// Lead time (dias) do feature engineering de F5 — ver <c>FeatureConfig.LeadTimeDias</c>.
    /// Define a observacao mais recente que alimenta um dia-alvo D: <c>D - LeadTimeDias</c>.
    /// E o que permite ao comparador checar a regra de informacao sem enxergar as features.
    /// </summary>
    public int LeadTimeDias { get; init; } = 7;

    public RupturaTratamento Ruptura { get; init; } = RupturaTratamento.ExcluirDia;

    /// <summary>
    /// Diferenca absoluta entre os erros dos dois bracos abaixo da qual o par e
    /// declarado empate. Default 1e-9 = so empate exato. Empate nunca conta como
    /// vitoria, mas conta no denominador da taxa.
    /// </summary>
    public double EmpateTolerancia { get; init; } = 1e-9;
}

/// <summary>
/// Um dia-alvo da janela avaliada. <paramref name="Features"/> traz a verdade
/// (<c>Target</c> = unidades vendidas) e o sinal de ruptura (<c>IsValidTarget</c>
/// = false), na mesma convencao de F5 — nao ha campo redundante aqui.
/// </summary>
public readonly record struct DiaAvaliado(FeatureVector Features, double PrevisaoMl);

/// <summary>
/// Uma linha da populacao: o par (sugestao, loja, sku) que o ERP de fato avaliou.
/// A populacao entra pronta e o comparador nunca a alarga — avaliar o ML sobre
/// itens que o ERP nao olhou tornaria a comparacao sem sentido.
/// </summary>
public sealed record ComparisonItem
{
    public required int RedeId { get; init; }

    public required long SugestaoId { get; init; }

    /// <summary>
    /// <c>SugestoesCompra.DataHora</c>. E o corte de informacao: nada que o ML usou
    /// pode ser desta data em diante.
    /// </summary>
    public required DateTime DataHora { get; init; }

    /// <summary>1 = "Emax e Eseg", 2 = "Dias de Reposicao". Sao baselines distintos.</summary>
    public required byte TipoCalculo { get; init; }

    public required int LojaId { get; init; }

    public required string Sku { get; init; }

    /// <summary><c>SugestoesCompraItens.DemandaDia</c> — a previsao do proprio ERP, unidades/dia.</summary>
    public required double DemandaDiaErp { get; init; }

    /// <summary>Curva de giro atribuida pelo ERP (A..E). Parametriza a cobertura dele.</summary>
    public string Curva { get; init; } = "";

    /// <summary>Dias-alvo da janela, com a previsao do ML para cada um.</summary>
    public required IReadOnlyList<DiaAvaliado> Dias { get; init; }
}

public enum ResultadoPar
{
    VitoriaMl,
    VitoriaErp,
    Empate
}

/// <summary>Resultado de um par item x loja. Tudo em unidades/dia.</summary>
public sealed record ParComparado(
    long SugestaoId,
    int LojaId,
    string Sku,
    int DiasAvaliados,
    double DemandaDiaReal,
    double DemandaDiaErp,
    double DemandaDiaMl,
    double ErroAbsErp,
    double ErroAbsMl,
    ResultadoPar Resultado);

/// <summary>
/// Metricas de um braco. Mesma forma de <c>BacktestResult</c> (Global + PorDimensao)
/// para a UI renderizar backtest e comparacao com o mesmo codigo.
/// </summary>
public sealed record ArmResult(
    string Nome,
    ForecastMetrics Global,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ForecastMetrics>> PorDimensao);

/// <summary>Placar de pares. Empate entra em <see cref="N"/>, nunca em vitoria.</summary>
public sealed record WinRate(int N, int VitoriasMl, int VitoriasErp, int Empates)
{
    public double TaxaVitoriaMl => N == 0 ? 0 : (double)VitoriasMl / N;

    public double TaxaVitoriaErp => N == 0 ? 0 : (double)VitoriasErp / N;
}

/// <param name="ParesDescartados">
/// Pares da populacao que ficaram sem nenhum dia pontuavel (ruptura em toda a
/// janela, ou janela vazia). Reportar junto com <paramref name="ParesAvaliados"/>:
/// se a fracao descartada for alta, a comparacao perde poder e o numero precisa
/// ser lido com essa ressalva.
/// </param>
public sealed record ComparisonResult(
    int ParesAvaliados,
    int ParesDescartados,
    ArmResult Erp,
    ArmResult Ml,
    WinRate Vitoria,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, WinRate>> VitoriaPorDimensao,
    IReadOnlyList<ParComparado> Detalhe);
