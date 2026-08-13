namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Avaliação do comprador sobre UMA comparação — a última fase da sessão, respondida
/// depois que ela já mostrou o resultado.
///
/// <para>
/// <b>Não tem coluna de situação, e isso é deliberado.</b> Quem afirma se a avaliação
/// está selada é <see cref="ComparacaoSessao.Status"/>: rascunho é a sessão em
/// <see cref="SessaoStatus.AguardandoQuestionario"/> com uma linha destas existindo, e
/// selado é a sessão em <see cref="SessaoStatus.Concluida"/>. Um <c>Status</c> aqui
/// repetiria a mesma verdade em dois lugares que podem divergir — e a tela leria o
/// errado. <see cref="EnviadoEm"/> é carimbo, não situação: existe porque a tela diz
/// "respondido em dd/MM" e a sessão só guarda o <c>AtualizadoEm</c>, que é sobrescrito
/// por qualquer avanço.
/// </para>
///
/// <para>
/// O selo é uma escrita só: gravar <see cref="EnviadoEm"/> e mover a sessão para
/// <see cref="SessaoStatus.Concluida"/> acontecem na <b>mesma transação</b>, com o
/// <c>WHERE ... AND Status = 'AguardandoQuestionario'</c> no <c>UPDATE</c> da sessão
/// servindo de guarda contra dois envios simultâneos — mesmo padrão do
/// <c>SessaoResultadoMaterializador</c>. Separar as duas escritas deixaria uma janela em
/// que a resposta está gravada e a sessão ainda pede resposta.
/// </para>
/// </summary>
public sealed class Questionario
{
    public Guid Id { get; set; }
    public int RedeId { get; set; }

    /// <summary>
    /// A comparação avaliada. Índice <b>único</b>: um questionário por sessão. Quem
    /// respondeu fica em <see cref="UsuarioId"/>, mas não faz parte da chave — duas
    /// pessoas da mesma rede não avaliam a mesma comparação em separado, porque o envio
    /// fecha a sessão para todo mundo.
    /// </summary>
    public Guid SessaoId { get; set; }

    /// <summary>
    /// Quem respondeu. <b>FK lógica</b> — índice sim, constraint não, no mesmo padrão de
    /// <c>SimulacaoCompra.TreinoJobId</c>: a resposta é dado de pesquisa e precisa
    /// sobreviver à remoção do usuário que a deu. Auditoria, nunca escopo — quem decide
    /// escopo é <see cref="RedeId"/>, vindo do <c>IRedeContext</c>.
    /// </summary>
    public Guid UsuarioId { get; set; }

    /// <summary>
    /// Versão do catálogo vigente no envio. O texto de cada pergunta e da opção escolhida
    /// já vai denormalizado em <see cref="QuestionarioResposta"/>, então isto não é o que
    /// torna a resposta legível — serve para agrupar respostas comparáveis entre si sem
    /// diferenciar N textos.
    /// </summary>
    public int VersaoCatalogo { get; set; }

    /// <summary>Onde o wizard parou, para retomar de onde o comprador saiu.</summary>
    public int PassoAtual { get; set; }

    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }
    public DateTimeOffset? EnviadoEm { get; set; }

    /// <summary>
    /// Quantos itens da comparação tinham decisão do braço de ML, e quantos itens ela tinha
    /// no total — copiados do <c>ResultadoJson</c> da sessão no momento do envio.
    ///
    /// <para>
    /// Existem porque hoje o desfecho esperado é <c>CompraSugeridaMl</c> <b>nula</b>: a
    /// cobertura do ERP é de 15 a 30 dias e o pipeline prevê 7
    /// (<c>DecisionOptions.HorizonteMaximoMl</c>). Uma resposta dada sobre uma tela em que a
    /// coluna do ML está vazia não é comparável com uma dada sobre a tela cheia, e sem estas
    /// duas colunas as duas populações ficam misturadas e <b>irrecuperáveis</b> — o Stage é
    /// apagado no import seguinte (<c>DELETE ... WHERE RedeId</c>) e a sessão pode ser
    /// excluída antes da análise. É o mesmo motivo pelo qual
    /// <c>ComparacaoSessao.SkusSemCadastro</c> mora na sessão em vez do manifesto.
    /// </para>
    ///
    /// <para>
    /// Anuláveis, e não zero: um <c>ResultadoJson</c> que não carregou os agregados não
    /// afirma "nenhum item teve decisão do ML" — afirma que ninguém contou.
    /// </para>
    /// </summary>
    public int? ItensComDecisaoMl { get; set; }

    /// <inheritdoc cref="ItensComDecisaoMl"/>
    public int? TotalDeItens { get; set; }
}
