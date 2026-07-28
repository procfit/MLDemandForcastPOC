using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints de import. Usado pelos testes de integração
/// no "Act" — uma camada tipada por cima do HttpClient (que vem do Aspire).
/// </summary>
public interface IImportsApi
{
    [Multipart]
    [Post("/api/imports/upload")]
    Task<IApiResponse<UploadResponse>> UploadAsync(
        [AliasAs("file")] StreamPart file,
        [Query] int redeId,
        CancellationToken ct = default);

    [Get("/api/imports/{id}")]
    Task<IApiResponse<CargaStageView>> GetAsync(Guid id, CancellationToken ct = default);

    [Get("/api/imports")]
    Task<IApiResponse<List<CargaStageView>>> ListAsync([Query] int take = 50, CancellationToken ct = default);
}

public sealed record UploadResponse(Guid Id, string Status, DateTimeOffset DataAgendamento);

public sealed record CargaStageView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    string NomeArquivoOriginal,
    string BlobKey,
    string? MensagemErro,
    long? LinhasImportadas);
