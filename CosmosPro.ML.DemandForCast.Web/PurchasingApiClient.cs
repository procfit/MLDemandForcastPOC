using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>Cliente HTTP da API de simulação de compras (F8).</summary>
/// <summary>redeId sempre do <see cref="IRedeContext"/> — ver ImportsApiClient.</summary>
public class PurchasingApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<SimulacaoView?> EnqueueAsync(EnqueueSimulationRequest req, CancellationToken ct = default)
    {
        var resp = await httpClient.PostAsJsonAsync("/api/purchasing/simulate", req, ct);
        if (resp.StatusCode is HttpStatusCode.Accepted)
            return await resp.Content.ReadFromJsonAsync<SimulacaoView>(JsonOpts, ct);
        return null;
    }

    public async Task<IReadOnlyList<SimulacaoView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var r = await httpClient.GetFromJsonAsync<List<SimulacaoView>>($"/api/purchasing?take={take}&redeId={redeId}", JsonOpts, ct);
        return r ?? [];
    }

    public async Task<SimulacaoView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync($"/api/purchasing/{id}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SimulacaoView>(JsonOpts, ct);
    }

    /// <summary>Desserializa o ResultadoJson de uma simulação concluída (null se ausente/ inválido).</summary>
    public static SimulationOutput? ParseResultado(string? resultadoJson)
    {
        if (string.IsNullOrWhiteSpace(resultadoJson)) return null;
        try { return JsonSerializer.Deserialize<SimulationOutput>(resultadoJson, JsonOpts); }
        catch { return null; }
    }
}

public sealed record EnqueueSimulationRequest(
    Guid TreinoJobId,
    int? JanelaDias,
    int? LeadTimeDias,
    int? CicloDias,
    double? FatorServico);

public sealed record SimulacaoView(
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

// ---- Espelha o que o Worker grava no ResultadoJson. -----------------------

public sealed record SimulationOutput(
    DateTimeOffset GeradoEm,
    Guid TreinoJobId,
    IReadOnlyDictionary<string, string>? Produtos,
    SimulationResultView Resultado);

public sealed record SimulationResultView(
    DateOnly DataInicio,
    DateOnly DataFim,
    int LeadTimeDias,
    int CicloDias,
    double FatorServico,
    int SeriesAvaliadas,
    IReadOnlyList<PolicySimulationResultView> Politicas);

public sealed record PolicySimulationResultView(
    string Policy,
    PolicyKpisView Global,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, PolicyKpisView>>? PorDimensao,
    IReadOnlyList<BuyListItemView>? ListaCompraFinal,
    IReadOnlyList<OrderRecordView>? Pedidos);

public sealed record BuyListItemView(
    string Sku,
    int LojaId,
    decimal Estoque,
    decimal EmTransito,
    decimal Posicao,
    decimal ReorderPoint,
    decimal OrderUpToLevel,
    decimal QuantidadeSugerida);

public sealed record OrderRecordView(
    DateOnly Data,
    string Sku,
    int LojaId,
    decimal Quantidade,
    decimal PosicaoAntes,
    decimal ReorderPoint,
    decimal OrderUpToLevel);

public sealed record PolicyKpisView(
    decimal DemandaTotal,
    decimal VendaRealizada,
    decimal VendaPerdida,
    double NivelServicoUnidades,
    int DiasComRuptura,
    int DiasTotais,
    double NivelServicoDias,
    decimal EstoqueMedio,
    double CoberturaMediaDias,
    double Giro,
    int Pedidos,
    decimal UnidadesPedidas,
    decimal CustoCarregamento,
    decimal CustoRuptura,
    decimal CustoTotal);
