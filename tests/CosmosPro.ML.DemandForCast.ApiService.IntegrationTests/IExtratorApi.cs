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

    /// <summary>
    /// O nome do campo é o do parâmetro do handler na apiservice, que é como o binding de
    /// <c>IFormFile</c> o encontra — trocá-lo aqui faz o endpoint receber nulo e recusar por
    /// "nenhum pacote enviado", não por erro de rota.
    /// </summary>
    [Multipart]
    [Post("/api/extrator")]
    Task<IApiResponse<ExtratorVersaoResponse>> PublicarAsync(
        [AliasAs("pacote")] StreamPart pacote,
        CancellationToken ct = default);
}

public sealed record ExtratorVersaoResponse(string Versao, string Sha256, DateTimeOffset PublicadoEm);
