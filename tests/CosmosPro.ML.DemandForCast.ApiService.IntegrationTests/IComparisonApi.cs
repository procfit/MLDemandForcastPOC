using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit da fila de comparação contra o ERP (<c>/api/comparison</c>).
/// Espelha os DTOs internos do ApiService — se um campo divergir, o teste que faz
/// round-trip do corte de treino quebra, que é o ponto.
/// </summary>
public interface IComparisonApi
{
    [Post("/api/comparison/run")]
    Task<IApiResponse<ComparacaoPbsView>> EnqueueAsync(
        [Body] EnqueueComparisonRequest req,
        [Query] int redeId,
        CancellationToken ct = default);

    [Get("/api/comparison/{id}")]
    Task<IApiResponse<ComparacaoPbsView>> GetAsync(
        Guid id, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/comparison")]
    Task<IApiResponse<List<ComparacaoPbsView>>> ListAsync(
        [Query] int redeId, [Query] int take = 50, CancellationToken ct = default);
}

public sealed record EnqueueComparisonRequest(
    Guid TreinoJobId,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    byte TipoCalculo);

public sealed record ComparacaoPbsView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    Guid TreinoJobId,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    byte TipoCalculo,
    string? MensagemErro,
    string? ResultadoJson);
