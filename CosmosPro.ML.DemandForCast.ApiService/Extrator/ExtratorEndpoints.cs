using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace CosmosPro.ML.DemandForCast.ApiService.Extrator;

/// <summary>
/// O executável do extrator é o mesmo para toda rede — não é dado de inquilino, e por
/// isso estes endpoints não recebem nem filtram por <c>redeId</c> (diferente de
/// <c>ImportsEndpoints</c>). Publicação é operação manual do time, feita direto no MinIO
/// a cada release — ver README.md. Não há endpoint de upload aqui de propósito: só o
/// operador publica, o comprador só baixa.
/// </summary>
internal static class ExtratorEndpoints
{
    public const string BucketName = "extrator";
    internal const string ExecutavelKey = "extrator.exe";
    internal const string ManifestoKey = "manifesto.json";

    // Case-insensitive: manifesto.json é escrito à mão pelo operador na publicação (ver
    // README.md), e "versao"/"Versao" divergindo por acaso não pode virar um 200 silencioso
    // com campo nulo — melhor aceitar qualquer casing do que exigir uma convenção exata que
    // ninguém vai lembrar às 2h de uma madrugada de release.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static IEndpointRouteBuilder MapExtratorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/extrator").WithTags("Extrator");

        group.MapGet("/download", DownloadAsync)
             .WithName("DownloadExtrator")
             .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("/versao", GetVersaoAsync)
             .WithName("GetExtratorVersao")
             .Produces<ExtratorVersaoResponse>()
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// StatObject antes de comprometer a resposta: uma vez que <c>Results.Stream</c>
    /// escreve o primeiro byte, o status HTTP já foi enviado como 200 e não pode virar 404.
    /// Só <see cref="BucketNotFoundException"/>/<see cref="ObjectNotFoundException"/> viram
    /// 404 — qualquer outra exceção (MinIO fora do ar, timeout de rede) sobe para o
    /// <c>UseExceptionHandler</c> do Program.cs e vira 500, porque "ainda não publicado" e
    /// "MinIO inacessível agora" pedem reação diferente de quem opera isto.
    /// </summary>
    internal static async Task<IResult> DownloadAsync(IMinioClient minio, CancellationToken ct)
    {
        try
        {
            await minio.StatObjectAsync(
                new StatObjectArgs().WithBucket(BucketName).WithObject(ExecutavelKey), ct);
        }
        catch (Exception ex) when (ex is BucketNotFoundException or ObjectNotFoundException)
        {
            return NaoPublicado();
        }

        return Results.Stream(
            stream => minio.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(BucketName)
                    .WithObject(ExecutavelKey)
                    .WithCallbackStream((s, token) => s.CopyToAsync(stream, token)),
                ct),
            contentType: "application/octet-stream",
            fileDownloadName: "extrator.exe");
    }

    /// <summary>
    /// Lê o manifesto publicado ao lado do executável (ver <see cref="ManifestoExtrator"/>).
    /// O checksum vem de lá — calculado uma vez pelo operador na publicação, não recalculado
    /// a cada request: o executável tem dezenas de MB, e hashear isso por download
    /// concorrente seria custo de CPU sem ganho (o arquivo não muda entre publicações).
    /// </summary>
    internal static async Task<IResult> GetVersaoAsync(IMinioClient minio, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        try
        {
            await minio.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(BucketName)
                    .WithObject(ManifestoKey)
                    .WithCallbackStream(s => s.CopyTo(ms)),
                ct);
        }
        catch (Exception ex) when (ex is BucketNotFoundException or ObjectNotFoundException)
        {
            return NaoPublicado();
        }

        ms.Position = 0;
        // manifesto.json é um punhado de bytes — materializar aqui não fere o requisito de
        // streaming, que é sobre o executável de dezenas de MB, não sobre este metadado.
        var manifesto = JsonSerializer.Deserialize<ManifestoExtrator>(ms, JsonOptions);
        if (manifesto is null) return NaoPublicado();

        return Results.Ok(new ExtratorVersaoResponse(manifesto.Versao, manifesto.Sha256, manifesto.PublicadoEm));
    }

    private static IResult NaoPublicado() => Results.Problem(
        title: "Extrator não publicado",
        detail: "Nenhuma versão do extrator foi publicada ainda. Contate o suporte técnico da CosmosPro.",
        statusCode: StatusCodes.Status404NotFound);
}

internal sealed record ExtratorVersaoResponse(string Versao, string Sha256, DateTimeOffset PublicadoEm);

/// <summary>
/// Publicado como <c>manifesto.json</c> ao lado do <c>extrator.exe</c> no bucket
/// <c>extrator</c> — mesmo objeto que o README.md instrui o operador a gerar e subir a
/// cada release. Sidecar em vez de metadado de objeto no MinIO: evita depender de como o
/// SDK normaliza chaves de metadata (com/sem prefixo <c>x-amz-meta-</c> entre versões) e
/// fica inspecionável/editável por qualquer cliente S3, sem ferramenta especial.
/// </summary>
internal sealed record ManifestoExtrator(string Versao, string Sha256, DateTimeOffset PublicadoEm);
