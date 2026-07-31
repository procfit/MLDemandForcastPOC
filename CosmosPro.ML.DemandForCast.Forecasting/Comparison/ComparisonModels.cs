using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Forecasting.Comparison;

/// <summary>
/// Como um dia em ruptura (estoque zerado) entra na apuração da demanda real.
/// Venda observada em dia de ruptura subestima a demanda — o item não vendeu
/// porque não estava lá (CLAUDE.md §6).
/// </summary>
public enum RupturaTratamento
{
    /// <summary>
    /// <b>Padrão e resultado de manchete.</b> Qualquer ruptura na janela invalida o par
    /// inteiro. É o único modo em que os dois braços são pontuados sobre exatamente o
    /// mesmo conjunto de dias: numa janela limpa não há seleção que um braço acompanhe
    /// e o outro não.
    ///
    /// <para>
    /// Custa população — ruptura é frequente em farma —, e por isso
    /// <c>ParesDescartados</c> precisa ser reportado junto com <c>ParesAvaliados</c>.
    /// O custo é aceito porque a alternativa não é comparável (ver
    /// <see cref="ExcluirDia"/>).
    /// </para>
    /// </summary>
    ExcluirPar,

    /// <summary>
    /// <b>Análise de sensibilidade, nunca o número de manchete.</b> O dia em ruptura sai
    /// da conta: a demanda real é a média dos dias sobreviventes e a previsão do ML é
    /// promediada sobre esses mesmos dias.
    ///
    /// <para>
    /// Parece simétrico e não é. O ML tem uma previsão por dia e é reprojetado sobre o
    /// subconjunto que sobreviveu; o ERP entrega um único escalar para a janela toda e
    /// não tem como se reprojetar. Como ruptura correlaciona com demanda alta, a
    /// seleção é feita sobre um desfecho, e só um dos braços se adapta a ela — um ML
    /// que errasse feio exatamente nos dias descartados sairia impune. Verdade
    /// compartilhada não é viés compartilhado quando os braços têm graus de liberdade
    /// diferentes em relação à seleção.
    /// </para>
    ///
    /// <para>
    /// Continua disponível porque preserva população e responde a uma pergunta
    /// legítima ("e se a ruptura fosse só ruído de observação?"), desde que lida como
    /// sensibilidade e apresentada ao lado de <see cref="ExcluirPar"/>.
    /// </para>
    /// </summary>
    ExcluirDia,

    /// <summary>
    /// Análise de sensibilidade apenas: pontua a venda observada como se fosse
    /// demanda. Sabidamente enviesado para baixo — é o piso pessimista da demanda
    /// real, útil para delimitar o intervalo entre "ruptura não existe" e
    /// <see cref="ExcluirPar"/>. Nunca use como resultado.
    /// </summary>
    Incluir
}

/// <summary>
/// Unidade em que as métricas de erro de um resultado foram apuradas. Existe porque
/// MAE/RMSE só são comparáveis entre painéis quando o ponto de erro é o mesmo — e o
/// deste comparador não é o do <c>WalkForwardBacktest</c>.
/// </summary>
public enum UnidadeMetrica
{
    /// <summary>
    /// Um ponto de erro por (dia, loja, sku) — a unidade do <c>WalkForwardBacktest</c>.
    /// </summary>
    ErroPorDia,

    /// <summary>
    /// Um ponto de erro por par (sugestão, loja, sku), com previsão e verdade
    /// promediadas sobre a janela avaliada. A média encolhe a variância: para o MESMO
    /// modelo, o MAE/RMSE nesta unidade sai sistematicamente MENOR que o MAE/RMSE em
    /// <see cref="ErroPorDia"/>. Os dois números não podem ser lidos lado a lado como
    /// se medissem a mesma coisa.
    /// </summary>
    ErroPorParNaJanela
}

public sealed record ComparisonOptions
{
    /// <summary>
    /// Lead time (dias) do feature engineering de F5 — ver <c>FeatureConfig.LeadTimeDias</c>.
    /// Define a observação mais recente que alimenta um dia-alvo D: <c>D - LeadTimeDias</c>.
    /// É o que permite ao comparador checar a regra de informação sem enxergar as features.
    ///
    /// <para>
    /// <b>O default (7) não deriva de <c>FeatureConfig.LeadTimeDias</c> nem de
    /// <c>DecisionOptions.LeadTimeDias</c> — os três só coincidem porque nenhum foi
    /// mudado.</b> Quem alterar um sem os outros não falha em silêncio: o comparador
    /// aplicaria a regra de informação com um lead time diferente do que o
    /// <c>FeatureBuilder</c> de fato usou ao gerar as features da população, e a
    /// população inteira sairia recusada aqui — ruidoso, mas o acoplamento em si não é
    /// imposto em nenhum ponto único do código.
    /// </para>
    /// </summary>
    public int LeadTimeDias { get; init; } = 7;

    /// <summary>
    /// Padrão <see cref="RupturaTratamento.ExcluirPar"/> — o único modo em que os dois
    /// braços são pontuados sobre o mesmo conjunto de dias. Os demais são sensibilidade.
    /// </summary>
    public RupturaTratamento Ruptura { get; init; } = RupturaTratamento.ExcluirPar;

    /// <summary>
    /// Diferença absoluta entre os erros dos dois braços abaixo da qual o par é
    /// declarado empate. Default 1e-9 = só empate exato. Empate nunca conta como
    /// vitória, mas conta no denominador da taxa.
    /// </summary>
    public double EmpateTolerancia { get; init; } = 1e-9;
}

/// <summary>
/// Um dia-alvo da janela avaliada. <paramref name="Features"/> traz a verdade
/// (<c>Target</c> = unidades vendidas) e o sinal de ruptura (<c>IsValidTarget</c>
/// = false), na mesma convenção de F5 — não há campo redundante aqui.
/// </summary>
public readonly record struct DiaAvaliado(FeatureVector Features, double PrevisaoMl);

/// <summary>
/// Uma linha da população: o par (sugestão, loja, sku) que o ERP de fato avaliou.
/// A população entra pronta e o comparador nunca a alarga — avaliar o ML sobre
/// itens que o ERP não olhou tornaria a comparação sem sentido.
/// </summary>
public sealed record ComparisonItem
{
    public required int RedeId { get; init; }

    public required long SugestaoId { get; init; }

    /// <summary>
    /// <c>SugestoesCompra.DataHora</c>. É o corte de informação: nada que o ML usou
    /// pode ser desta data em diante.
    /// </summary>
    public required DateTime DataHora { get; init; }

    /// <summary>
    /// Última data cujo dado entrou no ajuste do modelo de ML que produziu
    /// <c>DiaAvaliado.PrevisaoMl</c>. Precisa ser estritamente anterior a
    /// <see cref="DataHora"/>: sem esse campo, um modelo ajustado sobre o período
    /// inteiro passaria calado por todas as demais checagens — a regra de informação
    /// olha as features do dia-alvo, não o conjunto de treino.
    /// </summary>
    public required DateOnly ModeloTreinadoAte { get; init; }

    /// <summary>
    /// Declaração do chamador de que as features de todo dia-alvo deste par foram
    /// geradas com <c>FeatureConfig.PrecoCongeladoAPartirDe</c> igual a esta data.
    /// Precisa ser igual à data de <see cref="DataHora"/>: assim como
    /// <see cref="ModeloTreinadoAte"/> fecha o buraco do treino, este campo fecha o
    /// buraco do preço — sem ele, uma população montada com <c>FeatureConfig</c>
    /// default (sem congelamento) passaria calada por todas as demais checagens, e o
    /// preço realizado do próprio dia-alvo voltaria a vazar para a previsão do ML.
    ///
    /// <para>
    /// O comparador valida a igualdade, não a gera nem a infere: é uma DECLARAÇÃO de
    /// quem montou a população, não uma prova de que o <c>FeatureBuilder</c> de fato
    /// aplicou o congelamento ao produzir as features que alimentaram
    /// <c>DiaAvaliado.PrevisaoMl</c>. Ver <see cref="ForecastVsErpComparer"/>, "O que
    /// este comparador NÃO consegue verificar".
    /// </para>
    /// </summary>
    public required DateOnly PrecoCongeladoAPartirDe { get; init; }

    /// <summary>1 = "Emax e Eseg", 2 = "Dias de Reposição". São baselines distintos.</summary>
    public required byte TipoCalculo { get; init; }

    public required int LojaId { get; init; }

    public required string Sku { get; init; }

    /// <summary><c>SugestoesCompraItens.DemandaDia</c> — a previsão do próprio ERP, unidades/dia.</summary>
    public required double DemandaDiaErp { get; init; }

    /// <summary>Curva de giro atribuída pelo ERP (A..E). Parametriza a cobertura dele.</summary>
    public string Curva { get; init; } = "";

    /// <summary>Dias-alvo da janela, com a previsão do ML para cada um. Sem datas repetidas.</summary>
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
/// Métricas de um braço. Mesma forma de <c>BacktestResult</c> (Global + PorDimensao)
/// para a UI renderizar backtest e comparação com o mesmo código — mas não na mesma
/// unidade: ver <c>ComparisonResult.Unidade</c>.
/// </summary>
public sealed record ArmResult(
    string Nome,
    ForecastMetrics Global,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ForecastMetrics>> PorDimensao);

/// <summary>Placar de pares. Empate entra em <see cref="N"/>, nunca em vitória.</summary>
public sealed record WinRate(int N, int VitoriasMl, int VitoriasErp, int Empates)
{
    public double TaxaVitoriaMl => N == 0 ? 0 : (double)VitoriasMl / N;

    public double TaxaVitoriaErp => N == 0 ? 0 : (double)VitoriasErp / N;
}

/// <param name="ParesDescartados">
/// Pares da população que ficaram sem nenhum dia pontuável (ruptura na janela sob
/// <c>ExcluirPar</c>, ruptura em toda a janela sob <c>ExcluirDia</c>, ou janela
/// vazia). Reportar junto com <paramref name="ParesAvaliados"/>: se a fração
/// descartada for alta, a comparação perde poder e o número precisa ser lido com
/// essa ressalva.
/// </param>
/// <param name="Unidade">
/// Sempre <see cref="UnidadeMetrica.ErroPorParNaJanela"/>. Rotulado explicitamente
/// porque <c>BacktestResult</c> traz as MESMAS métricas em
/// <see cref="UnidadeMetrica.ErroPorDia"/>, e promediar a janela encolhe a variância:
/// o MAE daqui sai menor que o MAE do backtest para o mesmo modelo, sem que o modelo
/// tenha melhorado. Quem renderizar os dois painéis precisa exibir este rótulo.
/// </param>
public sealed record ComparisonResult(
    int ParesAvaliados,
    int ParesDescartados,
    UnidadeMetrica Unidade,
    ArmResult Erp,
    ArmResult Ml,
    WinRate Vitoria,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, WinRate>> VitoriaPorDimensao,
    IReadOnlyList<ParComparado> Detalhe);
