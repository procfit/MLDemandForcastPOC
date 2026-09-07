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

    /// <summary>Limite de `take` da API — a paginacao daqui existe por causa dele.</summary>
    private const int PaginaDaApi = 200;

    /// <summary>
    /// Teto de linhas da planilha. Existe porque sem corte a regra A1 devolve ~45 mil avisos e
    /// montar isso em memoria no processo da Web nao e trabalho de uma requisicao de download.
    /// Quando ele morde, a capa DIZ que a lista esta truncada — o pior desfecho possivel e uma
    /// planilha incompleta que se apresenta como completa.
    /// </summary>
    private const int TetoDeLinhas = 20_000;

    private static async Task<IResult> ExportarAsync(
        MercadoApiClient api,
        CancellationToken ct,
        [FromQuery] decimal? corteMinimo = null,
        [FromQuery] string? brick = null,
        [FromQuery] string? areaFarmacia = null,
        [FromQuery] string? laboratorio = null)
    {
        var corte = corteMinimo ?? 200m;

        // PAGINA ATE O TOTAL. A versao anterior pedia uma pagina de 200 e chamava aquilo de
        // "o recorte inteiro": a API limita `take` a 200 de proposito, entao um recorte maior
        // saia truncado com a CAPA DECLARANDO O TOTAL VERDADEIRO — planilha e capa se
        // contradizendo, a mesma classe de defeito que a exportacao de itens tinha nos filtros.
        // Com o corte padrao de 200 unidades o recorte real cabia (156 avisos), o que e
        // exatamente por que passou sem sintoma: baixar o corte na tela e a operacao que revela.
        var primeira = await api.OportunidadesAsync(
            corte, brick, areaFarmacia, laboratorio, skip: 0, take: PaginaDaApi, ct);

        var itens = new List<OportunidadeDeSortimento>(primeira.Itens);
        while (itens.Count < primeira.Total && itens.Count < TetoDeLinhas)
        {
            var proxima = await api.OportunidadesAsync(
                corte, brick, areaFarmacia, laboratorio, skip: itens.Count, take: PaginaDaApi, ct);

            // Pagina vazia com total maior significa que o recorte mudou entre as idas; parar e
            // declarar o que veio e melhor que girar em falso.
            if (proxima.Itens.Count == 0) break;
            itens.AddRange(proxima.Itens);
        }

        var truncado = itens.Count < primeira.Total;
        var pagina = primeira with { Itens = itens };

        var bytes = OportunidadesExcelExporter.Gerar(
            pagina, corte, brick, areaFarmacia, laboratorio, truncado, DateTimeOffset.UtcNow);

        var nome = $"oportunidades-sortimento_{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return Results.File(bytes, OportunidadesExcelExporter.XlsxContentType, nome);
    }
}
