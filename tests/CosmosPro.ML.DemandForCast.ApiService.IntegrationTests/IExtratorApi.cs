using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Sem <c>redeId</c> em nenhuma chamada — diferente de <see cref="IImportsApi"/>, o
/// extrator não é dado de inquilino. <c>DownloadAsync</c> devolve a resposta HTTP crua
/// (Refit não desserializa nem lança em status não-2xx quando o retorno é
/// <see cref="HttpResponseMessage"/>) para o teste poder inspecionar status e corpo de um
/// 404 sem exceção atrapalhando a asserção.
/// </summary>
public interface IExtratorApi
{
    [Get("/api/extrator/versao")]
    Task<IApiResponse<ExtratorVersaoResponse>> GetVersaoAsync(CancellationToken ct = default);

    [Get("/api/extrator/download")]
    Task<HttpResponseMessage> DownloadAsync(CancellationToken ct = default);
}

public sealed record ExtratorVersaoResponse(string Versao, string Sha256, DateTimeOffset PublicadoEm);
