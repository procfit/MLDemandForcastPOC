namespace CosmosPro.ML.DemandForCast.Extractor;

internal sealed record ExtractionRequest
{
    public required string ConnectionString { get; init; }

    /// <summary>
    /// A extração é sempre escopada a UMA sugestão (F14): lojas e produtos são
    /// derivados dela, não escolhidos à parte pelo usuário.
    /// </summary>
    public required long SugestaoId { get; init; }
    public required DateOnly DataInicial { get; init; }
    public required DateOnly DataFinal { get; init; }
    public required string OutputDirectory { get; init; }
}

internal sealed record ExtractionProgress(string FileName, int FileIndex, int FileCount, long RowsWritten);

internal sealed record ExtractionResult(
    string ZipPath,
    long ZipBytes,
    IReadOnlyDictionary<string, long> RowsByFile,
    IReadOnlyList<string> Warnings);

internal sealed record LojaOption(int LojaId, string Nome)
{
    public override string ToString() => $"{LojaId} - {Nome}";
}

/// <summary>Cabeçalho de uma sugestão, como vem de SUGESTOES_COMPRAS sozinha.</summary>
internal sealed record SugestaoCatalogoCabecalho(
    long SugestaoId,
    string? Descricao,
    DateTime DataHora,
    byte TipoCalculo,
    int DiasCoberturaMax);

/// <summary>Linhas e lojas de uma sugestão, contadas em SUGESTOES_COMPRAS_RESULTADO.</summary>
internal sealed record SugestaoContagem(long SugestaoId, int QtdLinhas, int QtdLojas);
