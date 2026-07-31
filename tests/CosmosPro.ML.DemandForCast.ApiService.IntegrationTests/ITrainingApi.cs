using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints de treino. Espelha os DTOs internos do ApiService
/// — se um campo divergir, o teste que faz round-trip do corte quebra, que é o
/// ponto.
/// </summary>
public interface ITrainingApi
{
    [Post("/api/training/run")]
    Task<IApiResponse<TreinoJobView>> EnqueueAsync(
        [Body] EnqueueTrainingRequest req,
        [Query] int redeId,
        CancellationToken ct = default);

    [Get("/api/training/{id}")]
    Task<IApiResponse<TreinoJobView>> GetAsync(Guid id, CancellationToken ct = default);
}

public sealed record EnqueueTrainingRequest(int? MaxSkus, DateOnly? TreinoAte);

public sealed record TreinoJobView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    int MaxSkus,
    DateOnly? TreinoAte,
    long? FeaturesGeradas,
    string? ModeloBlobKey,
    string? MensagemErro,
    string? ResultadoJson);
