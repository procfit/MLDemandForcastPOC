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

    /// <summary>
    /// Recorte de lojas DENTRO da sugestão -- não escolha livre de lojas, que é o que a
    /// F14 removeu. <c>null</c> significa todas as lojas que a sugestão cita.
    /// </summary>
    public IReadOnlyList<int>? LojaIds { get; init; }
}

internal sealed record ExtractionProgress(string FileName, int FileIndex, int FileCount, long RowsWritten);

internal sealed record ExtractionResult(
    string ZipPath,
    long ZipBytes,
    IReadOnlyDictionary<string, long> RowsByFile,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<int> LojasExportadas,
    int LojasNaSugestao);

internal sealed record LojaOption(int LojaId, string Nome)
{
    public override string ToString() => $"{LojaId} - {Nome}";
}

/// <summary>
/// Cabeçalho de uma sugestão, como vem de SUGESTOES_COMPRAS sozinha.
/// <para>
/// <b>Sem cobertura aqui.</b> Ela morava neste registro, lida do
/// <c>MAX(DIAS_CURVA_A..E)</c>, e o campo estava errado: é o parâmetro do método 2 e vem
/// zerado em 83% das sugestões de eMax/eSeg — o método que este projeto compara. Passou a
/// vir em <see cref="SugestaoContagem.DiasCoberturaMax"/>, do <c>DIAS_ESTOQUE</c> dos itens.
/// </para>
/// </summary>
internal sealed record SugestaoCatalogoCabecalho(
    long SugestaoId,
    string? Descricao,
    DateTime DataHora,
    byte TipoCalculo);

/// <summary>
/// Linhas, lojas e cobertura de uma sugestão, de SUGESTOES_COMPRAS_RESULTADO.
/// <para>
/// <see cref="DiasCoberturaMax"/> é o maior <c>DIAS_ESTOQUE</c> entre os itens — até onde a
/// janela precisa ir para cobrir o item mais longo. Zero quando a sugestão não tem item
/// nenhum, e aí a janela derivada é inviável por construção.
/// </para>
/// </summary>
internal sealed record SugestaoContagem(
    long SugestaoId, int QtdLinhas, int QtdLojas, int DiasCoberturaMax);

/// <summary>Uma loja citada pela sugestão, com o peso que ela tem nela.</summary>
internal sealed record LojaDaSugestao(int LojaId, string Nome, int Itens);
