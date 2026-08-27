using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>redeId sempre do <see cref="IRedeContext"/> — ver ImportsApiClient.</summary>
public class TrainingApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <param name="treinoAte">
    /// Corte de informação do treino (opcional) — ver <c>TreinoJob.TreinoAte</c>.
    /// Nulo treina sobre todo o histórico, que é o comportamento histórico da tela.
    /// </param>
    public async Task<TreinoJobView?> EnqueueAsync(
        int? maxSkus, DateOnly? treinoAte = null, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var resp = await httpClient.PostAsJsonAsync(
            $"/api/training/run?redeId={redeId}", new { MaxSkus = maxSkus, TreinoAte = treinoAte }, ct);
        if (resp.StatusCode is HttpStatusCode.Accepted)
            return await resp.Content.ReadFromJsonAsync<TreinoJobView>(JsonOpts, ct);
        return null;
    }

    public async Task<IReadOnlyList<TreinoJobView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var r = await httpClient.GetFromJsonAsync<List<TreinoJobView>>($"/api/training?take={take}&redeId={redeId}", JsonOpts, ct);
        return r ?? [];
    }

    public async Task<TreinoJobView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync($"/api/training/{id}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TreinoJobView>(JsonOpts, ct);
    }

    /// <summary>Desserializa o ResultadoJson de um job concluído (null se ausente/ inválido).</summary>
    public static TrainingResult? ParseResultado(string? resultadoJson)
    {
        if (string.IsNullOrWhiteSpace(resultadoJson)) return null;
        try { return JsonSerializer.Deserialize<TrainingResult>(resultadoJson, JsonOpts); }
        catch { return null; }
    }
}

public sealed record TreinoJobView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    int? MaxSkus,
    DateOnly? TreinoAte,
    long? FeaturesGeradas,
    string? ModeloBlobKey,
    string? MensagemErro,
    string? ResultadoJson);

public sealed record TrainingResult(
    DateTimeOffset GeradoEm,
    int SkusUsados,
    long TotalObservacoes,
    long TotalFeatures,
    int Folds,
    int TestWindowDias,
    IReadOnlyList<EngineResult> Engines,
    string MelhorEngine,
    DateOnly? TreinoAte = null,
    DateOnly? UltimaDataTreinada = null);

public sealed record EngineResult(
    string Engine,
    MetricsDto Global,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricsDto>>? PorDimensao);

public sealed record MetricsDto(int N, double Mae, double Rmse, double Wape, double? Mape);
