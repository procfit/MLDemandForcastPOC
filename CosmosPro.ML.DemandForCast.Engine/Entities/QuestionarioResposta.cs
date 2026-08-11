namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Uma pergunta respondida. Todas são de múltipla escolha, então a resposta é sempre uma
/// opção do catálogo — <see cref="TextoLivre"/> só acompanha as opções que o catálogo
/// marca como <c>PermiteTextoLivre</c> (o "Outro:").
///
/// <para>
/// <b>O texto da pergunta e o da opção vão denormalizados, e isso é o contrato da
/// tabela.</b> O catálogo vive em código e muda com deploy; uma análise que precisa saber
/// o que exatamente foi perguntado não pode depender de qual binário estava no ar. Sem o
/// retrato, um ajuste de redação reescreveria retroativamente perguntas já respondidas.
/// Mesmo princípio de <c>ComparacaoSessao.SugestaoDescricao</c> e da materialização de
/// <c>ComparacaoSessaoItens</c>.
/// </para>
///
/// <para>
/// <b>Sem <c>RedeId</c></b> — o escopo é transitivo pela FK para <see cref="Questionario"/>,
/// como em <c>ComparacaoSessaoItens</c>. Todo endpoint que ler esta tabela tem de juntar o
/// pai e filtrar pelo inquilino do <c>IRedeContext</c> num único round-trip, e responder
/// <b>404 e não 403</b> quando o questionário é de outra rede: um 403 confirmaria a quem
/// sondasse que ele existe.
/// </para>
/// </summary>
public sealed class QuestionarioResposta
{
    public Guid QuestionarioId { get; set; }

    /// <summary>Código estável do catálogo (ex.: <c>CONFIANCA_ML</c>). Parte da PK.</summary>
    public required string PerguntaCodigo { get; set; }

    /// <summary>Retrato do enunciado como ele foi exibido. Ver a nota da classe.</summary>
    public required string PerguntaTexto { get; set; }

    public required string OpcaoCodigo { get; set; }

    /// <summary>Retrato da opção escolhida como ela foi exibida.</summary>
    public required string OpcaoTexto { get; set; }

    /// <summary>
    /// Posição da opção na escala, quando a pergunta é ordinal (Likert). Existe para
    /// tabular sem parsear texto em português.
    ///
    /// <para>
    /// Nulo significa <b>"esta pergunta não é ordinal"</b> — nunca "grau zero". Uma
    /// pergunta nominal ("qual sua função?") não tem ordem, e atribuir 0 a ela produziria
    /// média onde não existe média.
    /// </para>
    /// </summary>
    public int? OpcaoValor { get; set; }

    /// <summary>Complemento livre. Nulo é o normal — só opções de "Outro:" o pedem.</summary>
    public string? TextoLivre { get; set; }
}
