using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CosmosPro.ML.DemandForCast.ApiService.Imports;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace CosmosPro.ML.DemandForCast.ApiService.Extrator;

/// <summary>
/// O executável do extrator é o mesmo para toda rede — não é dado de inquilino, e por
/// isso estes endpoints não recebem nem filtram por <c>redeId</c> (diferente de
/// <c>ImportsEndpoints</c>). Publicação continua sendo operação do operador, não do
/// comprador, mas passou a ter endpoint (<see cref="PublicarAsync"/>): antes o único
/// caminho era arrastar os arquivos no console do MinIO, e no deploy o MinIO não tem
/// endpoint publicado — só a Web atravessa o Traefik. Sem isto, publicar em produção
/// exigiria shell na VPS a cada release do extrator.
/// A autorização mora na Web (<c>PowerUser</c>), como no resto do sistema: esta
/// apiservice não tem endpoint externo nem auth própria — invariante do projeto, ver o
/// Program.cs dela. Se um dia for publicada, este endpoint é o primeiro a precisar de
/// autenticação, porque é o único aqui que escreve um executável que outros baixam.
/// </summary>
internal static class ExtratorEndpoints
{
    public const string BucketName = "extrator";
    internal const string ExecutavelKey = "extrator.exe";
    internal const string ManifestoKey = "manifesto.json";

    // O self-contained single-file do extrator dá ~118 MB hoje; o teto tem folga para ele
    // crescer sem virar manutenção, mas não é o de 500 MB dos ZIPs de import — aqui o
    // conteúdo é um executável conhecido, não um pacote de dados do cliente. O teto do
    // pacote é menor que o do conteúdo porque o ZIP comprime (o artefato do CI fica em
    // torno de metade do tamanho do .exe), e os dois são conferidos: o do pacote no upload,
    // o do executável no tamanho descompactado que a entrada declara.
    private const long MaxExecutavelBytes = 300L * 1024 * 1024;
    private const long MaxPacoteBytes = 200L * 1024 * 1024;
    private const long MaxManifestoBytes = 64L * 1024;

    // Case-insensitive: manifesto.json é escrito à mão pelo operador na publicação (ver
    // README.md), e "versao"/"Versao" divergindo por acaso não pode virar um 200 silencioso
    // com campo nulo — melhor aceitar qualquer casing do que exigir uma convenção exata que
    // ninguém vai lembrar às 2h de uma madrugada de release.
    // CamelCase na escrita para o objeto gravado no bucket sair igual ao manifesto que o
    // README documenta e que o CI gera — quem abrir o arquivo pelo console do MinIO vê o
    // mesmo formato de sempre. Na leitura o casing não importa (linha acima).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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

        group.MapPost("/", PublicarAsync)
             .DisableAntiforgery()
             .WithName("PublicarExtrator")
             .Accepts<IFormFile>("multipart/form-data")
             .Produces<ExtratorVersaoResponse>()
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest);

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

    /// <summary>
    /// Recebe **um** ZIP com os dois arquivos — que é exatamente o que o artefato
    /// <c>extrator</c> do CI (`.github/workflows/ci-imagens.yml`) baixa da UI do Actions.
    /// Um pacote lacrado, em vez de dois campos de arquivo, elimina na origem o erro que
    /// mais importa aqui: misturar execuções, mandando o `.exe` de uma com o
    /// <c>manifesto.json</c> de outra.
    /// O SHA-256 continua sendo **recalculado aqui** e conferido contra o declarado — agora
    /// como rede contra pacote corrompido ou montado à mão, não contra troca de arquivo. O
    /// hash é a promessa que a tela mostra ao comprador: publicar um valor que o download
    /// não cumpre faria quem conferisse concluir "executável adulterado".
    /// </summary>
    internal static async Task<IResult> PublicarAsync(
        IFormFile? pacote,
        IMinioClient minio,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (pacote is null || pacote.Length == 0)
        {
            return Results.BadRequest(new ValidationErrorResponse(
                ["Nenhum pacote enviado. Envie o .zip do artefato 'extrator' da execução do CI."]));
        }

        if (pacote.Length > MaxPacoteBytes)
        {
            return Results.BadRequest(new ValidationErrorResponse(
                [$"O pacote excede o limite de {MaxPacoteBytes / (1024 * 1024)} MB."]));
        }

        // ZipArchive em modo leitura precisa de stream seekable; o IFormFile já vem
        // bufferizado (memória ou disco, conforme o tamanho) pelo pipeline de formulário,
        // então isto não relê da rede — mesmo caminho de ImportsEndpoints com os ZIPs de
        // import.
        await using var stream = pacote.OpenReadStream();

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        // Mesmo conjunto de exceções que ImportValidator captura, e pelo mesmo motivo: o
        // ZipArchive lança diferente conforme o stream diverge do formato — arquivos curtos
        // estouram no seek do fim-do-central-directory com ArgumentOutOfRangeException, não
        // com InvalidDataException.
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException
                                      or EndOfStreamException or IOException)
        {
            return Results.BadRequest(new ValidationErrorResponse(
                ["O arquivo enviado não é um .zip válido."]));
        }

        using (zip)
        {
            if (Entrada(zip, ManifestoKey) is not { } entradaManifesto)
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"O pacote não contém um {ManifestoKey}. Envie o .zip do artefato 'extrator' sem alterar o conteúdo."]));
            }

            if (Entrada(zip, ExecutavelKey) is not { } entradaExecutavel)
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"O pacote não contém um {ExecutavelKey}. Envie o .zip do artefato 'extrator' sem alterar o conteúdo."]));
            }

            if (entradaManifesto.Length > MaxManifestoBytes)
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"O {ManifestoKey} do pacote é grande demais para ser um manifesto."]));
            }

            // Comparado contra o tamanho **descompactado** declarado na entrada, antes de
            // extrair: é o teto que o limite do pacote não cobre, porque um .zip pequeno
            // pode declarar um conteúdo enorme.
            if (entradaExecutavel.Length > MaxExecutavelBytes)
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"O executável dentro do pacote excede o limite de {MaxExecutavelBytes / (1024 * 1024)} MB."]));
            }

            ManifestoExtrator? declarado;
            await using (var manifestoStream = entradaManifesto.Open())
            {
                try
                {
                    declarado = await JsonSerializer.DeserializeAsync<ManifestoExtrator>(manifestoStream, JsonOptions, ct);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new ValidationErrorResponse(
                        [$"O {ManifestoKey} do pacote não é um JSON válido: {ex.Message}"]));
                }
            }

            if (declarado is null || string.IsNullOrWhiteSpace(declarado.Versao))
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"O {ManifestoKey} do pacote não declara a versão."]));
            }

            if (!ShaValido(declarado.Sha256))
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"O {ManifestoKey} do pacote não declara um SHA-256 válido (64 caracteres hexadecimais)."]));
            }

            string calculado;
            await using (var hashStream = entradaExecutavel.Open())
            {
                calculado = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashStream, ct));
            }

            if (!calculado.Equals(declarado.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"O SHA-256 do executável dentro do pacote ({calculado}) não corresponde ao declarado no " +
                     $"manifesto ({declarado.Sha256.ToLowerInvariant()}). O pacote está corrompido ou foi montado à mão."]));
            }

            await Imports.ImportsEndpoints.EnsureBucketExistsAsync(minio, BucketName, ct);

            // Executável primeiro, manifesto depois, e a ordem é a que falha do lado seguro:
            // a tela habilita o botão pelo /versao, que lê o manifesto. Se o segundo PUT
            // falhar, sobra um executável que ninguém é convidado a baixar. Na ordem
            // inversa, a tela anunciaria versão e checksum de um download que responde 404.
            // O ZIP entra como um só arquivo, mas o bucket guarda dois objetos: /versao lê
            // um manifesto de ~200 bytes a cada render da tela da sessão, e não teria por
            // que abrir um ZIP de ~118 MB para isso.
            await using (var uploadStream = entradaExecutavel.Open())
            {
                await minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(BucketName)
                    .WithObject(ExecutavelKey)
                    .WithStreamData(uploadStream)
                    .WithObjectSize(entradaExecutavel.Length)
                    .WithContentType("application/octet-stream"),
                    ct);
            }

            // PublicadoEm é do servidor, não do manifesto recebido: no CI aquele campo é a
            // hora do build, e o que a tela precisa dizer ao comprador é desde quando esta
            // versão está disponível — um artefato de semanas atrás publicado hoje é
            // publicado hoje. O hash gravado é o calculado, não o declarado, para o objeto
            // no bucket nunca divergir do executável ao lado dele nem por diferença de caixa.
            var publicado = new ManifestoExtrator(declarado.Versao.Trim(), calculado, DateTimeOffset.UtcNow);
            var bytesManifesto = JsonSerializer.SerializeToUtf8Bytes(publicado, JsonOptions);

            using (var manifestoUpload = new MemoryStream(bytesManifesto))
            {
                await minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(BucketName)
                    .WithObject(ManifestoKey)
                    .WithStreamData(manifestoUpload)
                    .WithObjectSize(bytesManifesto.Length)
                    .WithContentType("application/json"),
                    ct);
            }

            logger.LogInformation("Extrator publicado: versao={Versao} sha256={Sha} bytes={Bytes}",
                publicado.Versao, publicado.Sha256, entradaExecutavel.Length);

            return Results.Ok(new ExtratorVersaoResponse(publicado.Versao, publicado.Sha256, publicado.PublicadoEm));
        }
    }

    /// <summary>
    /// Casa pelo nome do arquivo, ignorando diretório: o artefato do Actions traz os dois na
    /// raiz, mas um ZIP feito à mão a partir da pasta de publicação vem com prefixo. Nome
    /// repetido é recusado (devolve <c>null</c>) em vez de escolher um: qual dos dois `.exe`
    /// de um pacote com duas versões seria publicado não é decisão para um tie-break
    /// silencioso.
    /// </summary>
    private static ZipArchiveEntry? Entrada(ZipArchive zip, string nome)
    {
        var candidatas = zip.Entries
            .Where(e => string.Equals(Path.GetFileName(e.FullName), nome, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidatas.Count == 1 ? candidatas[0] : null;
    }

    private static bool ShaValido(string? sha) =>
        sha is { Length: 64 } && sha.All(Uri.IsHexDigit);

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
