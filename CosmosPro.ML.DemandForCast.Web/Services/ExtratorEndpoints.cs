using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// A apiservice não tem endpoint externo (invariante do projeto — ver Program.cs dela);
/// o navegador do comprador só alcança a Web. Este endpoint existe só para repassar,
/// byte a byte, o stream que a apiservice já faz do MinIO — nunca materializa o
/// executável (dezenas de MB) em memória em nenhum dos dois saltos.
/// </summary>
internal static class ExtratorEndpoints
{
    public static IEndpointRouteBuilder MapExtratorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/extrator/download", DownloadAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> DownloadAsync(ExtratorApiClient api, CancellationToken ct)
    {
        var upstream = await api.AbrirDownloadAsync(ct);

        if (upstream.StatusCode == HttpStatusCode.NotFound)
        {
            upstream.Dispose();
            return Results.Problem(
                title: "Extrator não publicado",
                detail: "O extrator ainda não foi publicado. Fale com o suporte técnico da CosmosPro.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (!upstream.IsSuccessStatusCode)
        {
            upstream.Dispose();
            return Results.Problem(
                title: "Falha ao baixar o extrator",
                detail: "Não foi possível baixar o extrator agora. Tente novamente em alguns minutos.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

        // O disposal fica dentro do callback: Results.Stream só invoca isto quando a
        // resposta já começou a ser escrita, então descartar upstream antes (ex.: com
        // `using` no escopo deste método) fecharia o stream de origem antes de usá-lo.
        return Results.Stream(async body =>
        {
            using (upstream)
            {
                await upstream.Content.CopyToAsync(body, ct);
            }
        }, contentType, fileDownloadName: "extrator.exe");
    }
}
