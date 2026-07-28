namespace CosmosPro.ML.DemandForCast.Extractor;

internal sealed record ExtractionRequest
{
    public required string ConnectionString { get; init; }
    public required IReadOnlyList<int> LojaIds { get; init; }
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
