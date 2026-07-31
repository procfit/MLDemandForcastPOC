using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints de comparação. Mesmo papel que <see cref="IImportsApi"/>:
/// camada tipada por cima do HttpClient do Aspire, usada no "Act" dos testes.
/// </summary>
public interface IComparacoesApi
{
    [Post("/api/comparacoes")]
    Task<IApiResponse<SessaoView>> CreateAsync(
        [Body] CreateSessaoRequest request, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/comparacoes/{id}")]
    Task<IApiResponse<SessaoView>> GetAsync(Guid id, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/comparacoes")]
    Task<IApiResponse<List<SessaoView>>> ListAsync(
        [Query] int redeId, [Query] int take = 50, CancellationToken ct = default);

    [Multipart]
    [Post("/api/comparacoes/{id}/dados")]
    Task<IApiResponse> UploadDadosAsync(
        Guid id,
        [AliasAs("file")] StreamPart file,
        [Query] int redeId,
        CancellationToken ct = default);
}

public sealed record CreateSessaoRequest(string? Nome);

public sealed record SessaoView(
    Guid Id,
    string? Nome,
    string Status,
    DateTimeOffset CriadoEm,
    long? SugestaoId,
    string? SugestaoDescricao,
    DateTime? SugestaoDataHora,
    byte? SugestaoTipoCalculo,
    string? MotivoInviabilidade,
    string? MensagemErro);
