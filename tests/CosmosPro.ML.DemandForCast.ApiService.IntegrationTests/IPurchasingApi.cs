using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints de simulação de compra. Espelha os DTOs internos do
/// ApiService. Não há <c>redeId</c> no enfileiramento de propósito: a rede é herdada do
/// <c>TreinoJob</c>, para que a simulação rode sobre os dados que treinaram o modelo.
/// </summary>
public interface IPurchasingApi
{
    [Post("/api/purchasing/simulate")]
    Task<IApiResponse<SimulacaoJobView>> SimulateAsync(
        [Body] EnqueueSimulationRequest req,
        CancellationToken ct = default);

    [Get("/api/purchasing/{id}")]
    Task<IApiResponse<SimulacaoJobView>> GetAsync(Guid id, CancellationToken ct = default);
}

public sealed record EnqueueSimulationRequest(
    Guid TreinoJobId,
    int? JanelaDias,
    int? LeadTimeDias,
    int? CicloDias,
    double? FatorServico);

public sealed record SimulacaoJobView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    Guid TreinoJobId,
    int JanelaDias,
    int LeadTimeDias,
    int CicloDias,
    double FatorServico,
    long? SeriesSimuladas,
    string? MensagemErro,
    string? ResultadoJson);
