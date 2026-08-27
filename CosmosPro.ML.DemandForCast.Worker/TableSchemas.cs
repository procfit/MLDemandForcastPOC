using System.Data;

namespace CosmosPro.ML.DemandForCast.Worker;

/// <summary>
/// Schema explícito de cada tabela Stage. Necessário porque o `SqlBulkCopy`
/// não converte strings para tipos numéricos/bit/data automaticamente — temos
/// que materializar um `DataTable` com colunas já tipadas.
/// </summary>
internal static class TableSchemas
{
    /// <param name="ServerSupplied">
    /// Coluna que o Worker preenche, não o CSV. Nunca é procurada no header e
    /// nunca passa por <see cref="Parse"/>.
    /// </param>
    internal record Column(string Name, Type Type, bool Nullable, bool ServerSupplied = false);

    /// <summary>
    /// RedeId nunca vem do CSV: o Worker injeta a partir da CargaStage. Isso mantém
    /// o contrato CSV intacto e impede que um cliente reivindique a rede de outro
    /// escrevendo um id no arquivo.
    /// </summary>
    private static readonly Column RedeId = new("RedeId", typeof(int), false, ServerSupplied: true);

    public static readonly IReadOnlyDictionary<string, Column[]> ByTable = new Dictionary<string, Column[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Lojas"] =
        [
            RedeId,
            new("LojaId", typeof(int), false),
            new("Nome", typeof(string), false),
            new("UF", typeof(string), false),
            new("Cidade", typeof(string), false),
            new("Regiao", typeof(string), true),
            new("Perfil", typeof(string), true),
            new("DiasOperacaoSemana", typeof(byte), false),
            new("DataAbertura", typeof(DateTime), true),
            new("Ativo", typeof(bool), false),
            // Anulável e no fim do schema: ZIPs anteriores à F16 não trazem a coluna,
            // e coluna ausente do header vira NULL no BulkInsert.
            new("Cnpj", typeof(string), true),
        ],
        ["Produtos"] =
        [
            RedeId,
            new("Sku", typeof(string), false),
            new("Nome", typeof(string), false),
            new("Categoria", typeof(string), true),
            new("Subcategoria", typeof(string), true),
            new("Fabricante", typeof(string), true),
            new("PrincipioAtivo", typeof(string), true),
            new("Apresentacao", typeof(string), true),
            new("Ean", typeof(string), true),
            new("RegistroAnvisa", typeof(string), true),
            new("ListaControle", typeof(string), true),
            new("ClasseTerapeutica", typeof(string), true),
            new("Ativo", typeof(bool), false),
        ],
        ["Vendas"] =
        [
            RedeId,
            new("Data", typeof(DateTime), false),
            new("LojaId", typeof(int), false),
            new("Sku", typeof(string), false),
            new("Quantidade", typeof(decimal), false),
            new("PrecoUnitario", typeof(decimal), false),
            new("ValorTotal", typeof(decimal), false),
        ],
        ["EstoquesDiarios"] =
        [
            RedeId,
            new("Data", typeof(DateTime), false),
            new("LojaId", typeof(int), false),
            new("Sku", typeof(string), false),
            new("QuantidadeEmEstoque", typeof(decimal), false),
        ],
        ["Compras"] =
        [
            RedeId,
            new("DataPedido", typeof(DateTime), false),
            new("DataRecebimento", typeof(DateTime), true),
            new("LojaId", typeof(int), false),
            new("Sku", typeof(string), false),
            new("Quantidade", typeof(decimal), false),
            new("Fornecedor", typeof(string), true),
        ],
        ["Promocoes"] =
        [
            RedeId,
            new("DataInicio", typeof(DateTime), false),
            new("DataFim", typeof(DateTime), false),
            new("Sku", typeof(string), false),
            new("LojaId", typeof(int), true),
            new("Tipo", typeof(string), true),
            new("DescontoPct", typeof(decimal), true),
        ],
        ["SinaisExternos"] =
        [
            RedeId,
            new("Data", typeof(DateTime), false),
            new("Geografia", typeof(string), false),
            new("Tipo", typeof(string), false),
            new("Valor", typeof(decimal), false),
        ],
        ["SugestoesCompra"] =
        [
            RedeId,
            new("SugestaoId", typeof(long), false),
            new("Descricao", typeof(string), true),
            new("DataHora", typeof(DateTime), false),
            new("TipoCalculo", typeof(byte), false),
            new("LeadTimeDias", typeof(short), true),
            new("DiasCurvaA", typeof(short), false),
            new("DiasCurvaB", typeof(short), false),
            new("DiasCurvaC", typeof(short), false),
            new("DiasCurvaD", typeof(short), false),
            new("DiasCurvaE", typeof(short), false),
            new("Efetividade", typeof(decimal), false),
            new("ConsideraPedidosPendentes", typeof(bool), false),
            new("IncluiEstoqueZerado", typeof(bool), false),
        ],
        ["SugestoesCompraItens"] =
        [
            RedeId,
            new("SugestaoId", typeof(long), false),
            new("LojaId", typeof(int), false),
            new("Sku", typeof(string), false),
            new("Curva", typeof(string), true),
            new("DemandaDia", typeof(decimal), false),
            new("DemandaDiaPonderada", typeof(decimal), true),
            new("EstoqueSaldo", typeof(decimal), false),
            // Nullable porque TipoCalculo=2 ("Dias de Reposição") não usa eSeg/eMax.
            new("EstoqueSeguranca", typeof(decimal), true),
            new("EstoqueMaximo", typeof(decimal), true),
            new("EstoqueMinimo", typeof(decimal), true),
            new("DiasEstoque", typeof(short), false),
            new("PedidosPendentes", typeof(decimal), false),
            new("CompraSugerida", typeof(decimal), false),
            new("CompraAutorizada", typeof(decimal), false),
            new("PrecoCompra", typeof(decimal), true),
            new("FatorEmbalagem", typeof(decimal), true),
            new("Falteiro", typeof(bool), false),
        ],
    };

    public static DataTable BuildEmpty(string table)
    {
        var dt = new DataTable(table);
        foreach (var col in ByTable[table])
        {
            var dc = new DataColumn(col.Name, col.Type) { AllowDBNull = col.Nullable };
            dt.Columns.Add(dc);
        }
        return dt;
    }

    public static object Parse(Column col, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return col.Nullable
                ? DBNull.Value
                : throw new FormatException($"Coluna '{col.Name}' obrigatória recebeu valor vazio.");
        }

        var s = raw.Trim().Trim('"');

        // 1/0 mapeia para bit; aceita também true/false.
        if (col.Type == typeof(bool))
        {
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            throw new FormatException($"Coluna '{col.Name}' bit recebeu valor inválido '{s}'.");
        }

        if (col.Type == typeof(int)) return int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (col.Type == typeof(long)) return long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (col.Type == typeof(short)) return short.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (col.Type == typeof(byte)) return byte.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (col.Type == typeof(decimal)) return decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (col.Type == typeof(double)) return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (col.Type == typeof(DateTime)) return DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (col.Type == typeof(string)) return s;

        throw new NotSupportedException($"Tipo {col.Type} não suportado em TableSchemas.Parse.");
    }
}
