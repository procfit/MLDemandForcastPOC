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

/// <summary>
/// Como o braço ML obtém o nível de reposição em <c>TipoCalculo</c> 1, dado o
/// <c>EstoqueMaximo</c> que o ERP gravou e a razão <c>r = demanda_ml ÷ DemandaDia</c>.
///
/// <para>
/// <b>As duas alternativas são suposições sobre as entranhas do ERP, nenhuma validada.</b>
/// A reconciliação não consegue arbitrar entre elas: no braço ERP <c>r = 1</c> por
/// construção e as duas colapsam no próprio <c>EstoqueMaximo</c>. Enquanto nenhuma
/// sugestão real do PBS tiver sido reconciliada, uma taxa de concordância baixa significa
/// "não modelamos o ERP", não "o ML ganhou". O modo existe para que a sensibilidade do
/// resultado a esta escolha possa ser reportada em vez de ficar embutida.
/// </para>
/// </summary>
public enum ReescalaEstoqueMaximo
{
    /// <summary>
    /// <c>necessidade = eSeg + (eMax − eSeg) × r</c>. Supõe que o eMax do ERP tem um
    /// componente fixo (o estoque de segurança gravado) e um componente proporcional à
    /// demanda, e reescala só o segundo. Default: se a relação verdadeira for
    /// <c>eMax = a + b·d</c> com piso <c>a &gt; 0</c>, reescalar o nível inteiro erraria por
    /// <c>a(r − 1)</c>, amplificando toda discordância do ML — comprando demais quando ele
    /// prevê alto e de menos quando prevê baixo. Com <c>EstoqueSeguranca</c> nulo ou zero
    /// (o caso do <c>TipoCalculo</c> 2 e de linhas sem eSeg) o resultado é idêntico ao de
    /// <see cref="Proporcional"/>.
    /// </summary>
    SegurancaFixa,

    /// <summary>
    /// <c>necessidade = eMax × r</c>. Supõe o eMax estritamente linear na demanda, com
    /// intercepto zero. Mantido como alternativa explícita para medir a sensibilidade do
    /// resultado à hipótese do componente fixo.
    /// </summary>
    Proporcional
}

public sealed record DecisionOptions
{
    /// <summary>
    /// Diferença absoluta (unidades) entre o <c>CompraSugerida</c> gravado e o valor
    /// recalculado abaixo da qual o item é dado por reconciliado. O default 0,001 é uma
    /// unidade da última casa de <c>DECIMAL(15,3)</c> da coluna de origem: cobre
    /// arredondamento de transporte, não cobre divergência de regra.
    ///
    /// <para>
    /// <b>A tolerância não escala com o arredondamento de embalagem.</b> <c>DemandaDia</c>
    /// é <c>DECIMAL(12,4)</c> na origem e as quantidades são <c>DECIMAL(15,3)</c>: com
    /// <c>FatorEmbalagem &gt; 1</c>, uma diferença menor que a última casa que caia
    /// exatamente sobre a fronteira de um pacote vira um pacote inteiro ao passar pelo
    /// <c>Math.Ceiling</c>, e a divergência registrada é de um <c>FatorEmbalagem</c>, não
    /// de 0,001. Isso é comportamento esperado da própria regra de múltiplo de compra e
    /// não indica erro de fórmula — aumentar a tolerância para "corrigi-lo" mascararia
    /// divergência de regra de verdade.
    /// </para>
    /// </summary>
    public decimal ToleranciaReconciliacao { get; init; } = 0.001m;

    /// <summary>
    /// <b>Contrato de capacidade do braço ML:</b> quantos dias à frente do corte
    /// (<c>SugestoesCompra.DataHora</c>) o pipeline que produziu as previsões consegue
    /// prever. Um item cujo <c>DiasEstoque</c> excede este horizonte não tem braço ML —
    /// ele sai da comparação listado em
    /// <c>DecisionComparisonResult.ForaDoHorizonteMl</c>, com o motivo, em vez de virar
    /// violação de vazamento de informação.
    ///
    /// <para>
    /// O default 7 é a capacidade real do pipeline de F5/F6 hoje: as features de histórico
    /// são construídas com lead time de 7 dias, então o dia mais distante cujas
    /// observações são todas anteriores ao corte é <c>corte + 6</c>. Coberturas de 15 e 30
    /// dias, correntes no PBS, ficam fora — a lacuna é de capacidade de previsão
    /// multi-horizonte, não desta camada.
    /// </para>
    ///
    /// <para>
    /// Declarar um horizonte maior exige features geradas com
    /// <see cref="LeadTimeDias"/> pelo menos igual a ele, senão a regra de informação
    /// rejeitaria — corretamente — os dias mais distantes. Por isso o construtor de
    /// <c>DecisionComparer</c> recusa <c>HorizonteMaximoMl &gt; LeadTimeDias</c>: é o
    /// ponto único que uma tarefa futura precisa satisfazer.
    /// </para>
    /// </summary>
    public int HorizonteMaximoMl { get; init; } = 7;

    /// <summary>
    /// Qual das duas hipóteses sobre o <c>EstoqueMaximo</c> do ERP o braço ML usa em
    /// <c>TipoCalculo</c> 1. Ver <see cref="ReescalaEstoqueMaximo"/> — nenhuma das duas
    /// está validada contra o PBS.
    /// </summary>
    public ReescalaEstoqueMaximo ReescalaTipo1 { get; init; } = ReescalaEstoqueMaximo.SegurancaFixa;

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
    ///
    /// <para>
    /// Mesmo acoplamento invisível de <c>ComparisonOptions.LeadTimeDias</c>: o default
    /// (7) coincide com <c>FeatureConfig.LeadTimeDias</c> só porque nenhum dos dois foi
    /// mudado, não porque um derive do outro. Ver a nota lá — o efeito de divergir é
    /// recusa ruidosa da população, não corrupção silenciosa.
    /// </para>
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
    /// Quando falso o ERP ignora o que já está a caminho ao <b>calcular a compra</b>, e o
    /// braço ML tem de ignorar também.
    ///
    /// <para>
    /// <b>A flag governa só a aritmética da compra.</b> A pontuação do desfecho contra a
    /// venda real sempre conta os pendentes: mercadoria em trânsito chega e atende a
    /// janela, quer o ERP a tenha considerado ao decidir, quer não. Ignorá-la também na
    /// pontuação não favoreceria um braço sobre o outro, mas subestimaria a compra em
    /// excesso dos dois — com saldo 0, 100 pendentes e venda 100, os dois braços comprariam
    /// ~100 e o excesso apareceria como ~0 em vez de 100.
    /// </para>
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
    /// <c>EstoqueSeguranca</c> calculado pelo ERP. Em <c>TipoCalculo</c> 1 é a parcela do
    /// <see cref="EstoqueMaximo"/> que o modo
    /// <see cref="ReescalaEstoqueMaximo.SegurancaFixa"/> trata como componente fixo, e que
    /// portanto <b>não</b> é reescalada pela demanda do ML. Nulo ou zero (o caso do
    /// <c>TipoCalculo</c> 2) faz o braço ML recair na reescala proporcional do nível
    /// inteiro, sem mudar o comportamento do tipo 2.
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

/// <summary>
/// Reconciliação de um item: o que o ERP gravou contra o que a nossa fórmula produz.
/// Carrega os atributos que permitem <b>caracterizar</b> quem reconcilia e quem não —
/// contar não basta.
/// </summary>
/// <param name="Curva">
/// Curva de giro do ERP. Item que reconcilia é item em que nenhuma regra extra do ERP
/// entrou (<c>Efetividade</c>, piso de <c>EstoqueMinimo</c>, lote mínimo), e essas regras
/// não se distribuem uniformemente pelo catálogo. Sem este eixo, uma comparação rodando
/// só sobre a metade fácil do catálogo seria indistinguível de uma rodando sobre ele
/// inteiro.
/// </param>
/// <param name="DiferencaAssinada">
/// <c>recalculado − gravado</c>. Positivo = a nossa fórmula compra mais que o ERP.
/// O sinal é o que torna a falha diagnosticável: como <c>ceil(x) − round(x) ∈ {0, 1}</c>
/// pacote, se o PBS arredondar para o múltiplo mais próximo em vez de para cima a
/// divergência nunca cai entre zero e um <paramref name="FatorEmbalagem"/> — ela é
/// <b>exatamente</b> zero ou <b>exatamente</b> um pacote, sempre do mesmo lado (positiva),
/// nunca uma fração intermediária. "Zero ou um pacote inteiro, nada entre os dois" é uma
/// impressão digital bem mais forte para diagnosticar modo de arredondamento errado do que
/// "no máximo um pacote": aponta direto para um conserto de uma linha ("erramos o modo de
/// arredondamento"), não para "a fórmula está errada".
/// </param>
/// <param name="FatorEmbalagem">
/// O múltiplo de compra da linha, repetido aqui para que a comparação acima possa ser
/// feita sem voltar à população.
/// </param>
public sealed record ItemReconciliado(
    long SugestaoId,
    int LojaId,
    string Sku,
    string Curva,
    StatusReconciliacao Status,
    decimal CompraSugeridaErp,
    decimal CompraRecalculada,
    decimal Divergencia,
    decimal DiferencaAssinada,
    decimal? FatorEmbalagem);

/// <summary>
/// Recorte do portão de validade numa curva de giro. Mesmas contagens do resumo global —
/// inclusive <c>DivergenciaAbsMedia</c> e <c>DivergenciaAbsMaxima</c>, computadas só sobre
/// os itens divergentes do grupo (ver <see cref="ReconciliacaoResumo.DivergenciaAbsMaxima"/>).
/// </summary>
/// <summary>
/// Piso de concordância abaixo do qual nenhum número da camada de decisão é apresentável.
///
/// <para>
/// Mora no domínio, e não na tela, porque é uma afirmação sobre o RESULTADO: abaixo dele a
/// diferença entre os dois braços mede o nosso desconhecimento da regra do ERP, e não a
/// qualidade da previsão — o que vale para quem quer que leia o número.
/// </para>
/// </summary>
public static class Reconciliacao
{
    public const double PatamarAceitavel = 0.95;
}

public sealed record ReconciliacaoPorCurva(
    string Curva,
    int Itens,
    int Reconciliados,
    int Divergentes,
    int BracoMlIndeterminado,
    decimal DivergenciaAbsMedia,
    decimal DivergenciaAbsMaxima)
{
    public double TaxaConcordancia => Itens == 0 ? 0 : (double)Reconciliados / Itens;
}

/// <summary>
/// Agregado do portão de validade, sobre <b>toda</b> a população — inclusive itens que
/// depois caem por ruptura ou por horizonte, porque a pergunta "modelamos certo a
/// aritmética do ERP?" não depende da janela de venda.
/// </summary>
/// <param name="DivergenciaAbsMedia">
/// Média de <c>|recalculado − gravado|</c> sobre os itens <b>divergentes</b>, não sobre
/// todos os tentados: com concordância alta, uma média sobre todos seria empurrada para
/// zero pelos itens que bateram e esconderia o tamanho das falhas. Assim ela responde
/// "quando erra, erra quanto?", e distingue poucas falhas grandes de um centavo espalhado.
/// Sem nenhum divergente, vale zero.
/// </param>
/// <param name="DivergenciaAbsMaxima">
/// Máximo de <c>|recalculado − gravado|</c>, com o mesmo recorte de
/// <paramref name="DivergenciaAbsMedia"/>: só os itens <b>divergentes</b>. Uma divergência
/// dentro da tolerância não aparece aqui — quem quiser enxergar o quanto o portão tolerou
/// mesmo entre reconciliados precisa olhar
/// <see cref="DecisionComparisonResult.DetalheReconciliacao"/> item a item.
/// </param>
/// <param name="ItensComparados">
/// Quantos itens desta população chegaram a ser efetivamente comparados (reconciliados,
/// dentro do horizonte do ML e sem cair pela política de ruptura). Existe para que
/// <see cref="TaxaConcordancia"/> saiba quando suprimir o número: reconciliar 100% da
/// população e não comparar nenhum item é exatamente o cenário que motivou este campo —
/// sem ele a taxa sairia 1,0 e um leitor apressado leria "sucesso" onde não houve
/// comparação nenhuma.
/// </param>
/// <param name="PorCurva">
/// O mesmo recorte por curva de giro. Uma taxa global alta com uma curva inteira
/// divergindo é o sintoma de sobrevivência seletiva que a média global esconde
/// (CLAUDE.md §6). Itens sem curva preenchida não aparecem aqui.
/// </param>
public sealed record ReconciliacaoResumo(
    int Itens,
    int Reconciliados,
    int Divergentes,
    int BracoMlIndeterminado,
    int ItensComparados,
    decimal DivergenciaAbsMedia,
    decimal DivergenciaAbsMaxima,
    IReadOnlyDictionary<string, ReconciliacaoPorCurva> PorCurva)
{
    /// <summary>
    /// Fração da população em que reproduzimos o ERP — mas só quando isso responde a algo.
    /// Nula com população vazia (não há taxa a reportar) e nula também quando a população
    /// reconciliou por inteiro e, mesmo assim, nenhum item chegou a ser comparado: é o caso
    /// de horizonte 7 contra cobertura de 30 dias do PBS — a fórmula bate 100% das vezes,
    /// mas mostrar 1,0 leria como "comparação bem-sucedida" o que na verdade não comparou
    /// nada. Divergência parcial não é suprimida: uma taxa baixa já é, por si só, o alarme
    /// deste portão — abaixo de um patamar alto, nenhum número desta camada é apresentável.
    /// </summary>
    public double? TaxaConcordancia =>
        Itens == 0 || (ItensComparados == 0 && Reconciliados == Itens)
            ? null
            : (double)Reconciliados / Itens;
}

/// <summary>
/// Item que o braço ML não consegue disputar porque a compra cobre mais dias do que o
/// pipeline de previsão alcança. Não é violação de regra de informação nem falha de
/// reconciliação — é lacuna de capacidade do lado do ML, e por isso o item sai da
/// comparação identificado e sem derrubar a execução.
/// </summary>
public sealed record ItemForaDoHorizonte(
    long SugestaoId,
    int LojaId,
    string Sku,
    short DiasEstoque,
    int HorizonteMaximoMl)
{
    /// <summary>
    /// Por que estes itens não têm braço ML. É o mesmo texto para a lista inteira — o que
    /// varia de item para item já está nos campos do próprio registro —, e por isso vive em
    /// <see cref="DecisionComparisonResult.MotivoForaDoHorizonteMl"/>, uma vez por resultado.
    ///
    /// <para>
    /// Era uma propriedade calculada de instância, e o serializador gravava a frase inteira
    /// em cada linha: com o estado documentado de hoje (100% da população reconciliada cai
    /// por horizonte) e uma sugestão real do PBS de dezenas de milhares de itens, isso são
    /// megabytes de uma mesma frase gravados em <c>engine.ComparacoesPbs</c>, trafegados no
    /// GET e desserializados dentro do render do Blazor.
    /// </para>
    /// </summary>
    public static string Motivo(int horizonteMaximoMl) =>
        "A compra destes itens cobre mais dias do que o braço ML alcança: o pipeline atual só " +
        $"prevê {horizonteMaximoMl} dia(s) à frente do corte (DecisionOptions.HorizonteMaximoMl), " +
        "e decidir uma compra de N dias exige previsão de N dias à frente. A cobertura de cada " +
        "item está em DiasEstoque. Enquanto a previsão multi-horizonte não existir, estes itens " +
        "não têm braço ML — não é vazamento de informação nem divergência de fórmula.";
}

/// <summary>
/// Se o resultado produziu algo que uma UI pode chamar de "comparação" e, quando não, por
/// quê — sem que o chamador precise inspecionar contagens para descobrir.
///
/// <para>
/// Existe porque <c>ItensComparados == 0</c> sozinho não impede um resultado bem formado:
/// os dois braços saem zerados, o <see cref="WinRate"/> sai (0,0,0,0) e, se a população
/// inteira tiver reconciliado antes de cair por horizonte ou por ruptura,
/// <c>Reconciliacao.TaxaConcordancia</c> fica nula justamente por causa deste campo — mas
/// nada além dele avisa que a execução não comparou nada. É o caso real de horizonte 7
/// contra uma sugestão de 30 dias do PBS: o portão de validade fez o trabalho dele, e ainda
/// assim não há braço ML a disputar.
/// </para>
/// </summary>
public enum UtilidadeComparacao
{
    /// <summary><c>ItensComparados &gt; 0</c>: há pelo menos um par ERP/ML pontuado.</summary>
    Utilizavel,

    /// <summary><c>ItensNaPopulacao == 0</c>: nada foi passado ao comparador.</summary>
    PopulacaoVazia,

    /// <summary>
    /// Todo item reconciliado caiu em <c>ForaDoHorizonteMl</c>: a cobertura da compra
    /// excede <see cref="DecisionOptions.HorizonteMaximoMl"/> para 100% do que reconciliou.
    /// </summary>
    ForaDoHorizonteMl,

    /// <summary>
    /// Todo item reconciliado e dentro do horizonte caiu pela política de ruptura
    /// (<see cref="DecisionOptions.Ruptura"/>) — a janela inteira ficou sem venda válida.
    /// </summary>
    DescartadoPorRuptura,

    /// <summary>
    /// Nenhum item da população reconciliou: a aritmética modelada não reproduziu o
    /// <c>CompraSugerida</c> gravado para nenhuma linha. O portão de validade fechou por
    /// completo, antes mesmo de horizonte ou ruptura entrarem em jogo.
    /// </summary>
    ReconciliacaoDivergente,

    /// <summary>
    /// <c>ItensComparados == 0</c> por uma combinação de motivos, nenhum sozinho
    /// respondendo por toda a população — por exemplo parte fora do horizonte e parte
    /// descartada por ruptura.
    /// </summary>
    SemItensComparaveis
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
/// A posição resultante é <c>EstoqueSaldo + PedidosPendentes + compra</c>, com os
/// pendentes contados <b>sempre</b> — inclusive quando
/// <c>ConsideraPedidosPendentes</c> é falso e eles não entraram no cálculo da compra.
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
/// Itens reconciliados e dentro do horizonte cuja janela caiu pela política de ruptura.
/// Reportar junto com <paramref name="ItensComparados"/>.
/// </param>
/// <param name="ForaDoHorizonteMl">
/// Itens reconciliados que o braço ML não alcança porque a compra cobre mais dias do que
/// o horizonte declarado em <c>DecisionOptions.HorizonteMaximoMl</c>. Os portões são
/// aplicados em ordem — reconciliação, horizonte, ruptura — e cada item aparece só no
/// primeiro que o barrou.
/// </param>
/// <param name="ItensSemPrecoCompra">
/// Itens comparados sem <c>PrecoCompra</c>. Eles contribuem em unidades e zero em R$, o
/// que puxa os agregados monetários para baixo — sem esta contagem a queda ficaria
/// invisível.
/// </param>
/// <param name="Utilidade">
/// Se este resultado é utilizável como comparação e, quando não, por quê — ver
/// <see cref="UtilidadeComparacao"/>. É o campo que uma UI deve checar primeiro; os demais
/// só respondem "utilizável" ou "por quê" depois de contar população, horizonte e ruptura.
/// </param>
/// <param name="ItensComFallbackEstoqueSeguranca">
/// Itens de <c>TipoCalculo</c> 1 comparados com <see cref="DecisionOptions.ReescalaTipo1"/>
/// igual a <see cref="ReescalaEstoqueMaximo.SegurancaFixa"/> (a hipótese escolhida) cujo
/// <c>EstoqueSeguranca</c> gravado é nulo ou não positivo: sem componente fixo declarado, a
/// fórmula degenera na alternativa <see cref="ReescalaEstoqueMaximo.Proporcional"/> — a que
/// um ajuste anterior rejeitou por amplificar a discordância do ML. Não é erro (eSeg pode
/// faltar legitimamente, e lançar exceção seria pior), mas precisa ser contável: sem esta
/// contagem, parte da população usaria silenciosamente a fórmula rejeitada.
/// </param>
/// <param name="ItensComTaxaExtrapolada">
/// Itens comparados cuja cobertura (<c>DiasEstoque</c>) excede a janela de dias que pôde
/// alimentar a taxa do ML. Não é exclusão: o braço ML existe para eles, com a taxa
/// extrapolada pela MESMA multiplicação que o ERP faz (<c>demandaDia × DiasEstoque</c> no
/// método 2). Precisa ser reportado junto do resultado — ver
/// <see cref="DecisionComparisonResult.MotivoTaxaExtrapolada"/>.
/// </param>
/// <param name="DiasDeTaxaMinimo">
/// Menor número de dias que alimentou uma taxa nesta execução. Com o fator máximo, delimita
/// o quanto a extrapolação mais agressiva esticou: 1 dia de taxa para 30 de cobertura é uma
/// afirmação bem mais fraca que 7 para 15, e a diferença tem de aparecer.
/// </param>
/// <param name="FatorMaximoDeExtrapolacao">
/// Maior razão <c>DiasEstoque / diasDaTaxa</c> observada. 1 significa nenhuma extrapolação.
/// </param>
public sealed record DecisionComparisonResult(
    int ItensNaPopulacao,
    int ItensComparados,
    int ItensDescartadosPorRuptura,
    int ItensSemPrecoCompra,
    UtilidadeComparacao Utilidade,
    int ItensComFallbackEstoqueSeguranca,
    ReconciliacaoResumo Reconciliacao,
    IReadOnlyList<ItemReconciliado> DetalheReconciliacao,
    IReadOnlyList<ItemForaDoHorizonte> ForaDoHorizonteMl,
    ArmDecisionResult Erp,
    ArmDecisionResult Ml,
    WinRate Vitoria,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, WinRate>> VitoriaPorDimensao,
    IReadOnlyList<DecisaoComparada> Detalhe,
    int ItensComTaxaExtrapolada = 0,
    int DiasDeTaxaMinimo = 0,
    decimal FatorMaximoDeExtrapolacao = 1m)
{
    /// <summary>
    /// A explicação de <see cref="ForaDoHorizonteMl"/>, uma vez para a lista inteira — ver
    /// <see cref="ItemForaDoHorizonte.Motivo"/>. Nula quando nenhum item foi recusado por
    /// horizonte: sem item recusado não há horizonte declarado a citar, e inventar um número
    /// aqui afirmaria o que este resultado não sabe.
    /// </summary>
    public string? MotivoForaDoHorizonteMl =>
        ForaDoHorizonteMl.Count == 0 ? null : ItemForaDoHorizonte.Motivo(ForaDoHorizonteMl[0].HorizonteMaximoMl);

    /// <summary>
    /// A ressalva da extrapolação, uma vez para a execução inteira. Nula quando nenhum item
    /// extrapolou.
    ///
    /// <para>
    /// <b>Por que extrapolar é legítimo aqui:</b> a fórmula do próprio ERP para o método 2 é
    /// <c>demandaDia × DiasEstoque</c> — ele também não prevê 30 dias, ele multiplica uma taxa
    /// diária pela cobertura. Exigir do braço ML uma previsão dia a dia de toda a cobertura,
    /// enquanto o baseline extrapola uma taxa, não compararia dois métodos: desqualificaria um
    /// deles por um critério que o outro não precisa cumprir. O que a extrapolação NÃO faz é
    /// contornar a regra de informação: a taxa sai só dos dias cujas features param antes do
    /// corte, e a venda real usada para pontuar continua sendo a da cobertura inteira.
    /// </para>
    /// </summary>
    public string? MotivoTaxaExtrapolada => ItensComTaxaExtrapolada == 0
        ? null
        : $"Em {ItensComTaxaExtrapolada} item(ns) a compra cobre mais dias do que a janela que " +
          $"alimentou a previsão do ML: a taxa de {DiasDeTaxaMinimo} dia(s) foi multiplicada pela " +
          $"cobertura, com fator de até {FatorMaximoDeExtrapolacao:0.#}×. É a mesma conta que o " +
          "ERP faz com a taxa dele (demanda/dia × dias de cobertura), então os dois braços são " +
          "extrapolados do mesmo jeito — mas quanto maior o fator, mais o resultado depende de a " +
          "demanda se manter estável ao longo da cobertura.";
}
