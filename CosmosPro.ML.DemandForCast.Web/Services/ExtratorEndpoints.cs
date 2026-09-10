namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Serve o executável que vem embutido nesta imagem (ver <see cref="ExtratorEmbutido"/>).
/// A Web é o único processo que o navegador alcança, então é dela a responsabilidade — a
/// apiservice não tem mais nada a ver com o extrator, e o bucket que ela mantinha no MinIO
/// deixou de existir.
/// </summary>
internal static class ExtratorEndpoints
{
    public static IEndpointRouteBuilder MapExtratorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/extrator/download", Download).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// <c>Results.File</c> com caminho físico, e não um stream montado à mão: o Kestrel
    /// responde com <c>sendfile</c>, honra <c>Range</c> e emite <c>ETag</c>/
    /// <c>Last-Modified</c>. Requisição parcial não é detalhe aqui — são ~118 MB indo para
    /// um servidor de farmácia, e antes deste desenho um download interrompido recomeçava do
    /// zero porque a origem era um stream do MinIO repassado por dois saltos.
    /// </summary>
    internal static IResult Download(ExtratorEmbutido extrator)
    {
        if (!extrator.Disponivel)
        {
            return Results.Problem(
                title: "Extrator não disponível",
                detail: "Esta instalação não traz o extrator. Fale com o suporte técnico da CosmosPro.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.File(
            extrator.CaminhoDoExecutavel,
            contentType: "application/octet-stream",
            fileDownloadName: "extrator.exe",
            enableRangeProcessing: true);
    }
}
