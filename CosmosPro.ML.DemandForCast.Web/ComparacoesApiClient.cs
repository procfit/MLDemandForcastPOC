using System.Net;
using System.Net.Http.Json;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// O <c>redeId</c> vem sempre do <see cref="IRedeContext"/>, como nos demais clients
/// (ver <see cref="ImportsApiClient"/>) — nunca de parâmetro de página.
/// </summary>
public class ComparacoesApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    public async Task<SessaoView> CreateAsync(string? nome, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var resp = await httpClient.PostAsJsonAsync(
            $"/api/comparacoes?redeId={redeId}", new CreateSessaoRequest(nome), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessaoView>(cancellationToken: ct))!;
    }

    public async Task<IReadOnlyList<SessaoView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var result = await httpClient.GetFromJsonAsync<List<SessaoView>>(
            $"/api/comparacoes?take={take}&redeId={redeId}", ct);
        return result ?? [];
    }

    public async Task<SessaoView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var resp = await httpClient.GetAsync($"/api/comparacoes/{id}?redeId={redeId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessaoView>(cancellationToken: ct);
    }

    public async Task<UploadDadosResult> UploadDadosAsync(
        Guid id, Stream content, string fileName, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new("application/zip");
        form.Add(fileContent, "file", fileName);

        var resp = await httpClient.PostAsync($"/api/comparacoes/{id}/dados?redeId={redeId}", form, ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            return new UploadDadosResult(true, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new UploadDadosResult(false, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        return new UploadDadosResult(false, [$"Erro HTTP {(int)resp.StatusCode}: {text}"]);
    }
}

public sealed record CreateSessaoRequest(string? Nome);

public sealed record SessaoView(
    Guid Id,
    string? Nome,
    string Status,
    DateTimeOffset CriadoEm,
    long? SugestaoId,
    string? SugestaoDescricao,
    DateTime? SugestaoDataHora,
    byte? SugestaoTipoCalculo,
    string? MotivoInviabilidade,
    string? MensagemErro);

public sealed record UploadDadosResult(bool Success, IReadOnlyList<string>? Errors);
