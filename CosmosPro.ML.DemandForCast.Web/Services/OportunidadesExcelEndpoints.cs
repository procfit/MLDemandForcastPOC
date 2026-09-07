using Microsoft.AspNetCore.Mvc;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Exportação da lista de oportunidades de sortimento.
///
/// <para>
/// <b>Endpoint HTTP e não página Blazor</b>, pelo mesmo motivo da exportação da tabela de
/// itens: um componente interativo teria de trazer o arquivo inteiro para o circuito para
/// depois empurrá-lo ao navegador. Um endpoint comum deixa o download ser um download.
/// </para>
///
/// <para>
/// A rota recebe <b>o mesmo recorte</b> que está na tela e o reproduz na consulta. Parâmetro
/// esquecido aqui não dá erro: produz planilha mais ampla do que o comprador viu, com o
/// contador da capa desmentindo a tela.
/// </para>
/// </summary>
internal static class OportunidadesExcelEndpoints
{
    public static IEndpointRouteBuilder MapOportunidadesExcelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/mercado/oportunidades/excel", ExportarAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ExportarAsync(
        MercadoApiClient api,
        CancellationToken ct,
        [FromQuery] decimal? corteMinimo = null,
        [FromQuery] string? brick = null,
        [FromQuery] string? areaFarmacia = null,
        [FromQuery] string? laboratorio = null)
    {
        var corte = corteMinimo ?? 200m;

        // take alto de propósito: a planilha traz o recorte inteiro, e não a página. É o pedido
        // do patrocinador — exportar o que está filtrado, não o que caberia numa tela.
        var pagina = await api.OportunidadesAsync(
            corte, brick, areaFarmacia, laboratorio, skip: 0, take: 200, ct);

        var bytes = OportunidadesExcelExporter.Gerar(
            pagina, corte, brick, areaFarmacia, laboratorio, DateTimeOffset.UtcNow);

        var nome = $"oportunidades-sortimento_{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return Results.File(bytes, OportunidadesExcelExporter.XlsxContentType, nome);
    }
}
