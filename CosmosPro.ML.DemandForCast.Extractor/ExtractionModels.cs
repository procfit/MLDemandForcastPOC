using System.Globalization;

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

    /// <summary>
    /// Instante usado por <see cref="ZipNaming.BuildPath"/> para nomear o ZIP.
    /// <para>
    /// <c>null</c> quando quem chama não perguntou nada antes -- hoje só o modo linha de
    /// comando (<see cref="ExtractorCli"/>), que não tem confirmação e portanto não tem um
    /// instante prévio para threading. Nesse caso <see cref="ExtractionService.Run"/> usa o
    /// instante da própria gravação (ver <see cref="ExtractionService.ResolverInstante"/>).
    /// O form sempre preenche este campo com o instante em que perguntou ao operador --
    /// duas chamadas independentes a <c>DateTime.Now</c> (uma na pergunta, outra na
    /// gravação) deixavam o nome perguntado e o nome gravado discordarem quando o minuto
    /// virava entre os dois.
    /// </para>
    /// </summary>
    public DateTime? Instante { get; init; }
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

/// <summary>
/// Qual dos três desfechos a análise da sugestão selecionada produziu, para a tela pintar
/// o semáforo. <see cref="Ressalva"/> não é meio-termo entre os outros dois: ela extrai
/// igual a <see cref="Viavel"/> — o que muda é que há algo a dizer antes.
/// </summary>
internal enum SinalDaAnalise { Viavel, Ressalva, Recusa }

/// <summary>
/// O que a tela mostra depois de analisar a sugestão selecionada, e se o Extrair liga.
/// <para>
/// Mora fora do <c>MainForm</c> porque é a regra que decide se o comprador consegue
/// exportar — a mesma que já custou a uma rede inteira não conseguir exportar nada — e um
/// <c>Form</c> não é instanciável num teste.
/// </para>
/// </summary>
/// <param name="Rotulo">Linha do rótulo ao lado do semáforo, que tem 320×30.</param>
/// <param name="Aviso">
/// Texto longo que não cabe no rótulo e vai para o log. Nulo quando não há o que avisar —
/// aviso em toda extração é aviso que ninguém lê.
/// </param>
internal readonly record struct DesfechoDaAnalise(string Rotulo, string? Aviso, SinalDaAnalise Sinal)
{
    public bool PodeExtrair => Sinal is not SinalDaAnalise.Recusa;

    public static DesfechoDaAnalise De(SugestaoContagem contagem, ExtractionWindow janela)
    {
        var itens = $"{contagem.QtdLinhas:N0} itens · {contagem.QtdLojas:N0} loja(s)";

        // O motivo por extenso, e não um "inviável" seco: ele diz o que escolher em vez
        // desta, que é a única coisa acionável para quem está na tela.
        if (!janela.Viavel)
            return new DesfechoDaAnalise($"{itens} · {janela.MotivoInviabilidade}", null, SinalDaAnalise.Recusa);

        var rotulo = $"{itens} · {janela.Descricao} · cobertura {contagem.DiasCoberturaMax} dia(s)";

        return janela.Ressalva is { } ressalva
            ? new DesfechoDaAnalise(rotulo, ressalva, SinalDaAnalise.Ressalva)
            : new DesfechoDaAnalise(rotulo, null, SinalDaAnalise.Viavel);
    }
}

/// <summary>Uma loja citada pela sugestão, com o peso que ela tem nela.</summary>
internal sealed record LojaDaSugestao(int LojaId, string Nome, int Itens)
{
    public override string ToString() => $"{LojaId} · {Nome} · {Itens:N0} itens";
}

/// <summary>
/// Nome do ZIP de extração — fonte única para quem grava o arquivo
/// (<see cref="ExtractionService.Run"/>) e quem precisa checar se ele já existe antes de
/// perguntar (<c>MainForm.ExtrairAsync</c>, CHANGE 3). Duplicar a regra nos dois lugares
/// deixaria a tela perguntar sobre um arquivo e o serviço sobrescrever outro.
/// <para>
/// Granularidade de minuto, não de segundo: duas extrações no mesmo minuto colidem no
/// mesmo nome de propósito — essa colisão é o que a confirmação de sobrescrita existe
/// para avisar, não um bug a esconder aumentando a resolução do timestamp.
/// </para>
/// </summary>
internal static class ZipNaming
{
    public static string BuildPath(string outputDirectory, DateTime timestamp) =>
        Path.Combine(outputDirectory, FileName(timestamp));

    private static string FileName(DateTime timestamp) =>
        $"extracao-pbs_{timestamp.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.zip";
}
