using System.Net;
using System.Net.Http.Json;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// O <c>redeId</c> vem sempre do <see cref="IRedeContext"/>, que o deriva do usuário
/// autenticado. Nenhuma página informa rede — é o que impede um usuário de alcançar
/// o dado de outro cliente trocando um valor na tela.
/// </summary>
public class ImportsApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    public async Task<UploadResult> UploadAsync(Stream content, string fileName, long length, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new("application/zip");
        form.Add(fileContent, "file", fileName);

        var resp = await httpClient.PostAsync($"/api/imports/upload?redeId={redeId}", form, ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            var body = await resp.Content.ReadFromJsonAsync<UploadResponse>(cancellationToken: ct);
            return new UploadResult(true, body, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new UploadResult(false, null, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        return new UploadResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {text}"]);
    }

    public async Task<IReadOnlyList<CargaStageView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var result = await httpClient.GetFromJsonAsync<List<CargaStageView>>(
            $"/api/imports?take={take}&redeId={redeId}", ct);
        return result ?? [];
    }

    public async Task<CargaStageView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync($"/api/imports/{id}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<CargaStageView>(cancellationToken: ct);
    }

    public async Task<UploadResult> GenerateSyntheticAsync(SyntheticRequest req, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var resp = await httpClient.PostAsJsonAsync($"/api/imports/synthetic?redeId={redeId}", req, ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            var body = await resp.Content.ReadFromJsonAsync<UploadResponse>(cancellationToken: ct);
            return new UploadResult(true, body, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new UploadResult(false, null, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        return new UploadResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {text}"]);
    }
}

public sealed record SyntheticRequest(
    int NumLojas,
    int NumSkus,
    DateOnly DataInicio,
    DateOnly DataFim,
    int Seed);

public sealed record UploadResult(bool Success, UploadResponse? Body, IReadOnlyList<string>? Errors);

public sealed record UploadResponse(Guid Id, string Status, DateTimeOffset DataAgendamento);

public sealed record CargaStageView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    string NomeArquivoOriginal,
    string BlobKey,
    string? MensagemErro,
    long? LinhasImportadas);

internal sealed record ValidationErrorResponse(IReadOnlyList<string> Errors);
