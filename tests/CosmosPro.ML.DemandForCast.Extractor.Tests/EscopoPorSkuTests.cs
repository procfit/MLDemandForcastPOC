using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Inventário do escopo por SKU. Cada consulta abaixo tem de filtrar pelos produtos da
/// sugestão — sem isso a extração leva o histórico de **todos** os produtos das lojas.
/// <para>
/// Medido numa sugestão real de produção (id 9589, 1.695 SKUs distintos, 93 lojas): sem o
/// filtro o ZIP saiu com 242 MB, 16,8 milhões de linhas de venda, 52,9 milhões de estoque
/// diário e 79.749 produtos, em 5 minutos de extração. Outra sugestão, de 174 SKUs, produziu
/// 879 MB e 309 milhões de linhas de estoque. O import tem teto de 500 MB, então a ausência
/// do filtro não é ineficiência: é o fluxo não completar.
/// </para>
/// <para>
/// Este teste existe porque o filtro é uma linha fácil de perder num refactor de query, e a
/// perda não quebra nada — só volta a produzir um ZIP que não sobe, minutos depois, contra o
/// ERP do cliente.
/// </para>
/// </summary>
public sealed class EscopoPorSkuTests
{
    [Theory]
    [InlineData("vendas.sql")]
    [InlineData("estoques_movimentos.sql")]
    [InlineData("compras.sql")]
    [InlineData("promocoes.sql")]
    [InlineData("produtos.sql")]
    public void Consulta_escopada_filtra_pelos_skus_da_sugestao(string arquivo)
    {
        var sql = SqlResources.Load(arquivo);

        sql.Should().Contain("@skus",
            $"{arquivo} tem de ser escopada pelos SKUs da sugestão — sem o filtro ela traz o " +
            "histórico de todos os produtos das lojas, e o ZIP passa do teto de upload");

        sql.Should().Contain("STRING_SPLIT",
            $"{arquivo} recebe os SKUs num parâmetro único, lido por STRING_SPLIT: um parâmetro " +
            "por SKU estouraria o teto de 2.100 do SQL Server numa sugestão grande");
    }

    /// <summary>
    /// A que **não** é escopada, e de propósito: lojas vêm do escopo da sugestão pela lista de
    /// ids, não por SKU. Pinar isto evita alguém "uniformizar" as consultas e passar um
    /// parâmetro que a query não usa.
    /// </summary>
    [Fact]
    public void Consulta_de_lojas_nao_recebe_skus()
        => SqlResources.Load("lojas.sql").Should().NotContain("@skus");
}
