using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints de dados de mercado (IQVIA). Mesmo papel dos demais
/// <c>I*Api</c>: camada tipada por cima do HttpClient do Aspire, usada no "Act" dos testes.
/// </summary>
public interface IMercadoApi
{
    [Multipart]
    [Post("/api/mercado/uploads")]
    Task<IApiResponse<MercadoUploadResposta>> UploadAsync(
        [AliasAs("file")] StreamPart file, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/mercado/uploads/{id}")]
    Task<IApiResponse<MercadoCargaResposta>> GetCargaAsync(
        Guid id, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/mercado/cobertura")]
    Task<IApiResponse<List<MercadoCoberturaResposta>>> CoberturaAsync(
        [Query] int redeId, CancellationToken ct = default);

    [Delete("/api/mercado/cobertura")]
    Task<IApiResponse> ExcluirCoberturaAsync(
        [Query] int redeId,
        [Query(Format = "yyyy-MM-dd")] DateOnly mes,
        [Query] string brick,
        CancellationToken ct = default);
}

public sealed record MercadoUploadResposta(Guid Id, string Status, DateTimeOffset DataAgendamento);

public sealed record MercadoCargaResposta(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    string NomeArquivoOriginal,
    string? MensagemErro,
    long? LinhasImportadas,
    string? ResumoJson);

public sealed record MercadoCoberturaResposta(DateOnly Mes, string Brick, int Observacoes, decimal Unidades);
