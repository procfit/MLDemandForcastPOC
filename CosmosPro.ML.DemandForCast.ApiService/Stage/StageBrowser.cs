using System.Data;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.ApiService.Stage;

/// <summary>
/// Leitura paginada (server-side) das tabelas do banco Stage. Whitelist de
/// tabelas/colunas é a defesa contra SQL injection — nome de tabela e coluna de
/// ordenação NUNCA vêm crus do request; só passam se baterem com a whitelist.
/// </summary>
public sealed class StageBrowser(SqlConnection connection)
{
    public sealed record TableInfo(string Alias, string TableName, string Label, string Icon, string[] Columns, string DefaultOrderBy);

    public sealed record PageResult(long Total, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows);

    // Alias (usado na URL/UI) → metadados. Colunas espelham o schema do Stage
    // (ver CosmosPro.ML.DemandForCast.Worker.TableSchemas).
    public static readonly IReadOnlyDictionary<string, TableInfo> Tables =
        new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["lojas"] = new("lojas", "Lojas", "Lojas", "store",
                ["LojaId", "Nome", "UF", "Cidade", "Regiao", "Perfil", "DiasOperacaoSemana", "DataAbertura", "Ativo"], "LojaId"),
            ["produtos"] = new("produtos", "Produtos", "Produtos", "inventory_2",
                ["Sku", "Nome", "Categoria", "Subcategoria", "Fabricante", "PrincipioAtivo", "Apresentacao", "Ean", "RegistroAnvisa", "ListaControle", "ClasseTerapeutica", "Ativo"], "Sku"),
            ["vendas"] = new("vendas", "Vendas", "Vendas", "point_of_sale",
                ["Data", "LojaId", "Sku", "Quantidade", "PrecoUnitario", "ValorTotal"], "Data DESC"),
            ["estoques"] = new("estoques", "EstoquesDiarios", "Estoques diários", "warehouse",
                ["Data", "LojaId", "Sku", "QuantidadeEmEstoque"], "Data DESC"),
            ["compras"] = new("compras", "Compras", "Compras", "shopping_cart",
                ["DataPedido", "DataRecebimento", "LojaId", "Sku", "Quantidade", "Fornecedor"], "DataPedido DESC"),
            ["promocoes"] = new("promocoes", "Promocoes", "Promoções", "sell",
                ["DataInicio", "DataFim", "Sku", "LojaId", "Tipo", "DescontoPct"], "DataInicio DESC"),
            ["iqvia"] = new("iqvia", "MercadoIqvia", "Mercado IQVIA", "public",
                ["Mes", "PrincipioAtivo", "UF", "DemandaMercadoUnidades", "MarketShareCategoria"], "Mes DESC"),
            ["sinais"] = new("sinais", "SinaisExternos", "Sinais externos", "thermostat",
                ["Data", "Geografia", "Tipo", "Valor"], "Data DESC"),
        };

    public static bool TryGetTable(string alias, out TableInfo info) => Tables.TryGetValue(alias, out info!);

    /// <remarks>
    /// <paramref name="redeId"/> filtra toda leitura. RedeId não está na whitelist de
    /// colunas exibidas de propósito — é escopo, não conteúdo, e mostrá-lo em toda
    /// grid só polui.
    /// </remarks>
    public async Task<PageResult> QueryAsync(
        TableInfo table, int redeId, int skip, int take, string? orderBy, bool descending, CancellationToken ct)
    {
        // Sanitiza o ORDER BY contra a whitelist de colunas. Fallback no default.
        var orderClause = table.DefaultOrderBy;
        if (!string.IsNullOrWhiteSpace(orderBy) &&
            table.Columns.Contains(orderBy, StringComparer.OrdinalIgnoreCase))
        {
            var realCol = table.Columns.First(c => c.Equals(orderBy, StringComparison.OrdinalIgnoreCase));
            orderClause = $"[{realCol}] {(descending ? "DESC" : "ASC")}";
        }

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        long total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.[{table.TableName}] WHERE RedeId = @redeId";
            countCmd.Parameters.Add(new SqlParameter("@redeId", SqlDbType.Int) { Value = redeId });
            total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));
        }

        var colList = string.Join(", ", table.Columns.Select(c => $"[{c}]"));
        var rows = new List<Dictionary<string, object?>>(take);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT {colList}
                FROM dbo.[{table.TableName}]
                WHERE RedeId = @redeId
                ORDER BY {orderClause}
                OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
            cmd.Parameters.Add(new SqlParameter("@redeId", SqlDbType.Int) { Value = redeId });
            cmd.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = skip });
            cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(table.Columns.Length);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[reader.GetName(i)] = value;
                }
                rows.Add(row);
            }
        }

        return new PageResult(total, table.Columns, rows);
    }
}
