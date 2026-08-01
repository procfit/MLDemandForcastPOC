using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit do cadastro de redes (inquilinos). Em F10 o endpoint é aberto;
/// F11 o restringe ao papel PowerUser.
/// </summary>
public interface IRedesApi
{
    [Post("/api/redes")]
    Task<IApiResponse<RedeView>> CreateAsync([Body] CreateRedeRequest request, CancellationToken ct = default);

    [Get("/api/redes")]
    Task<IApiResponse<List<RedeView>>> ListAsync(CancellationToken ct = default);
}

public sealed record CreateRedeRequest(string Nome, string Slug, string? CnpjRaiz = null);

public sealed record RedeView(int Id, string Nome, string Slug, bool Ativo);
