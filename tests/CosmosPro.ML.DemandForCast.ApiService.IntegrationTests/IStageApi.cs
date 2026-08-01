using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit do browse do Stage. Usado nos testes só pelo <c>Total</c> —
/// é a forma mais barata de contar linhas de uma tabela sem abrir conexão SQL
/// própria no teste.
/// </summary>
public interface IStageApi
{
    [Get("/api/stage/{table}")]
    Task<IApiResponse<StagePageView>> BrowseAsync(
        string table,
        [Query] int redeId,
        [Query] int take = 1,
        CancellationToken ct = default);
}

public sealed record StagePageView(
    long Total,
    List<string> Columns,
    List<Dictionary<string, object?>> Rows);
