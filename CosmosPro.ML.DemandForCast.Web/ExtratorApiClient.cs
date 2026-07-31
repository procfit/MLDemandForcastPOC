using System.Net;
using System.Net.Http.Json;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// Sem <see cref="Services.IRedeContext"/> de propósito, diferente de
/// <see cref="ImportsApiClient"/>: o executável do extrator é o mesmo para toda rede,
/// não dado de inquilino, então nenhuma chamada aqui carrega <c>redeId</c>.
/// </summary>
public class ExtratorApiClient(HttpClient httpClient)
{
    public async Task<ExtratorVersaoView?> GetVersaoAsync(CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync("/api/extrator/versao", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ExtratorVersaoView>(cancellationToken: ct);
    }

    /// <summary>
    /// Devolve a resposta HTTP crua, com <see cref="HttpCompletionOption.ResponseHeadersRead"/>:
    /// o corpo (dezenas de MB) só é lido por quem chama, via stream — nunca materializado
    /// aqui. Quem chama é dono do <see cref="HttpResponseMessage"/> e precisa descartá-lo.
    /// </summary>
    public Task<HttpResponseMessage> AbrirDownloadAsync(CancellationToken ct = default) =>
        httpClient.GetAsync("/api/extrator/download", HttpCompletionOption.ResponseHeadersRead, ct);
}

public sealed record ExtratorVersaoView(string Versao, string Sha256, DateTimeOffset PublicadoEm);
