using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace CosmosPro.ML.DemandForCast.Tests.Shared.Xlsx;

/// <summary>
/// Constrói em memória um XLSX com a forma do relatório mensal da IQVIA (aba "QUERY"
/// cross-tab + aba de PDVs). O arquivo real é dado licenciado e não entra no repo —
/// todo teste do parser e do validador usa este builder.
///
/// <para>
/// Fiel ao arquivo real no que o parser depende: strings viram sharedStrings
/// (<c>t="s"</c>), números ficam inline no <c>&lt;v&gt;</c>, células têm referência
/// (<c>r="B2"</c>) e a aba de PDVs usa '_' no brick onde a QUERY usa espaço.
/// </para>
/// </summary>
public sealed class IqviaXlsxBuilder(string abaDados = "QUERY", string? abaPdv = "quantidade PDV")
{
    private readonly List<object?[]> _linhas = [];
    private readonly List<(string Brick, string Pdv)> _pdvs = [];
    private string[] _colunas = [];

    /// <summary>Cabeçalho de medida no formato do relatório real.</summary>
    public static string Medida(string brick, string bandeira, string aaaamm, string metrica)
        => $"Brick rpe {brick} Bandeira {bandeira} Mes {aaaamm} {metrica}";

    public IqviaXlsxBuilder WithColunas(params string[] colunas)
    {
        _colunas = colunas;
        return this;
    }

    public IqviaXlsxBuilder AddLinha(params object?[] valores)
    {
        _linhas.Add(valores);
        return this;
    }

    public IqviaXlsxBuilder AddPdv(string brick, string pdv)
    {
        _pdvs.Add((brick, pdv));
        return this;
    }

    public MemoryStream Build()
    {
        var shared = new List<string>();
        var sharedIdx = new Dictionary<string, int>(StringComparer.Ordinal);

        int Indice(string s)
        {
            if (!sharedIdx.TryGetValue(s, out var i))
            {
                i = shared.Count;
                shared.Add(s);
                sharedIdx[s] = i;
            }
            return i;
        }

        var sheet1 = MontarSheet([_colunas, .. _linhas], Indice);

        var pdvLinhas = new List<object?[]> { new object?[] { "Brick rpe", "PDV" } };
        pdvLinhas.AddRange(_pdvs.Select(p => new object?[] { p.Brick, p.Pdv }));
        var sheet2 = abaPdv is null ? null : MontarSheet(pdvLinhas, Indice);

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(zip, "[Content_Types].xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  {(sheet2 is null ? "" : """<Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""")}
                  <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
                </Types>
                """);

            Entry(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);

            Entry(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>
                    <sheet name="{SecurityElement.Escape(abaDados)}" sheetId="1" r:id="rId1"/>
                    {(sheet2 is null ? "" : $"""<sheet name="{SecurityElement.Escape(abaPdv)}" sheetId="2" r:id="rId2"/>""")}
                  </sheets>
                </workbook>
                """);

            Entry(zip, "xl/_rels/workbook.xml.rels", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  {(sheet2 is null ? "" : """<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>""")}
                  <Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
                </Relationships>
                """);

            var ss = new StringBuilder();
            ss.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            ss.Append($"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{shared.Count}" uniqueCount="{shared.Count}">""");
            foreach (var s in shared)
            {
                ss.Append($"<si><t>{SecurityElement.Escape(s)}</t></si>");
            }
            ss.Append("</sst>");
            Entry(zip, "xl/sharedStrings.xml", ss.ToString());

            Entry(zip, "xl/worksheets/sheet1.xml", sheet1);
            if (sheet2 is not null)
            {
                Entry(zip, "xl/worksheets/sheet2.xml", sheet2);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private static string MontarSheet(IReadOnlyList<object?[]> linhas, Func<string, int> indice)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (int r = 0; r < linhas.Count; r++)
        {
            sb.Append($"""<row r="{r + 1}">""");
            var valores = linhas[r];
            for (int c = 0; c < valores.Length; c++)
            {
                var v = valores[c];
                if (v is null) continue;
                var cellRef = $"{Coluna(c)}{r + 1}";
                if (v is string s)
                {
                    sb.Append($"""<c r="{cellRef}" t="s"><v>{indice(s)}</v></c>""");
                }
                else
                {
                    var num = Convert.ToDecimal(v, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture);
                    sb.Append($"""<c r="{cellRef}"><v>{num}</v></c>""");
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string Coluna(int idx)
    {
        var s = "";
        idx++;
        while (idx > 0)
        {
            idx--;
            s = (char)('A' + idx % 26) + s;
            idx /= 26;
        }
        return s;
    }

    private static void Entry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
