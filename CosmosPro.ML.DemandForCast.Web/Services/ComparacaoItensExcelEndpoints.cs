using Microsoft.AspNetCore.Mvc;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Entrega a planilha dos itens de uma comparação.
///
/// <para>
/// <b>Endpoint HTTP e não botão de circuito, de propósito.</b> O padrão de exportação da tela
/// de sugestão de compra (F8) devolve o arquivo por interop JS em base64, e funciona porque lá
/// a tela já tem os dados em memória. Aqui a população é a sugestão inteira do ERP — 20 mil
/// itens na Retiro —, e mandar isso pelo circuito Blazor em base64 seria empurrar megabytes por
/// SignalR para produzir um download que o navegador faz sozinho, com barra de progresso, se a
/// rota existir.
/// </para>
///
/// <para>
/// A rota não carrega rede: o escopo sai do <see cref="IRedeContext"/> dentro do client, como no
/// resto da Web. Com <c>redeId</c> na URL, trocar o número exportaria o dado comercial de outro
/// inquilino para uma planilha.
/// </para>
/// </summary>
internal static class ComparacaoItensExcelEndpoints
{
    public static IEndpointRouteBuilder MapComparacaoItensExcelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/comparacoes/{id:guid}/itens/excel", ExportarAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ExportarAsync(
        Guid id,
        ComparacoesApiClient api,
        CancellationToken ct,
        [FromQuery] int? lojaId = null,
        [FromQuery] string? categoria = null,
        [FromQuery] string? curva = null,
        [FromQuery] string? orderBy = null,
        [FromQuery] bool desc = true)
    {
        var sessao = await api.GetAsync(id, ct);
        if (sessao is null)
        {
            return Results.Problem(
                title: "Comparação não encontrada",
                detail: "Esta comparação não existe, ou não pertence à rede em que você está.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var filtro = new FiltroDeItens(lojaId, categoria, curva);

        // A primeira página serve só para os totais e o total sem filtro, que descrevem o
        // recorte na aba de capa. take mínimo: as linhas vêm da rota de exportação.
        var pagina = await api.GetItensAsync(id, skip: 0, take: 1, orderBy, desc, filtro, ct);
        var itens = await api.GetItensParaExportacaoAsync(id, orderBy, desc, filtro, ct);
        var disponiveis = await api.GetFiltrosDeItensAsync(id, ct);

        var bytes = ComparacaoItensExcelExporter.Build(
            id, sessao.Nome, sessao.SugestaoId, filtro, disponiveis,
            pagina.Totais, pagina.TotalSemFiltro, itens, DateTimeOffset.UtcNow);

        var nome = $"comparacao-itens_{sessao.SugestaoId?.ToString() ?? id.ToString()[..8]}{Sufixo(filtro)}.xlsx";
        return Results.File(bytes, ComparacaoItensExcelExporter.XlsxContentType, nome);
    }

    /// <summary>
    /// Marca o recorte no nome do arquivo. Sem isto, duas exportações de filtros diferentes
    /// chegam ao disco como "comparacao-itens (1).xlsx" e ninguém sabe qual é qual.
    /// </summary>
    private static string Sufixo(FiltroDeItens filtro)
    {
        if (!filtro.Algum) return "";

        var partes = new List<string>();
        if (filtro.LojaId is { } loja) partes.Add($"loja{loja}");
        if (!string.IsNullOrWhiteSpace(filtro.Categoria))
            partes.Add(filtro.Categoria == FiltroDeItens.Ausente ? "sem-categoria" : Limpar(filtro.Categoria));
        if (!string.IsNullOrWhiteSpace(filtro.Curva))
            partes.Add(filtro.Curva == FiltroDeItens.Ausente ? "sem-curva" : $"curva{Limpar(filtro.Curva)}");

        return "_" + string.Join("-", partes);
    }

    private static string Limpar(string valor) =>
        new(valor.Where(c => char.IsLetterOrDigit(c)).Take(24).ToArray());
}
