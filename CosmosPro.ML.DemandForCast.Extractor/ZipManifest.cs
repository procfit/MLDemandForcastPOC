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
    string VersaoExtractor)
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
}
