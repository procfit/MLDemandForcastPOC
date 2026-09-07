using CosmosPro.ML.DemandForCast.Engine;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Exportação da tabulação das avaliações.
///
/// <para>
/// <b>Endpoint HTTP e não página Blazor</b>, pelo mesmo motivo das outras duas exportações: um
/// componente interativo teria de trazer o arquivo para o circuito antes de empurrá-lo ao
/// navegador. Um endpoint comum deixa o download ser um download.
/// </para>
///
/// <para>
/// <b>Sem parâmetro de recorte, e isso é a decisão.</b> As outras exportações espelham os
/// filtros da tela porque o comprador exporta o que está olhando; aqui o pedido é o oposto —
/// os dados brutos, inteiros, para quem conduz a pesquisa tabular por fora. Um filtro nesta
/// rota produziria planilha parcial que se apresenta como o conjunto.
/// </para>
/// </summary>
internal static class AvaliacoesExcelEndpoints
{
    public static IEndpointRouteBuilder MapAvaliacoesExcelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/avaliacoes/excel", ExportarAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ExportarAsync(
        QuestionariosApiClient api,
        IRedeContext redeContext,
        EngineDbContext db,
        CancellationToken ct)
    {
        var tabulacao = await api.GetTabulacaoAsync(ct);

        // O escopo já veio do IRedeContext dentro do client; aqui só o nome, para a capa não
        // dizer "rede 1030" a quem vai ler a planilha numa reunião.
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var nome = await db.Redes
            .AsNoTracking()
            .Where(r => r.Id == redeId)
            .Select(r => r.Nome)
            .FirstOrDefaultAsync(ct);

        var bytes = AvaliacoesExcelExporter.Gerar(tabulacao, nome, DateTimeOffset.UtcNow);
        var arquivo = $"avaliacoes-poc_{DateTime.Now:yyyyMMdd-HHmm}.xlsx";

        return Results.File(bytes, AvaliacoesExcelExporter.XlsxContentType, arquivo);
    }
}
