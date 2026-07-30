using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Escreve CSVs direto dentro de um ZIP, em streaming. Nada é materializado em
/// memória nem em arquivo temporário — o volume de estoque diário chega a
/// dezenas de milhões de linhas.
/// </summary>
internal sealed class CsvZipWriter(Stream output) : IDisposable
{
    private readonly ZipArchive _archive = new(output, ZipArchiveMode.Create, leaveOpen: false);

    public CsvEntryWriter CreateEntry(string entryName, IReadOnlyList<string> header) =>
        new(_archive.CreateEntry(entryName, CompressionLevel.Optimal), header);

    /// <summary>Grava um arquivo de texto solto no ZIP — usado pelo manifesto.json, que não é CSV.</summary>
    public void WriteText(string entryName, string content)
    {
        var entry = _archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    public void Dispose() => _archive.Dispose();
}

internal sealed class CsvEntryWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly int _columnCount;

    public long RowCount { get; private set; }

    internal CsvEntryWriter(ZipArchiveEntry entry, IReadOnlyList<string> header)
    {
        // UTF-8 sem BOM: o reader do Worker tolera BOM, outros consumidores não.
        _writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _columnCount = header.Count;
        // '\n' explícito (e não WriteLine) para o arquivo inteiro usar a mesma
        // quebra de linha independente do SO onde o extrator rodar.
        _writer.Write(string.Join(',', header.Select(Escape)));
        _writer.Write('\n');
    }

    /// <summary>Escreve a linha corrente de um reader, na ordem dos ordinais.</summary>
    public void WriteRow(IDataRecord record)
    {
        for (var i = 0; i < _columnCount; i++)
        {
            if (i > 0) _writer.Write(',');
            if (!record.IsDBNull(i)) _writer.Write(Escape(Format(record.GetValue(i))));
        }
        _writer.Write('\n');
        RowCount++;
    }

    public void WriteRow(params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) _writer.Write(',');
            if (values[i] is { } v) _writer.Write(Escape(Format(v)));
        }
        _writer.Write('\n');
        RowCount++;
    }

    /// <summary>
    /// Cultura invariante sempre: o reader do Worker faz parse com
    /// <see cref="CultureInfo.InvariantCulture"/> e quebraria com o locale pt-BR.
    /// </summary>
    private static string Format(object value) => value switch
    {
        string s => s,
        bool b => b ? "1" : "0",
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
        double db => db.ToString("0.####", CultureInfo.InvariantCulture),
        float f => f.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Escape(string value) =>
        value.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }
}
