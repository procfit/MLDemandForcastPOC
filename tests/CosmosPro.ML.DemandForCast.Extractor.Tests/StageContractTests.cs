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
    [InlineData("catalogo_eans.sql")]
    public void Query_embarcada_existe_no_assembly(string arquivo)
    {
        var sql = SqlResources.Load(arquivo);

        sql.Should().NotBeNullOrWhiteSpace();
    }
    /// <summary>
    /// <c>catalogo_eans.csv</c> é o primeiro CSV do ZIP que <b>não</b> tem tabela
    /// correspondente no Stage: ele vai para <c>engine.RedeCatalogoEans</c>, porque a tela de
    /// oportunidades não pertence a sessão nenhuma e o Stage é apagado a cada import.
    /// </summary>
    [Fact]
    public void O_catalogo_de_eans_esta_no_contrato_com_as_tres_colunas()
    {
        StageContract.Headers.Should().ContainKey(StageContract.CatalogoEans);
        StageContract.Headers[StageContract.CatalogoEans].Should().Equal(["Sku", "Ean", "Nome"]);
        StageContract.WriteOrder.Should().Contain(StageContract.CatalogoEans);
    }

    /// <summary>
    /// <b>A ausência dele no mapeamento Stage é deliberada, e este teste existe para ela não
    /// ser "corrigida".</b> <see cref="Mapeamento"/> pareia CSV com tabela do Stage; incluir o
    /// catálogo ali faria o teste de header procurar <c>TableSchemas.ByTable["CatalogoEans"]</c>,
    /// que não existe — e a correção óbvia (criar a tabela no Stage) é exatamente o defeito
    /// que a F16 corrigiu ao remover <c>MercadoIqvia</c>: tabela de Stage que ninguém lê,
    /// apagada a cada import.
    /// </summary>
    [Fact]
    public void O_catalogo_de_eans_nao_tem_tabela_no_Stage()
    {
        Mapeamento.Select(m => m.File).Should().NotContain(StageContract.CatalogoEans);
        TableSchemas.ByTable.Keys.Should().NotContain("RedeCatalogoEans",
            "o catálogo vive no banco engine, gravado por EF Core, não pelo bulk do Stage");
    }

    /// <summary>
    /// Ordem importa para o log da extração fazer sentido: o comprador vê "produtos da
    /// sugestão: 43" e depois "catálogo de códigos: 29.068", e a diferença entre os dois
    /// números é exatamente o motivo pelo qual este arquivo existe.
    /// </summary>
    [Fact]
    public void O_catalogo_e_escrito_depois_dos_produtos_da_sugestao()
    {
        var ordem = StageContract.WriteOrder.ToList();

        ordem.IndexOf(StageContract.CatalogoEans)
             .Should().BeGreaterThan(ordem.IndexOf(StageContract.Produtos));
    }

}
