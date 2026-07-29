using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Extractor;
using CosmosPro.ML.DemandForCast.Worker;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O extrator roda isolado na máquina do cliente e por isso duplica o contrato
/// de colunas do Stage em vez de referenciar o Worker. Estes testes são o que
/// impede as duas definições de divergirem em silêncio.
/// </summary>
public sealed class StageContractTests
{
    private static readonly (string File, string Table)[] Mapeamento =
    [
        (StageContract.Lojas, "Lojas"),
        (StageContract.Produtos, "Produtos"),
        (StageContract.Vendas, "Vendas"),
        (StageContract.EstoquesDiarios, "EstoquesDiarios"),
        (StageContract.Compras, "Compras"),
        (StageContract.Promocoes, "Promocoes"),
        (StageContract.MercadoIqvia, "MercadoIqvia"),
        (StageContract.SugestoesCompra, "SugestoesCompra"),
        (StageContract.SugestoesCompraItens, "SugestoesCompraItens"),
    ];

    [Fact]
    public void Headers_batem_com_o_schema_do_Worker_em_nome_e_ordem()
    {
        foreach (var (file, table) in Mapeamento)
        {
            // Colunas ServerSupplied (RedeId) são injetadas pelo Worker a partir da
            // CargaStage e não existem no CSV — o extrator não deve produzi-las.
            var esperado = TableSchemas.ByTable[table]
                .Where(c => !c.ServerSupplied)
                .Select(c => c.Name)
                .ToArray();

            StageContract.Headers[file].Should().Equal(
                esperado,
                $"o CSV '{file}' é lido pelo Worker na ordem declarada em TableSchemas");
        }
    }

    [Fact]
    public void Todo_arquivo_exigido_pelo_import_e_produzido_pelo_extrator()
    {
        foreach (var arquivo in ImportSchemas.ExpectedFiles.Keys)
        {
            StageContract.Headers.Should().ContainKey(arquivo);
            StageContract.WriteOrder.Should().Contain(arquivo);
        }
    }

    [Fact]
    public void Colunas_obrigatorias_do_validador_estao_no_header()
    {
        foreach (var (arquivo, obrigatorias) in ImportSchemas.ExpectedFiles)
        {
            StageContract.Headers[arquivo].Should().Contain(
                obrigatorias,
                $"o validador do import rejeita '{arquivo}' sem essas colunas");
        }
    }

    [Fact]
    public void Ordem_de_escrita_cobre_exatamente_os_arquivos_do_contrato()
    {
        StageContract.WriteOrder.Should().BeEquivalentTo(StageContract.Headers.Keys);
        StageContract.WriteOrder.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("lojas.sql")]
    [InlineData("produtos.sql")]
    [InlineData("vendas.sql")]
    [InlineData("estoques_movimentos.sql")]
    [InlineData("compras.sql")]
    [InlineData("promocoes.sql")]
    public void Query_embarcada_existe_no_assembly(string arquivo)
    {
        var sql = SqlResources.Load(arquivo);

        sql.Should().NotBeNullOrWhiteSpace();
    }
}
