using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints do questionário. Mesmo papel de <see cref="IComparacoesApi"/>.
///
/// <para>
/// <b>Nenhuma rota leva id de sessão.</b> O questionário passou a ser do comprador em
/// 19/09/2026: um por pessoa, sobre a experiência acumulada em pelo menos duas execuções
/// avaliadas.
/// </para>
/// </summary>
public interface IQuestionariosApi
{
    [Get("/api/questionarios/catalogo")]
    Task<IApiResponse<CatalogoResposta>> CatalogoAsync(CancellationToken ct = default);

    [Get("/api/questionario")]
    Task<IApiResponse<QuestionarioResposta>> GetAsync(
        [Query] int redeId, [Query] Guid usuarioId, CancellationToken ct = default);

    [Put("/api/questionario")]
    Task<IApiResponse<QuestionarioResposta>> SalvarAsync(
        [Body] SalvarQuestionarioBody body,
        [Query] int redeId,
        [Query] Guid usuarioId,
        CancellationToken ct = default);

    [Post("/api/questionario/enviar")]
    Task<IApiResponse<QuestionarioResposta>> EnviarAsync(
        [Body] SalvarQuestionarioBody body,
        [Query] int redeId,
        [Query] Guid usuarioId,
        CancellationToken ct = default);

    [Get("/api/comparacoes/avaliacoes")]
    Task<IApiResponse<TabulacaoResposta>> TabulacaoAsync(
        [Query] int redeId, CancellationToken ct = default);
}

public sealed record SalvarQuestionarioBody(int PassoAtual, List<RespostaBody> Respostas);

public sealed record RespostaBody(string PerguntaCodigo, string OpcaoCodigo, string? TextoLivre);

public sealed record CatalogoResposta(int Versao, List<SecaoResposta> Secoes);

public sealed record SecaoResposta(string Titulo, string? Descricao, List<PerguntaResposta> Perguntas);

public sealed record PerguntaResposta(
    string Codigo, string Texto, bool Obrigatoria, List<OpcaoResposta> Opcoes);

public sealed record OpcaoResposta(string Codigo, string Texto, int? Valor, bool PermiteTextoLivre);

/// <param name="EnviadoEm">
/// Carimbo do envio, e a única autoridade sobre "selado" — não há coluna de situação.
/// </param>
/// <param name="ExecucoesAvaliadas">Execuções com seção G respondida por este comprador.</param>
public sealed record QuestionarioResposta(
    Guid? Id,
    DateTimeOffset? EnviadoEm,
    int PassoAtual,
    int VersaoCatalogo,
    int ExecucoesAvaliadas,
    int MinimoExigido,
    bool Liberado,
    List<RespostaItemResposta> Respostas);

public sealed record RespostaItemResposta(
    string PerguntaCodigo, string OpcaoCodigo, int? OpcaoValor, string? TextoLivre);

public sealed record TabulacaoResposta(
    int RedeId,
    List<string> Codigos,
    List<string> CodigosDeTexto,
    int Participantes,
    List<ExecucaoAvaliadaResposta> Execucoes,
    List<QuestionarioDoCompradorResposta> Questionarios);

public sealed record ExecucaoAvaliadaResposta(
    Guid SessaoId,
    DateTimeOffset CriadoEm,
    string Status,
    long? SugestaoId,
    string? SugestaoDescricao,
    string? AvaliacaoVeredito,
    string? AvaliacaoComentario,
    DateTimeOffset? AvaliacaoEm,
    string? Avaliador);

public sealed record QuestionarioDoCompradorResposta(
    string? Respondente,
    DateTimeOffset? EnviadoEm,
    int VersaoCatalogo,
    List<RespostaTabuladaResposta> Respostas);

public sealed record RespostaTabuladaResposta(
    string PerguntaCodigo,
    string PerguntaTexto,
    string OpcaoTexto,
    int? OpcaoValor,
    string? TextoLivre);
