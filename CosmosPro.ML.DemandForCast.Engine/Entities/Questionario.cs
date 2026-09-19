namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// A avaliação do comprador sobre o <b>protótipo</b>, respondida uma única vez.
///
/// <para>
/// <b>Não pertence a uma execução</b>, e essa é a mudança de 19/09/2026. Havia um
/// questionário por sessão, e era o envio dele que a concluía; o Professor orientou que o
/// comprador responda a Seção G a cada execução e o questionário uma vez só, sobre a
/// experiência acumulada em pelo menos
/// <see cref="Questionarios.QuestionarioCatalogo.MinimoDeExecucoes"/> delas (documento do
/// patrocinador de 16/09/2026). Quem conclui a sessão passou a ser a Seção G — ver
/// <see cref="ComparacaoSessao.AvaliacaoVeredito"/>.
/// </para>
///
/// <para>
/// <b>Sem coluna de situação</b>, como antes e pelo mesmo motivo: <see cref="EnviadoEm"/>
/// nulo é rascunho, preenchido é selado. Um <c>Status</c> aqui repetiria a mesma verdade em
/// dois lugares que podem divergir, e a tela leria o errado.
/// </para>
///
/// <para>
/// <b>Sobrevive à exclusão de qualquer execução.</b> A FK <c>Cascade</c> para a sessão saiu
/// junto com a coluna <c>SessaoId</c>: o veredito do comprador sobre a ferramenta não evapora
/// porque uma das execuções que ele avaliou foi apagada.
/// </para>
/// </summary>
public sealed class Questionario
{
    public Guid Id { get; set; }

    /// <summary>
    /// Rede em que o comprador respondeu. Continua sendo quem decide escopo de leitura, vindo
    /// do <c>IRedeContext</c> — mas <b>não</b> é a chave de unicidade, que é o usuário.
    /// </summary>
    public int RedeId { get; set; }

    /// <summary>
    /// Quem respondeu, e agora <b>a chave</b>: um questionário por comprador
    /// (<c>UQ_Questionarios_UsuarioId</c>). Continua sem constraint de FK — índice sim,
    /// referência não —, no mesmo padrão de <c>SimulacaoCompra.TreinoJobId</c>: resposta de
    /// pesquisa precisa sobreviver à remoção do usuário que a deu.
    /// </summary>
    public Guid UsuarioId { get; set; }

    /// <inheritdoc cref="Questionarios.QuestionarioCatalogo.Versao"/>
    public int VersaoCatalogo { get; set; }

    /// <summary>Onde o wizard parou, para retomar de onde o comprador saiu.</summary>
    public int PassoAtual { get; set; }

    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }
    public DateTimeOffset? EnviadoEm { get; set; }
}
