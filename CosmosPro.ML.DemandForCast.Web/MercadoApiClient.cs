using System.Net;
using System.Net.Http.Json;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// Dados de mercado da IQVIA (F16). O <c>redeId</c> vem sempre do
/// <see cref="IRedeContext"/>, como nos demais clients — nenhuma página informa rede.
/// </summary>
public class MercadoApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    public async Task<MercadoUploadResult> UploadAsync(Stream content, string fileName, long length, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(fileContent, "file", fileName);

        var resp = await httpClient.PostAsync($"/api/mercado/uploads?redeId={redeId}", form, ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            var body = await resp.Content.ReadFromJsonAsync<MercadoUploadResponse>(cancellationToken: ct);
            return new MercadoUploadResult(true, body, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new MercadoUploadResult(false, null, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        return new MercadoUploadResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {text}"]);
    }

    public async Task<IReadOnlyList<MercadoCargaView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var result = await httpClient.GetFromJsonAsync<List<MercadoCargaView>>(
            $"/api/mercado/uploads?take={take}&redeId={redeId}", ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<MercadoCoberturaView>> CoberturaAsync(CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var result = await httpClient.GetFromJsonAsync<List<MercadoCoberturaView>>(
            $"/api/mercado/cobertura?redeId={redeId}", ct);
        return result ?? [];
    }
}

public sealed record MercadoUploadResult(bool Success, MercadoUploadResponse? Body, IReadOnlyList<string>? Errors);

public sealed record MercadoUploadResponse(Guid Id, string Status, DateTimeOffset DataAgendamento);

public sealed record MercadoCargaView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    string NomeArquivoOriginal,
    string? MensagemErro,
    long? LinhasImportadas,
    string? ResumoJson);

public sealed record MercadoCoberturaView(
    DateOnly Mes,
    string Brick,
    int Observacoes,
    decimal Unidades);
