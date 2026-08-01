namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Janela de dados que o ZIP precisa cobrir para uma sugestão datada em T.
/// <para>
/// Antes de T: histórico para o modelo aprender. O pipeline de features exige no
/// mínimo 34 dias por SKU×loja, mas 34 dias não pegam sazonalidade — usamos 12 meses.
/// Depois de T: até T + dias de cobertura, que é o período que a compra deveria
/// suprir e portanto o único que revela quem acertou.
/// </para>
/// </summary>
internal sealed record ExtractionWindow(
    DateOnly Inicio, DateOnly Fim, bool Viavel, string? MotivoInviabilidade)
{
    private const int MesesHistorico = 12;

    public static ExtractionWindow Derive(DateOnly dataSugestao, int diasCobertura, DateOnly hoje)
    {
        var fim = dataSugestao.AddDays(diasCobertura);
        var inicio = dataSugestao.AddMonths(-MesesHistorico);

        // A cobertura tem de ter terminado: sem as vendas do periodo nao ha como
        // dizer quem acertou.
        if (fim > hoje)
        {
            var limite = hoje.AddDays(-diasCobertura);
            return new ExtractionWindow(inicio, fim, false,
                $"Esta sugestão é de {dataSugestao:dd/MM/yyyy} e cobre {diasCobertura} dias. " +
                $"As vendas que provariam quem acertou ainda não aconteceram. " +
                $"Escolha uma sugestão de até {limite:dd/MM/yyyy}.");
        }

        return new ExtractionWindow(inicio, fim, true, null);
    }
}
