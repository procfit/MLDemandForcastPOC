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

    /// <summary>
    /// Publica o ZIP do artefato do CI, que traz o <c>extrator.exe</c> e o
    /// <c>manifesto.json</c> juntos. Um arquivo só, e não dois campos, porque é assim que o
    /// artefato sai do Actions — e porque um pacote lacrado não permite ao operador misturar
    /// execuções.
    /// O conteúdo chega como stream do navegador e é repassado como stream: os ~118 MB do
    /// executável não são materializados nem aqui nem no salto seguinte.
    /// </summary>
    public async Task<PublicarExtratorResult> PublicarAsync(
        Stream pacote,
        string nomePacote,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();

        var conteudo = new StreamContent(pacote);
        conteudo.Headers.ContentType = new("application/zip");
        form.Add(conteudo, "pacote", nomePacote);

        var resp = await httpClient.PostAsync("/api/extrator", form, ct);

        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<ExtratorVersaoView>(cancellationToken: ct);
            return new PublicarExtratorResult(true, body, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new PublicarExtratorResult(false, null, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var texto = await resp.Content.ReadAsStringAsync(ct);
        return new PublicarExtratorResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {texto}"]);
    }
}

public sealed record ExtratorVersaoView(string Versao, string Sha256, DateTimeOffset PublicadoEm);

public sealed record PublicarExtratorResult(
    bool Success, ExtratorVersaoView? Versao, IReadOnlyList<string>? Errors);
