using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// A apiservice não tem endpoint externo (invariante do projeto — ver Program.cs dela);
/// o navegador do comprador só alcança a Web. Este endpoint existe só para repassar,
/// byte a byte, o stream que a apiservice já faz do MinIO — nunca materializa o
/// executável (dezenas de MB) em memória em nenhum dos dois saltos.
///
/// O grupo <c>/extrator/publicacao</c> é o outro lado da mesma invariante: a publicação
/// do executável mora em <c>/admin/extrator</c>, atrás de cookie de <c>PowerUser</c>, e
/// CI não faz login numa tela Blazor. Sem uma rota autenticada por token, automatizar a
/// publicação exigiria expor a apiservice ou dar shell na VPS a cada release do extrator.
/// </summary>
internal static class ExtratorEndpoints
{
    /// <summary>
    /// Env var no destino: <c>Extrator__PublishToken</c>, alimentada pelo parâmetro Aspire
    /// <c>extrator-publish-token</c> (ver AppHost.cs).
    /// </summary>
    internal const string TokenConfigKey = "Extrator:PublishToken";

    private const string EsquemaBearer = "Bearer";

    public static IEndpointRouteBuilder MapExtratorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/extrator/download", DownloadAsync).RequireAuthorization();

        // Token em vez de `RequireAuthorization`, e um grupo próprio em vez de conviver com
        // o download: este é o único caminho da aplicação cujo chamador é uma máquina, não
        // uma pessoa com sessão. O cookie de Identity não serve a um `curl` do Actions, e
        // criar um usuário de serviço no Identity daria a um segredo de CI o mesmo poder de
        // um `PowerUser` — que enxerga o dado comercial de todas as redes.
        var publicacao = app.MapGroup("/extrator/publicacao")
                            .AddEndpointFilter(ConferirTokenAsync)
                            .DisableAntiforgery();

        publicacao.MapGet("/", VersaoPublicadaAsync);
        publicacao.MapPost("/", PublicarAsync);

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

    /// <summary>
    /// Responde **200 com campos nulos** quando nada foi publicado, e não 404 como o
    /// <c>/api/extrator/versao</c> de onde o dado vem. A diferença é o que deixa o CI
    /// distinguir três situações que um 404 juntaria numa só: 200 é "este build tem a rota
    /// e respondeu"; 404 é "a Web no ar é anterior a este commit, a rota não existe"; 401 é
    /// "o token do destino não é o que o CI tem". Sem essa distinção, um deploy que ainda
    /// não subiu pareceria um extrator nunca publicado, e o CI publicaria contra uma rota
    /// inexistente sem saber por quê.
    /// </summary>
    internal static async Task<IResult> VersaoPublicadaAsync(ExtratorApiClient api, CancellationToken ct)
    {
        var publicada = await api.GetVersaoAsync(ct);

        return Results.Ok(new PublicacaoExtratorView(
            publicada?.Versao, publicada?.Sha256, publicada?.PublicadoEm));
    }

    /// <summary>
    /// Recebe o ZIP no **corpo cru**, não em multipart: o pacote é um arquivo só e o único
    /// chamador é o CI. Um multipart aqui acrescentaria um parse e um buffer em disco no
    /// meio do caminho, sem nada para nomear além do próprio corpo — e a apiservice
    /// continua recebendo o multipart que o contrato dela pede, montado por
    /// <see cref="ExtratorApiClient.PublicarAsync"/> em cima deste stream.
    ///
    /// Nenhuma validação de conteúdo acontece aqui de propósito. Quem confere o ZIP, exige
    /// os dois arquivos e **recalcula** o SHA-256 contra o declarado é a apiservice, e ter
    /// uma segunda cópia dessas regras neste salto criaria dois validadores que podem
    /// discordar sobre o mesmo pacote.
    /// </summary>
    internal static async Task<IResult> PublicarAsync(
        HttpRequest request,
        ExtratorApiClient api,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(ExtratorEndpoints).FullName!);

        var resultado = await api.PublicarAsync(request.Body, "extrator.zip", ct);

        if (!resultado.Success || resultado.Versao is null)
        {
            var erros = resultado.Errors ?? ["Falha desconhecida ao publicar o extrator."];

            // Registra além de devolver, pelo mesmo motivo do `Recusar` da apiservice: quem
            // investiga uma publicação recusada não tem tela na frente — tem o log do job e
            // o log do container, e os dois precisam contar a mesma coisa.
            logger.LogWarning("Publicação automática do extrator recusada: {Erros}", string.Join(" | ", erros));
            return Results.BadRequest(new ValidationErrorResponse(erros));
        }

        logger.LogInformation("Extrator publicado pelo CI: versao={Versao} sha256={Sha}",
            resultado.Versao.Versao, resultado.Versao.Sha256);

        return Results.Ok(new PublicacaoExtratorView(
            resultado.Versao.Versao, resultado.Versao.Sha256, resultado.Versao.PublicadoEm));
    }

    private static async ValueTask<object?> ConferirTokenAsync(
        EndpointFilterInvocationContext contexto,
        EndpointFilterDelegate proximo)
    {
        var configuracao = contexto.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var esperado = configuracao[TokenConfigKey];

        // Token ausente no destino **desliga** a rota, em vez de deixá-la aceitar um pedido
        // sem token: um `esperado` vazio comparado com um recebido vazio daria igual, e a
        // publicação do executável que os clientes baixam ficaria aberta a quem achasse a
        // URL. 503 e não 401 porque a causa é configuração do servidor, não credencial de
        // quem chamou — e é isso que o job precisa ler para saber onde consertar.
        if (string.IsNullOrWhiteSpace(esperado))
        {
            return Results.Problem(
                title: "Publicação automática não configurada",
                detail: $"Nenhum token de publicação configurado neste ambiente ({TokenConfigKey}). " +
                        "A publicação pela UI em /admin/extrator continua disponível.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!TokenConfere(esperado, contexto.HttpContext.Request.Headers.Authorization))
        {
            return Results.Unauthorized();
        }

        return await proximo(contexto);
    }

    /// <summary>
    /// Comparação em tempo fixo: o token é um segredo longo e comparação curto-circuitada
    /// vaza o prefixo correto pelo tempo de resposta, que é justamente o que permite
    /// descobrir um segredo byte a byte. <c>FixedTimeEquals</c> devolve <c>false</c> para
    /// tamanhos diferentes sem comparar — o comprimento é o único bit que escapa, e ele não
    /// reduz o espaço de busca de forma útil.
    /// </summary>
    internal static bool TokenConfere(string? esperado, string? cabecalhoAutorizacao)
    {
        if (string.IsNullOrWhiteSpace(esperado) || string.IsNullOrWhiteSpace(cabecalhoAutorizacao))
        {
            return false;
        }

        var cabecalho = cabecalhoAutorizacao.Trim();
        if (!cabecalho.StartsWith(EsquemaBearer + " ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var recebido = cabecalho[(EsquemaBearer.Length + 1)..].Trim();
        if (recebido.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(esperado.Trim()),
            Encoding.UTF8.GetBytes(recebido));
    }
}

/// <summary>
/// Campos anuláveis porque "nada publicado ainda" é estado normal de ambiente novo — ver
/// <see cref="ExtratorEndpoints.VersaoPublicadaAsync"/>. Nulo aqui significa "não há versão
/// no ar", nunca "não foi possível consultar": esse caso é o 502 do salto de baixo.
/// </summary>
internal sealed record PublicacaoExtratorView(string? Versao, string? Sha256, DateTimeOffset? PublicadoEm);
