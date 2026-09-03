using System.Net;

using CosmosPro.ML.DemandForCast.Engine.Entities;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Repasse do download do ZIP que uma sessão de comparação recebeu. A apiservice não tem
/// endpoint externo (invariante do projeto — ver o Program.cs dela) e o navegador só
/// alcança a Web, então o arquivo precisa de um salto aqui — feito byte a byte, sem
/// materializar o ZIP em memória em nenhum dos dois lados.
///
/// <para>
/// <b>Não é página Blazor de propósito.</b> Um componente interativo teria de trazer o
/// arquivo inteiro para o circuito para depois empurrá-lo ao navegador; um endpoint HTTP
/// comum deixa o download ser um download, com barra de progresso do próprio browser e
/// sem consumir o circuito.
/// </para>
///
/// <para>
/// <b>A rota não carrega rede.</b> O escopo sai do <see cref="IRedeContext"/> dentro do
/// client, como em todo o resto da Web: com <c>redeId</c> na URL, trocar o número daria
/// acesso ao envio de outro inquilino.
/// </para>
/// </summary>
internal static class ComparacoesDownloadEndpoints
{
    public static IEndpointRouteBuilder MapComparacoesDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        // Papel, e não só autenticação: o ZIP é o insumo bruto da rede inteira — cadastro,
        // vendas, compras e a sugestão do ERP — enquanto a tela entrega o resultado já
        // apurado. A rota é onde a regra tem de morar, porque BaixarDados navega com
        // forceLoad para cá: fechar só o botão deixaria o arquivo a uma URL digitada de
        // distância. 403 (e não 404) porque quem chega aqui já vê a sessão e já sabe que ela
        // tem envio — a recusa não revela nada que a tela dele não mostre.
        app.MapGet("/comparacoes/{id:guid}/dados/download", DownloadDadosAsync)
           .RequireAuthorization(politica => politica.RequireRole(Papeis.PowerUser));
        return app;
    }

    private static async Task<IResult> DownloadDadosAsync(
        Guid id, ComparacoesApiClient api, CancellationToken ct)
    {
        var upstream = await api.AbrirDownloadDadosAsync(id, ct);

        if (upstream.StatusCode == HttpStatusCode.NotFound)
        {
            upstream.Dispose();
            return Results.Problem(
                title: "Arquivo não disponível",
                detail: "Esta comparação não tem um arquivo enviado, ou o arquivo não está mais " +
                        "no armazenamento. Se a comparação chegou a rodar, fale com o suporte técnico.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (!upstream.IsSuccessStatusCode)
        {
            upstream.Dispose();
            return Results.Problem(
                title: "Falha ao baixar o arquivo",
                detail: "Não foi possível baixar o arquivo agora. Tente novamente em alguns minutos.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Nome vem de quem sabe: a apiservice o lê de CargaStage.NomeArquivoOriginal. O
        // fallback existe só para não depender do header, não porque se espere ausência.
        var nome = upstream.Content.Headers.ContentDisposition?.FileNameStar
                   ?? upstream.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? $"comparacao-{id}.zip";

        // O disposal fica dentro do callback: Results.Stream só o invoca quando a resposta
        // já começou a ser escrita, então descartar antes fecharia o stream de origem.
        return Results.Stream(async body =>
        {
            using (upstream)
            {
                await upstream.Content.CopyToAsync(body, ct);
            }
        }, "application/zip", fileDownloadName: nome);
    }
}
