using System.Net.Http.Json;
using System.Text.Json;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>redeId sempre do <see cref="IRedeContext"/> — ver ImportsApiClient.</summary>
public class StageApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    public async Task<IReadOnlyList<StageTableSummary>> ListTablesAsync(CancellationToken ct = default)
    {
        var result = await httpClient.GetFromJsonAsync<List<StageTableSummary>>("/api/stage/tables", ct);
        return result ?? [];
    }

    public async Task<StagePage> BrowseAsync(
        string table, int skip, int take, string? orderBy, bool desc, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var url = $"/api/stage/{Uri.EscapeDataString(table)}?redeId={redeId}&skip={skip}&take={take}&desc={desc.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(orderBy))
            url += $"&orderBy={Uri.EscapeDataString(orderBy)}";

        var page = await httpClient.GetFromJsonAsync<StagePage>(url, ct);
        return page ?? new StagePage(0, [], []);
    }
}

public sealed record StageTableSummary(string Alias, string Label, string Icon);

/// <summary>
/// Página de dados do Stage. Rows são objetos dinâmicos (chave = nome da coluna);
/// os valores chegam como <see cref="JsonElement"/> e são formatados na UI.
/// </summary>
public sealed record StagePage(
    long Total,
    IReadOnlyList<string> Columns,
    IReadOnlyList<Dictionary<string, JsonElement>> Rows);
