using System.Globalization;
using System.Text.RegularExpressions;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Sink duplo: o painel do form e um arquivo por dia ao lado do executável. O
/// arquivo existe porque o operador roda isto num terminal do cliente, e sem ele
/// toda falha chega até aqui como "não sei, deu erro".
/// </summary>
internal sealed partial class ExtratorLog(string pasta, Action<string>? tela = null, Func<DateTime>? agora = null)
{
    private readonly Func<DateTime> _agora = agora ?? (() => DateTime.Now);
    private readonly Lock _gravarLock = new();

    public string CaminhoDeHoje => Path.Combine(pasta, NomeDoArquivo(_agora()));

    public static string NomeDoArquivo(DateTime dia) =>
        $"extrator-log-{dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.txt";

    public void Escrever(string mensagem)
    {
        var linha = Formatar(mensagem);
        tela?.Invoke(linha);
        Gravar(linha);
    }

    public void EscreverSoNoArquivo(string mensagem) => Gravar(Formatar(mensagem));

    private string Formatar(string mensagem) =>
        $"{_agora().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {Redigir(mensagem)}";

    // Chamado tanto da UI quanto da thread do pool (Retentativa logando um retry
    // dentro do Task.Run de ExecutarAsync) -- sem serializar, dois AppendAllText
    // concorrentes colidem no mesmo arquivo, um deles estoura IOException, o catch
    // abaixo engole, e a linha desaparece em silêncio.
    private void Gravar(string linha)
    {
        lock (_gravarLock)
        {
            try
            {
                Directory.CreateDirectory(pasta);
                File.AppendAllText(CaminhoDeHoje, linha + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or DirectoryNotFoundException)
            {
                // Pasta somente leitura ou caminho inválido: perder o log não justifica
                // derrubar a operação. Mesma política de AppConfig.Save().
            }
        }
    }

    public static string Redigir(string texto) => SenhaNaConnectionString().Replace(texto, "Password=***");

    // SqlConnectionStringBuilder quotes password values when they contain ';', quotes, or whitespace,
    // and escapes an internal quote of the same kind by doubling it (Password="Sec""re't" for the
    // value Sec"re't). A branch that stops at the first quote — "[^"]*" — matches only up to that
    // doubled pair's first character and leaves the rest of the secret in the log; each quoted branch
    // must allow a doubled quote as part of the value: "(?:[^"]|"")*". Quoted alternatives must still
    // come first, or the unquoted fallback [^;]* wins on a quoted value and undoes the fix.
    [GeneratedRegex("""\b(password|pwd)\s*=\s*(?:"(?:[^"]|"")*"|'(?:[^']|'')*'|[^;]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex SenhaNaConnectionString();
}
