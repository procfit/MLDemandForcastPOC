using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints do questionário. Mesmo papel de <see cref="IComparacoesApi"/>.
/// </summary>
public interface IQuestionariosApi
{
    [Get("/api/questionarios/catalogo")]
    Task<IApiResponse<CatalogoResposta>> CatalogoAsync(CancellationToken ct = default);

    [Get("/api/comparacoes/{sessaoId}/questionario")]
    Task<IApiResponse<QuestionarioResposta>> GetAsync(
        Guid sessaoId, [Query] int redeId, CancellationToken ct = default);

    [Put("/api/comparacoes/{sessaoId}/questionario")]
    Task<IApiResponse<QuestionarioResposta>> SalvarAsync(
        Guid sessaoId,
        [Body] SalvarQuestionarioBody body,
        [Query] int redeId,
        [Query] Guid usuarioId,
        CancellationToken ct = default);

    [Post("/api/comparacoes/{sessaoId}/questionario/enviar")]
    Task<IApiResponse<QuestionarioResposta>> EnviarAsync(
        Guid sessaoId,
        [Body] SalvarQuestionarioBody body,
        [Query] int redeId,
        [Query] Guid usuarioId,
        CancellationToken ct = default);
}

public sealed record SalvarQuestionarioBody(int PassoAtual, List<RespostaBody> Respostas);

public sealed record RespostaBody(string PerguntaCodigo, string OpcaoCodigo, string? TextoLivre);

public sealed record CatalogoResposta(int Versao, List<SecaoResposta> Secoes);

public sealed record SecaoResposta(string Titulo, string? Descricao, List<PerguntaResposta> Perguntas);

public sealed record PerguntaResposta(
    string Codigo, string Texto, bool Obrigatoria, List<OpcaoResposta> Opcoes);

public sealed record OpcaoResposta(string Codigo, string Texto, int? Valor, bool PermiteTextoLivre);

public sealed record QuestionarioResposta(
    Guid? Id,
    string SessaoStatus,
    DateTimeOffset? EnviadoEm,
    int PassoAtual,
    int VersaoCatalogo,
    List<RespostaItemResposta> Respostas);

public sealed record RespostaItemResposta(
    string PerguntaCodigo, string OpcaoCodigo, int? OpcaoValor, string? TextoLivre);
