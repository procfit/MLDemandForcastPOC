using CosmosPro.ML.DemandForCast.Forecasting.Comparison;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Purchasing.Comparison;

/// <summary>
/// Se a aritmética modelada aqui reproduz o <c>CompraSugerida</c> que o ERP gravou,
/// partindo do <c>DemandaDia</c> que o próprio ERP usou. É o portão de validade da
/// camada B: sem o ERP reproduzido, trocar a demanda pela do ML não mede a previsão,
/// mede o erro do nosso modelo da aritmética dele.
/// </summary>
public enum StatusReconciliacao
{
    /// <summary>
    /// A recálculo bateu com <c>CompraSugerida</c> dentro da tolerância. Só estes itens
    /// entram na comparação agregada.
    /// </summary>
    Reconciliado,

    /// <summary>
    /// A recálculo produziu outro número. O item fica identificável em
    /// <c>DecisionComparisonResult.DetalheReconciliacao</c> com os dois valores, e é
    /// excluído da comparação — misturá-lo atribuiria à previsão uma diferença que vem
    /// do nosso desconhecimento da regra do ERP.
    /// </summary>
    Divergente,

    /// <summary>
    /// <c>TipoCalculo</c> 1 com <c>DemandaDia</c> zero: o braço ML é obtido reescalando
    /// o eMax do ERP pela razão entre as demandas, e com denominador zero a razão não
    /// existe. O braço ERP continua reconstruível (o eMax está gravado), mas o item não
    /// tem par comparável e é excluído.
    /// </summary>
    BracoMlIndeterminado
}

public sealed record DecisionOptions
{
    /// <summary>
    /// Diferença absoluta (unidades) entre o <c>CompraSugerida</c> gravado e o valor
    /// recalculado abaixo da qual o item é dado por reconciliado. O default 0,001 é uma
    /// unidade da última casa de <c>DECIMAL(15,3)</c> da coluna de origem: cobre
    /// arredondamento de transporte, não cobre divergência de regra.
    /// </summary>
    public decimal ToleranciaReconciliacao { get; init; } = 0.001m;

    /// <summary>
    /// Só <see cref="RupturaTratamento.ExcluirPar"/> (default, e o mesmo default da
    /// camada A) e <see cref="RupturaTratamento.Incluir"/> (piso pessimista, apenas
    /// sensibilidade). <see cref="RupturaTratamento.ExcluirDia"/> é recusado: aqui a
    /// decisão é um escalar único que cobre a janela inteira, então pontuá-la contra a
    /// venda de um subconjunto de dias compararia uma compra dimensionada para N dias
    /// com a demanda de menos que N.
    /// </summary>
    public RupturaTratamento Ruptura { get; init; } = RupturaTratamento.ExcluirPar;

    /// <summary>
    /// Lead time (dias) do feature engineering de F5, igual ao da camada A. Define a
    /// observação mais recente que alimenta um dia-alvo D: <c>D - LeadTimeDias</c>.
    /// </summary>
    public int LeadTimeDias { get; init; } = 7;

    /// <summary>
    /// Diferença absoluta entre os desvios dos dois braços abaixo da qual o item é
    /// declarado empate. Default 0 = só empate exato (aritmética decimal, sem ruído de
    /// ponto flutuante). Empate nunca conta como vitória, mas conta no denominador.
    /// </summary>
    public decimal EmpateTolerancia { get; init; } = 0m;
}

/// <summary>
/// Uma linha de <c>SugestoesCompraItens</c> mais os dias da janela que a compra cobre.
/// Os campos de posição de estoque, cobertura e embalagem são os do próprio ERP e são
/// usados <b>iguais nos dois braços</b> — a única variável trocada é a demanda.
/// </summary>
public sealed record DecisionItem
{
    public required int RedeId { get; init; }

    public required long SugestaoId { get; init; }

    /// <summary>
    /// <c>SugestoesCompra.DataHora</c>. É o corte de informação e o início da janela
    /// que a compra cobre.
    /// </summary>
    public required DateTime DataHora { get; init; }

    /// <summary>
    /// Última data cujo dado entrou no ajuste do modelo de ML. Precisa ser estritamente
    /// anterior a <see cref="DataHora"/> — mesma regra da camada A.
    /// </summary>
    public required DateOnly ModeloTreinadoAte { get; init; }

    /// <summary>
    /// Declaração de que as features dos dias-alvo foram geradas com
    /// <c>FeatureConfig.PrecoCongeladoAPartirDe</c> igual à data de
    /// <see cref="DataHora"/>. Mesma regra e mesma limitação da camada A: é declaração
    /// do chamador, não prova.
    /// </summary>
    public required DateOnly PrecoCongeladoAPartirDe { get; init; }

    /// <summary>1 = "Emax e Eseg", 2 = "Dias de Reposição". São baselines distintos.</summary>
    public required byte TipoCalculo { get; init; }

    public required int LojaId { get; init; }

    public required string Sku { get; init; }

    /// <summary>Curva de giro atribuída pelo ERP (A..E). Eixo de quebra do resultado.</summary>
    public string Curva { get; init; } = "";

    /// <summary><c>DemandaDia</c> — a previsão do próprio ERP, unidades/dia.</summary>
    public required decimal DemandaDiaErp { get; init; }

    /// <summary><c>EstoqueSaldo</c> no momento do cálculo. Idêntico nos dois braços.</summary>
    public required decimal EstoqueSaldo { get; init; }

    /// <summary><c>PedidosPendentes</c>. Só entra na posição quando <see cref="ConsideraPedidosPendentes"/>.</summary>
    public required decimal PedidosPendentes { get; init; }

    /// <summary>
    /// <c>SugestoesCompra.ConsideraPedidosPendentes</c> — flag do cabeçalho da sugestão.
    /// Quando falso o ERP ignora o que já está a caminho, e o braço ML tem de ignorar
    /// também.
    /// </summary>
    public bool ConsideraPedidosPendentes { get; init; } = true;

    /// <summary><c>DiasEstoque</c> — dias de cobertura da compra. Idêntico nos dois braços.</summary>
    public required short DiasEstoque { get; init; }

    /// <summary>
    /// <c>EstoqueMaximo</c> calculado pelo ERP. Obrigatório em <c>TipoCalculo</c> 1 (é o
    /// nível até onde ele repõe); vem zerado em <c>TipoCalculo</c> 2, que não o usa.
    /// </summary>
    public decimal? EstoqueMaximo { get; init; }

    /// <summary>
    /// <c>EstoqueSeguranca</c> calculado pelo ERP. Não entra na aritmética modelada:
    /// em <c>TipoCalculo</c> 1 ele já está embutido no <see cref="EstoqueMaximo"/>, e em
    /// <c>TipoCalculo</c> 2 vem zerado. Carregado para inspeção.
    /// </summary>
    public decimal? EstoqueSeguranca { get; init; }

    /// <summary><c>CompraSugerida</c> — o que o ERP mandou comprar. É o alvo da reconciliação.</summary>
    public required decimal CompraSugerida { get; init; }

    /// <summary>
    /// <c>PrecoCompra</c>. Valoriza excesso e falta. Nulo faz o item contribuir zero nos
    /// agregados em R$ — por isso <c>DecisionComparisonResult.ItensSemPrecoCompra</c>.
    /// </summary>
    public decimal? PrecoCompra { get; init; }

    /// <summary>
    /// <c>FatorEmbalagem</c> — múltiplo de compra. Nulo ou não-positivo significa "sem
    /// múltiplo", não divisão por zero.
    /// </summary>
    public decimal? FatorEmbalagem { get; init; }

    /// <summary>
    /// <c>Falteiro</c> — sinalização do ERP no momento do cálculo, anterior à janela.
    /// Não decide descarte por ruptura: a ruptura da janela vem do
    /// <c>FeatureVector.IsValidTarget</c> de cada dia, exatamente como na camada A.
    /// </summary>
    public bool Falteiro { get; init; }

    /// <summary>
    /// Os <see cref="DiasEstoque"/> dias da janela coberta pela compra, de
    /// <see cref="DataHora"/> (inclusive) até <see cref="DataHora"/> + dias (exclusive),
    /// cada um com a previsão do ML. Mesmo tipo da camada A: a verdade é
    /// <c>Features.Target</c> e a ruptura é <c>Features.IsValidTarget</c>.
    /// </summary>
    public required IReadOnlyList<DiaAvaliado> Dias { get; init; }
}

/// <summary>Reconciliação de um item: o que o ERP gravou contra o que a nossa fórmula produz.</summary>
public sealed record ItemReconciliado(
    long SugestaoId,
    int LojaId,
    string Sku,
    StatusReconciliacao Status,
    decimal CompraSugeridaErp,
    decimal CompraRecalculada,
    decimal Divergencia);

/// <summary>
/// Agregado do portão de validade, sobre <b>toda</b> a população — inclusive itens que
/// depois caem por ruptura, porque a pergunta "modelamos certo a aritmética do ERP?" não
/// depende da janela de venda.
/// </summary>
/// <param name="DivergenciaAbsMedia">
/// Média de <c>|recalculado − gravado|</c> sobre os itens em que a reconciliação foi
/// tentada. Uma taxa de concordância alta com divergência média alta nos poucos que
/// falham é um sintoma diferente de uma divergência de um centavo espalhada por todos.
/// </param>
public sealed record ReconciliacaoResumo(
    int Itens,
    int Reconciliados,
    int Divergentes,
    int BracoMlIndeterminado,
    decimal DivergenciaAbsMedia,
    decimal DivergenciaAbsMaxima)
{
    /// <summary>
    /// Fração da população em que reproduzimos o ERP. Abaixo de um patamar alto, nenhum
    /// número desta camada é apresentável.
    /// </summary>
    public double TaxaConcordancia => Itens == 0 ? 0 : (double)Reconciliados / Itens;
}

/// <summary>
/// Venda perdida. <b>Secundária e ilustrativa — nunca manchete.</b>
///
/// <para>
/// <see cref="Unidades"/> é, por construção, o mesmo
/// <c>ArmDecisionResult.FaltaUnidades</c>; o tipo existe para que a valorização em R$ não
/// possa ser lida sem a ressalva junto, e para que uma UI que renderize
/// <c>VendaPerdida</c> receba o nome e esta documentação no mesmo lugar.
/// </para>
///
/// <para>
/// Duas razões para não ser manchete. Primeira, circularidade: a demanda atribuída à
/// janela é observável só onde não houve ruptura; onde há, ela é estimada por um modelo —
/// o mesmo tipo de modelo que produziu a decisão. Sob uma regra de repor-até-o-nível, o
/// braço que prevê mais alto compra mais e por isso aparece com menos venda perdida, sem
/// que isso seja evidência de ter previsto melhor. Segunda, valorização: <c>PrecoCompra</c>
/// é custo, não receita nem margem — valorizar venda perdida a custo não é o prejuízo de
/// uma venda perdida.
/// </para>
/// </summary>
public sealed record VendaPerdidaIlustrativa(decimal Unidades, decimal Valor);

/// <summary>
/// Desfecho de um braço sobre os itens comparados. Excesso e falta são medidos contra a
/// venda real da janela, e são os números de manchete.
/// </summary>
/// <param name="ExcessoUnidades">
/// Σ max(0, posição resultante − venda real da janela): o que sobraria no fim da janela.
/// </param>
/// <param name="FaltaUnidades">
/// Σ max(0, venda real da janela − posição resultante): o que faltaria para atender a
/// venda que de fato ocorreu. Não tem par em R$ aqui de propósito — a valorização mora em
/// <see cref="VendaPerdidaIlustrativa"/>, que carrega a ressalva.
/// </param>
/// <param name="PosicaoVsVenda">
/// MAE/RMSE/WAPE da posição resultante (estoque + pendentes + compra) contra a venda real
/// da janela, um ponto de erro por item. Mesmas métricas da camada A, outra grandeza:
/// aqui é unidades na janela, não unidades/dia.
/// </param>
public sealed record ArmDecisionResult(
    string Nome,
    decimal UnidadesCompradas,
    decimal ValorComprado,
    decimal ExcessoUnidades,
    decimal ExcessoValor,
    decimal FaltaUnidades,
    VendaPerdidaIlustrativa VendaPerdida,
    ForecastMetrics PosicaoVsVenda);

/// <summary>Desfecho de um item nos dois braços. Tudo em unidades da janela.</summary>
public sealed record DecisaoComparada(
    long SugestaoId,
    int LojaId,
    string Sku,
    string Curva,
    int DiasAvaliados,
    decimal VendaRealJanela,
    decimal DemandaDiaErp,
    decimal DemandaDiaMl,
    decimal CompraErp,
    decimal CompraMl,
    decimal ExcessoErp,
    decimal ExcessoMl,
    decimal FaltaErp,
    decimal FaltaMl,
    ResultadoPar Resultado);

/// <param name="ItensComparados">
/// Itens reconciliados e com janela pontuável. É o denominador de tudo que compara os
/// dois braços.
/// </param>
/// <param name="ItensDescartadosPorRuptura">
/// Itens reconciliados cuja janela caiu pela política de ruptura. Reportar junto com
/// <paramref name="ItensComparados"/>.
/// </param>
/// <param name="ItensSemPrecoCompra">
/// Itens comparados sem <c>PrecoCompra</c>. Eles contribuem em unidades e zero em R$, o
/// que puxa os agregados monetários para baixo — sem esta contagem a queda ficaria
/// invisível.
/// </param>
public sealed record DecisionComparisonResult(
    int ItensNaPopulacao,
    int ItensComparados,
    int ItensDescartadosPorRuptura,
    int ItensSemPrecoCompra,
    ReconciliacaoResumo Reconciliacao,
    IReadOnlyList<ItemReconciliado> DetalheReconciliacao,
    ArmDecisionResult Erp,
    ArmDecisionResult Ml,
    WinRate Vitoria,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, WinRate>> VitoriaPorDimensao,
    IReadOnlyList<DecisaoComparada> Detalhe);
