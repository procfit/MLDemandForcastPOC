using System.Text.Json;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Declara, na raiz do ZIP, qual sugestão do PBS foi extraída e qual janela de
/// dados a acompanha. A sessão de comparação (F14) lê isto para se vincular ao
/// upload sem depender do usuário digitar nada.
/// </summary>
internal sealed record ZipManifest(
    long SugestaoId,
    string? SugestaoDescricao,
    DateTime SugestaoDataHora,
    byte SugestaoTipoCalculo,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    string VersaoExtractor,
    // Quantos SKUs citados pela sugestão entraram em produtos.csv como
    // placeholder (sem cadastro em PRODUTOS no PBS). Precisa estar no manifesto
    // e não só no log do extrator: quem consome o ZIP (a sessão de comparação
    // F14) precisa avisar "N itens sem cadastro" ao comprador, não deixar ele
    // descobrir olhando uma célula vazia na tabela de itens.
    int SkusSemCadastro,
    // Quais lojas da sugestão entraram no ZIP, e quantas ela tinha. A comparação
    // pontua a sugestão do ERP RESTRITA a estas lojas; sem os dois números, um
    // resultado de 3 lojas é indistinguível de um da rede inteira.
    IReadOnlyList<int> LojasExportadas,
    int LojasNaSugestao)
{
    public const string EntryName = "manifesto.json";

    // System.Text.Json já formata número/data em invariant culture por padrão
    // (não consulta CultureInfo.CurrentCulture) — sem naming policy para a
    // casing do JSON ficar estável e igual ao nome dos campos em C#, do lado de
    // quem lê (o service pode rodar num SO com outra cultura ou plataforma).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string Escrever(ZipManifest manifesto) =>
        JsonSerializer.Serialize(manifesto, JsonOptions);

    public static ZipManifest Ler(string json)
    {
        var manifesto = JsonSerializer.Deserialize<ZipManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("manifesto.json vazio ou inválido.");

        if (manifesto.SugestaoId <= 0)
        {
            throw new InvalidOperationException(
                "manifesto.json sem SugestaoId — o ZIP não pode ser vinculado a uma sugestão.");
        }

        return manifesto;
    }

    /// <summary>Versão do assembly do extrator — nunca hardcoded, para o manifesto refletir o build real.</summary>
    public static string VersaoAtual() =>
        typeof(ZipManifest).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Quando este executável foi gerado. Mora ao lado de <see cref="VersaoAtual"/> porque as
    /// duas respondem à mesma pergunta — <i>que binário é este?</i> — e separá-las convidaria
    /// uma delas a divergir.
    /// <para>
    /// Vem da data de escrita do arquivo, e <b>não</b> do timestamp do cabeçalho PE: o SDK do
    /// .NET compila de forma determinística por default, e nesse modo aquele campo é um hash
    /// do conteúdo, não uma data — exibi-lo daria "1976" ou algo igualmente absurdo.
    /// </para>
    /// <para>
    /// Devolve <c>null</c> quando o caminho do processo não existe (host que roda o assembly
    /// sem executável próprio, como o runner de testes) em vez de inventar um instante: a tela
    /// que mostra isto precisa poder dizer "desconhecido".
    /// </para>
    /// </summary>
    public static DateTime? GeradoEm()
    {
        var caminho = Environment.ProcessPath;
        if (string.IsNullOrEmpty(caminho) || !File.Exists(caminho)) return null;

        try
        {
            return File.GetLastWriteTime(caminho);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
