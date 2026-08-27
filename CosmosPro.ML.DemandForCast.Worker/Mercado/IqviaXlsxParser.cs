using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using CosmosPro.ML.DemandForCast.Engine.Mercado;

namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

internal sealed record IqviaObservacao(
    DateOnly Mes, string Brick, string Bandeira, string Ean, decimal Unidades, decimal ValorCpp);

internal sealed record IqviaProduto(
    string Ean, string DescricaoLonga, string? Laboratorio, string? Molecula,
    string? AreaFarmacia, string? Nec1, string? Forma3, string? Classe4);

internal sealed record IqviaBrickPdv(string Brick, string Cnpj, string Bandeira);

internal sealed record IqviaParseResult(
    IReadOnlyList<IqviaObservacao> Observacoes,
    IReadOnlyList<IqviaProduto> Produtos,
    IReadOnlyList<IqviaBrickPdv> Pdvs,
    MercadoCargaResumo Resumo);

/// <summary>
/// Lê o relatório mensal da IQVIA (XLSX cross-tab por EAN × brick × bandeira × mês) e o
/// devolve em forma longa. Streaming de propósito: a planilha real tem ~60 MB de XML
/// descompactado, então nada aqui materializa DOM — <see cref="XmlReader"/> sobre as
/// entries do ZIP, sharedStrings primeiro, depois uma passada pela aba de dados.
///
/// <para>
/// <b>O cabeçalho de medida é contrato, e cabeçalho que não casa é falha, não linha
/// pulada.</b> Cada coluna de medida codifica as dimensões no próprio nome
/// ("Brick rpe 528-RJ ... Bandeira CONCORRENTES Mes 202506 Unidades"); ignorar uma
/// coluna quase-válida perderia um recorte inteiro em silêncio.
/// </para>
///
/// <para>
/// Três normalizações que não são estilo: (1) EAN vira só dígitos, e linha sem EAN é
/// descartada e contada — sem código não há com o que casar o cadastro; (2) células
/// com as duas medidas zeradas não viram observação (~72% do arquivo real), e a
/// distinção zero × não-coberto fica no resumo; (3) o brick da aba de PDVs usa '_'
/// onde o da aba de dados usa espaço ("526-RJ_VOLTA REDONDA_CENTRO" vs
/// "526-RJ VOLTA REDONDA CENTRO") — normalizado aqui, senão a ponte brick↔loja
/// nasceria quebrada.
/// </para>
/// </summary>
internal static partial class IqviaXlsxParser
{
    private const string AbaDeDados = "QUERY";

    [GeneratedRegex(@"^Brick rpe (?<brick>.+?) Bandeira (?<bandeira>.+?) Mes (?<mes>\d{6}) (?<metrica>Unidades|Real CPP)$")]
    private static partial Regex CabecalhoDeMedida();

    [GeneratedRegex(@"^(?<bandeira>.+?)\s*-\s*(?<cnpj>\d{8,14})$")]
    private static partial Regex PdvComCnpj();

    /// <summary>Dimensões conhecidas da aba de dados. Só o EAN e a descrição são exigidos.</summary>
    private static readonly string[] DimensoesConhecidas =
        ["Ean", "Produto Desc Longa", "Laboratorio", "Molecula", "Areas da Farmacia", "Nec 1", "Forma 3", "Classe 4"];

    public static IqviaParseResult Parse(Stream xlsx)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read, leaveOpen: true);

        var sheets = LerAbas(zip);
        if (!sheets.TryGetValue(AbaDeDados, out var abaDados))
        {
            throw new FormatException(
                $"O arquivo não tem a aba '{AbaDeDados}'. Abas encontradas: {string.Join(", ", sheets.Keys)}. " +
                "Envie o relatório mensal da IQVIA sem renomear as abas.");
        }

        var sharedStrings = LerSharedStrings(zip);

        var (observacoes, produtos, resumo) = LerAbaDeDados(zip, abaDados, sharedStrings);

        // A aba de PDVs tem nome livre ("quantidade PDV" no arquivo real) — localizada
        // por conter "PDV". Ausente não é erro: o dado principal continua utilizável,
        // só a ponte brick↔loja fica vazia.
        var abaPdv = sheets.FirstOrDefault(s => s.Key.Contains("PDV", StringComparison.OrdinalIgnoreCase));
        var pdvs = abaPdv.Value is null
            ? []
            : LerAbaDePdvs(zip, abaPdv.Value, sharedStrings);

        return new IqviaParseResult(observacoes, produtos, pdvs, resumo);
    }

    private static (IReadOnlyList<IqviaObservacao>, IReadOnlyList<IqviaProduto>, MercadoCargaResumo) LerAbaDeDados(
        ZipArchive zip, string entryPath, List<string> sharedStrings)
    {
        var dimIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var medidas = new Dictionary<int, (string Brick, string Bandeira, DateOnly Mes, bool EhUnidades)>();

        // (mes, brick, bandeira, ean) → medidas somadas. O arquivo real tem EANs
        // repetidos (52.837 linhas, 52.804 EANs distintos); sem a soma o PK composto
        // estouraria no bulk insert por causa de meia dúzia de duplicatas.
        var acc = new Dictionary<(DateOnly, string, string, string), (decimal Unidades, decimal ValorCpp)>();
        var produtos = new Dictionary<string, IqviaProduto>(StringComparer.Ordinal);

        int linhas = 0, semEan = 0;
        long zeradas = 0;

        foreach (var row in LerLinhas(zip, entryPath, sharedStrings))
        {
            if (row.Numero == 1)
            {
                MapearCabecalho(row.Celulas, dimIdx, medidas);
                continue;
            }

            linhas++;
            var ean = SoDigitos(Valor(row.Celulas, dimIdx, "Ean"));
            if (ean.Length == 0)
            {
                semEan++;
                continue;
            }

            if (!produtos.ContainsKey(ean))
            {
                produtos[ean] = new IqviaProduto(
                    Ean: ean,
                    DescricaoLonga: Valor(row.Celulas, dimIdx, "Produto Desc Longa"),
                    Laboratorio: ValorOuNulo(row.Celulas, dimIdx, "Laboratorio"),
                    Molecula: ValorOuNulo(row.Celulas, dimIdx, "Molecula"),
                    AreaFarmacia: ValorOuNulo(row.Celulas, dimIdx, "Areas da Farmacia"),
                    Nec1: ValorOuNulo(row.Celulas, dimIdx, "Nec 1"),
                    Forma3: ValorOuNulo(row.Celulas, dimIdx, "Forma 3"),
                    Classe4: ValorOuNulo(row.Celulas, dimIdx, "Classe 4"));
            }

            // As medidas vêm em pares de colunas (Unidades, Real CPP) por recorte; a
            // agregação por recorte acontece aqui, célula a célula.
            foreach (var (col, m) in medidas)
            {
                var raw = row.Celulas.GetValueOrDefault(col);
                if (string.IsNullOrEmpty(raw)) continue;

                var valor = ParseDecimal(raw, col, row.Numero);
                if (valor == 0m) continue;

                var key = (m.Mes, m.Brick, m.Bandeira, ean);
                var atual = acc.GetValueOrDefault(key);
                acc[key] = m.EhUnidades
                    ? (atual.Unidades + valor, atual.ValorCpp)
                    : (atual.Unidades, atual.ValorCpp + valor);
            }
        }

        var observacoes = new List<IqviaObservacao>(acc.Count);
        foreach (var ((mes, brick, bandeira, ean), v) in acc)
        {
            observacoes.Add(new IqviaObservacao(
                mes, brick, bandeira, ean,
                Unidades: Math.Round(v.Unidades, 3),
                // 2 casas: o XLSX carrega artefatos de float (113700.59999999999).
                ValorCpp: Math.Round(v.ValorCpp, 2)));
        }

        // Total de células de medida menos as que viraram observação — inclui vazias.
        var recortes = medidas.Values
            .Select(m => (m.Mes, m.Brick, m.Bandeira))
            .Distinct()
            .ToList();
        zeradas = (long)(linhas - semEan) * recortes.Count - observacoes.Count;

        var resumo = new MercadoCargaResumo(
            Meses: [.. recortes.Select(r => r.Mes).Distinct().Order()],
            Bricks: [.. recortes.Select(r => r.Brick).Distinct().Order()],
            Bandeiras: [.. recortes.Select(r => r.Bandeira).Distinct().Order()],
            LinhasDoArquivo: linhas,
            LinhasSemEan: semEan,
            CelulasZeradas: zeradas);

        return (observacoes, [.. produtos.Values], resumo);
    }

    private static void MapearCabecalho(
        IReadOnlyDictionary<int, string> celulas,
        Dictionary<string, int> dimIdx,
        Dictionary<int, (string, string, DateOnly, bool)> medidas)
    {
        foreach (var (col, textoBruto) in celulas)
        {
            var texto = textoBruto.Trim();
            if (texto.Length == 0) continue;

            if (DimensoesConhecidas.Contains(texto, StringComparer.OrdinalIgnoreCase))
            {
                dimIdx[texto] = col;
                continue;
            }

            var m = CabecalhoDeMedida().Match(texto);
            if (!m.Success)
            {
                throw new FormatException(
                    $"Coluna desconhecida na aba '{AbaDeDados}': '{texto}'. Esperava uma dimensão " +
                    $"({string.Join(", ", DimensoesConhecidas)}) ou uma medida no formato " +
                    "'Brick rpe <brick> Bandeira <bandeira> Mes <AAAAMM> Unidades|Real CPP'. " +
                    "Se o layout do relatório mudou, o importador precisa ser atualizado.");
            }

            medidas[col] = (
                m.Groups["brick"].Value,
                m.Groups["bandeira"].Value,
                ParseMes(m.Groups["mes"].Value, texto),
                m.Groups["metrica"].Value == "Unidades");
        }

        foreach (var obrigatoria in new[] { "Ean", "Produto Desc Longa" })
        {
            if (!dimIdx.ContainsKey(obrigatoria))
            {
                throw new FormatException($"A aba '{AbaDeDados}' não tem a coluna obrigatória '{obrigatoria}'.");
            }
        }

        if (medidas.Count == 0)
        {
            throw new FormatException($"A aba '{AbaDeDados}' não tem nenhuma coluna de medida (Unidades / Real CPP).");
        }
    }

    private static List<IqviaBrickPdv> LerAbaDePdvs(ZipArchive zip, string entryPath, List<string> sharedStrings)
    {
        // Chave (brick, cnpj): a aba real termina com uma seção de pivot ("Rótulos de
        // Linha...") que não casa com o regex e é ignorada; duplicata legítima não existe,
        // mas o primeiro vence para o parse ser determinístico.
        var pdvs = new Dictionary<(string Brick, string Cnpj), IqviaBrickPdv>();

        foreach (var row in LerLinhas(zip, entryPath, sharedStrings))
        {
            if (row.Numero == 1) continue; // header ("Brick rpe" | "PDV")

            var brick = row.Celulas.GetValueOrDefault(0)?.Trim();
            var pdv = row.Celulas.GetValueOrDefault(1)?.Trim();
            if (string.IsNullOrEmpty(brick) || string.IsNullOrEmpty(pdv)) continue;

            var m = PdvComCnpj().Match(pdv);
            if (!m.Success) continue;

            // Normaliza para a grafia da aba de dados: '_' vira espaço.
            var brickNormalizado = brick.Replace('_', ' ');
            var cnpj = m.Groups["cnpj"].Value;

            pdvs.TryAdd((brickNormalizado, cnpj),
                new IqviaBrickPdv(brickNormalizado, cnpj, m.Groups["bandeira"].Value.Trim()));
        }

        return [.. pdvs.Values];
    }

    private static DateOnly ParseMes(string aaaamm, string cabecalho)
    {
        var ano = int.Parse(aaaamm[..4], CultureInfo.InvariantCulture);
        var mes = int.Parse(aaaamm[4..], CultureInfo.InvariantCulture);
        if (ano is < 2000 or > 2100 || mes is < 1 or > 12)
        {
            throw new FormatException($"Mês inválido '{aaaamm}' no cabeçalho '{cabecalho}'.");
        }
        return new DateOnly(ano, mes, 1);
    }

    private static decimal ParseDecimal(string raw, int col, int linha)
    {
        // double, não decimal, porque o OOXML pode serializar em notação científica.
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            throw new FormatException($"Valor não numérico '{raw}' na coluna {col + 1}, linha {linha}.");
        }
        return (decimal)d;
    }

    private static string SoDigitos(string s)
    {
        // EAN pode vir como célula numérica; se o XML trouxer notação científica,
        // extrair dígitos do texto cru corromperia o código.
        if (s.Contains('E') || s.Contains('.'))
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : "";
        }
        return string.Concat(s.Where(char.IsAsciiDigit));
    }

    private static string Valor(IReadOnlyDictionary<int, string> celulas, Dictionary<string, int> dimIdx, string dim)
        => dimIdx.TryGetValue(dim, out var i) ? celulas.GetValueOrDefault(i, "").Trim() : "";

    private static string? ValorOuNulo(IReadOnlyDictionary<int, string> celulas, Dictionary<string, int> dimIdx, string dim)
    {
        var v = Valor(celulas, dimIdx, dim);
        return v.Length == 0 ? null : v;
    }

    // ---------------------------------------------------------------------------
    // Leitura do OOXML (streaming)
    // ---------------------------------------------------------------------------

    private readonly record struct Linha(int Numero, IReadOnlyDictionary<int, string> Celulas);

    /// <summary>Abas do workbook: nome → caminho da entry (ex.: "xl/worksheets/sheet1.xml").</summary>
    private static Dictionary<string, string> LerAbas(ZipArchive zip)
    {
        var rels = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var relStream = AbrirEntry(zip, "xl/_rels/workbook.xml.rels"))
        using (var r = XmlReader.Create(relStream))
        {
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship")
                {
                    var id = r.GetAttribute("Id");
                    var target = r.GetAttribute("Target");
                    if (id is not null && target is not null)
                    {
                        rels[id] = target.StartsWith("/", StringComparison.Ordinal)
                            ? target.TrimStart('/')
                            : $"xl/{target}";
                    }
                }
            }
        }

        var sheets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var wbStream = AbrirEntry(zip, "xl/workbook.xml"))
        using (var r = XmlReader.Create(wbStream))
        {
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                {
                    var nome = r.GetAttribute("name");
                    var rid = r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (nome is not null && rid is not null && rels.TryGetValue(rid, out var path))
                    {
                        sheets[nome] = path;
                    }
                }
            }
        }
        return sheets;
    }

    private static List<string> LerSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return result; // planilha 100% numérica é válida

        // Máquina de estados sobre nós de texto, sem ReadElementContentAsString:
        // aquele método deixa o reader EM CIMA do nó seguinte (o </si>), e o Read()
        // do topo do loop o pularia — o fim do <si> nunca seria visto e a lista
        // sairia vazia, fazendo todo cabeçalho degradar para o índice numérico cru.
        using var stream = entry.Open();
        using var r = XmlReader.Create(stream);
        string? atual = null;
        var dentroDeT = false;
        while (r.Read())
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Element when r.LocalName == "si":
                    atual = "";
                    break;
                case XmlNodeType.Element when r.LocalName == "t" && atual is not null:
                    dentroDeT = !r.IsEmptyElement;
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA when dentroDeT:
                    atual += r.Value;
                    break;
                case XmlNodeType.EndElement when r.LocalName == "t":
                    dentroDeT = false;
                    break;
                case XmlNodeType.EndElement when r.LocalName == "si":
                    result.Add(atual ?? "");
                    atual = null;
                    break;
            }
        }
        return result;
    }

    private static IEnumerable<Linha> LerLinhas(ZipArchive zip, string entryPath, List<string> sharedStrings)
    {
        using var stream = AbrirEntry(zip, entryPath);
        using var r = XmlReader.Create(stream);

        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "row") continue;

            var numero = int.Parse(r.GetAttribute("r") ?? "0", CultureInfo.InvariantCulture);
            var celulas = new Dictionary<int, string>();

            if (!r.IsEmptyElement)
            {
                using var row = r.ReadSubtree();
                int col = -1;
                string? tipo = null;
                while (row.Read())
                {
                    if (row.NodeType == XmlNodeType.Element && row.LocalName == "c")
                    {
                        col = ColunaDoRef(row.GetAttribute("r"));
                        tipo = row.GetAttribute("t");
                    }
                    else if (row.NodeType == XmlNodeType.Element && row.LocalName == "v" && col >= 0)
                    {
                        var v = row.ReadElementContentAsString();
                        celulas[col] = tipo == "s" && int.TryParse(v, out var idx) && idx < sharedStrings.Count
                            ? sharedStrings[idx]
                            : v;
                    }
                    else if (row.NodeType == XmlNodeType.Element && row.LocalName == "t" && tipo == "inlineStr" && col >= 0)
                    {
                        celulas[col] = row.ReadElementContentAsString();
                    }
                }
            }

            yield return new Linha(numero, celulas);
        }
    }

    private static int ColunaDoRef(string? cellRef)
    {
        if (cellRef is null) return -1;
        int n = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else break;
        }
        return n - 1;
    }

    private static Stream AbrirEntry(ZipArchive zip, string path)
        => (zip.GetEntry(path) ?? throw new FormatException(
                $"O arquivo não é um XLSX válido: entry '{path}' ausente."))
            .Open();
}
