using System.Data;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Worker.Training;

/// <summary>
/// Restringe uma consulta do Stage a um conjunto de SKUs por <b>join com tabela
/// temporária</b>, e não por <c>Sku IN (@s0, …, @sN)</c>.
///
/// <para>
/// <b>Por que existe:</b> o <c>IN</c> parametrizado gastava um parâmetro por SKU e o SQL
/// Server aceita 2100 por comando. Isso não era um teto de modelagem — era um limite de
/// implementação —, mas virou um: o orçamento de SKUs do treino foi fixado em mil para
/// ficar longe do estouro. O join não tem teto: um parâmetro (nenhum) para qualquer número
/// de SKUs. Fechou também um bug latente em <c>StageEstoqueInicialLoader</c>, cuja lista
/// vem dos itens da simulação e estouraria em runtime acima de 2100 SKUs.
/// </para>
///
/// <para>
/// <b>O que a remoção NÃO resolveu, medido:</b> na sugestão 125595 da Retiro só 991 SKUs
/// tinham venda antes do corte, então o teto de mil não excluía SKU nenhum. Tirá-lo deixou
/// a cobertura igual e o viés do modelo quase igual. Serve de aviso para a próxima
/// hipótese: o teto era risco à espera de catálogo maior, não a causa daquele resultado.
/// </para>
///
/// <para>
/// <see cref="Todos"/> não é "conjunto vazio": é <b>ausência de filtro</b> —
/// <see cref="Join"/> devolve string vazia e a consulta varre a rede inteira. Um escopo
/// materializado vazio, por outro lado, casa zero linhas. As duas coisas são opostas, e
/// é por isso que o caso "sem teto" não passa por aqui como lista de todos os SKUs: além
/// de inútil, pagaria bulk copy e join para não excluir nada.
/// </para>
///
/// <para>
/// A tabela vive na sessão da <see cref="SqlConnection"/> em que foi criada, então o
/// escopo só vale para comandos da <b>mesma</b> conexão e morre quando ela fecha.
/// </para>
/// </summary>
internal sealed class EscopoDeSkus
{
    private const string Tabela = "#escopo_skus";

    private readonly bool _materializado;

    private EscopoDeSkus(bool materializado) => _materializado = materializado;

    /// <summary>Sem filtro: a consulta alcança todos os SKUs da rede.</summary>
    public static EscopoDeSkus Todos { get; } = new(materializado: false);

    /// <summary>
    /// Cria e popula a tabela temporária na conexão indicada, que precisa estar aberta.
    /// </summary>
    /// <remarks>
    /// Deduplica com <see cref="StringComparer.OrdinalIgnoreCase"/> — o mesmo comparador
    /// que <c>StageObservationLoader</c> usa em todos os dicionários por SKU. SKU repetido
    /// na tabela <b>duplicaria linha no join</b>, e em <c>Vendas</c> isso dobraria
    /// quantidade em silêncio; por isso a dedupe é obrigação daqui, não do chamador.
    /// O índice é clustered mas não único: a garantia de unicidade é a dedupe acima, e uma
    /// constraint com collation diferente da coluna original falharia em runtime.
    /// </remarks>
    public static async Task<EscopoDeSkus> MaterializarAsync(
        SqlConnection conn, IEnumerable<string> skus, CancellationToken ct)
    {
        await using (var cmd = conn.CreateCommand())
        {
            // COLLATE DATABASE_DEFAULT: sem isso a coluna nasce com a collation do tempdb
            // e o join com dbo.Vendas.Sku pode estourar conflito de collation.
            cmd.CommandText = $@"
                CREATE TABLE {Tabela} (Sku NVARCHAR(30) COLLATE DATABASE_DEFAULT NOT NULL);
                CREATE CLUSTERED INDEX IX_{Tabela[1..]}_Sku ON {Tabela} (Sku);";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var tabela = new DataTable();
        tabela.Columns.Add("Sku", typeof(string));
        foreach (var sku in skus.ToHashSet(StringComparer.OrdinalIgnoreCase))
            tabela.Rows.Add(sku);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = Tabela, BulkCopyTimeout = 300 };
        bulk.ColumnMappings.Add("Sku", "Sku");
        await bulk.WriteToServerAsync(tabela, ct);

        return new EscopoDeSkus(materializado: true);
    }

    /// <summary>
    /// Fragmento a interpolar entre o <c>FROM</c> e o <c>WHERE</c>. Vazio quando o escopo
    /// é <see cref="Todos"/>.
    /// </summary>
    /// <param name="alias">Alias da tabela do Stage que tem a coluna <c>Sku</c>.</param>
    public string Join(string alias) =>
        _materializado ? $"INNER JOIN {Tabela} esc ON esc.Sku = {alias}.Sku" : "";
}
