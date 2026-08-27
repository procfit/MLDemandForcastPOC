using System.Globalization;
using System.IO.Compression;
using System.Text;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;

namespace CosmosPro.ML.DemandForCast.Tests.Shared.Csv;

/// <summary>
/// Constrói um ZIP em memória com os 6 CSVs esperados pelo endpoint de import.
/// Compõe rows tipadas (geradas por fakers) em arquivos CSV com separador `,`,
/// formato ISO de data, ponto decimal — o que o validator e o worker esperam.
/// </summary>
public sealed class CsvZipBuilder
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public CsvZipBuilder WithLojas(IReadOnlyList<LojaRow> rows) => Add("lojas.csv", Build(rows,
        ["LojaId", "Nome", "UF", "Cidade", "Regiao", "Perfil", "DiasOperacaoSemana", "DataAbertura", "Ativo", "Cnpj"],
        r => [r.LojaId, r.Nome, r.UF, r.Cidade, r.Regiao, r.Perfil, r.DiasOperacaoSemana, r.DataAbertura, r.Ativo ? 1 : 0, r.Cnpj]));

    public CsvZipBuilder WithProdutos(IReadOnlyList<ProdutoRow> rows) => Add("produtos.csv", Build(rows,
        ["Sku", "Nome", "Categoria", "Subcategoria", "Fabricante", "PrincipioAtivo", "Apresentacao", "Ean", "RegistroAnvisa", "ListaControle", "ClasseTerapeutica", "Ativo"],
        r => [r.Sku, r.Nome, r.Categoria, r.Subcategoria, r.Fabricante, r.PrincipioAtivo, r.Apresentacao, r.Ean, r.RegistroAnvisa, r.ListaControle, r.ClasseTerapeutica, r.Ativo ? 1 : 0]));

    public CsvZipBuilder WithVendas(IReadOnlyList<VendaRow> rows) => Add("vendas.csv", Build(rows,
        ["Data", "LojaId", "Sku", "Quantidade", "PrecoUnitario", "ValorTotal"],
        r => [r.Data, r.LojaId, r.Sku, r.Quantidade, r.PrecoUnitario, r.ValorTotal]));

    public CsvZipBuilder WithEstoquesDiarios(IReadOnlyList<EstoqueDiarioRow> rows) => Add("estoques_diarios.csv", Build(rows,
        ["Data", "LojaId", "Sku", "QuantidadeEmEstoque"],
        r => [r.Data, r.LojaId, r.Sku, r.QuantidadeEmEstoque]));

    public CsvZipBuilder WithCompras(IReadOnlyList<CompraRow> rows) => Add("compras.csv", Build(rows,
        ["DataPedido", "DataRecebimento", "LojaId", "Sku", "Quantidade", "Fornecedor"],
        r => [r.DataPedido, r.DataRecebimento, r.LojaId, r.Sku, r.Quantidade, r.Fornecedor]));

    public CsvZipBuilder WithPromocoes(IReadOnlyList<PromocaoRow> rows) => Add("promocoes.csv", Build(rows,
        ["DataInicio", "DataFim", "Sku", "LojaId", "Tipo", "DescontoPct"],
        r => [r.DataInicio, r.DataFim, r.Sku, r.LojaId, r.Tipo, r.DescontoPct]));

    /// <summary>Substitui o conteúdo bruto de um arquivo já adicionado (para casos de teste de validação).</summary>
    public CsvZipBuilder ReplaceRaw(string fileName, string content)
    {
        _files[fileName] = content;
        return this;
    }

    public CsvZipBuilder Remove(string fileName)
    {
        _files.Remove(fileName);
        return this;
    }

    /// <summary>Materializa o ZIP em memória. Caller deve dispor o stream.</summary>
    public MemoryStream Build()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in _files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private CsvZipBuilder Add(string name, string content)
    {
        _files[name] = content;
        return this;
    }

    private static string Build<T>(IEnumerable<T> rows, string[] headers, Func<T, object?[]> selector)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers));
        sb.Append('\n');
        foreach (var row in rows)
        {
            var values = selector(row);
            sb.Append(string.Join(",", values.Select(FormatValue)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string FormatValue(object? v) => v switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        string s when s.Contains(',') || s.Contains('"') || s.Contains('\n') => "\"" + s.Replace("\"", "\"\"") + "\"",
        _ => v.ToString() ?? "",
    };
}
