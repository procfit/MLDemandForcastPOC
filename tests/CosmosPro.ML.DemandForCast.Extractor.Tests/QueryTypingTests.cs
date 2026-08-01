using System.Text.RegularExpressions;
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// No PBS todo numérico é declarado <c>numeric(p,s)</c> — nunca <c>int</c> ou
/// <c>bigint</c> —, e o driver entrega <c>numeric</c> como <see cref="decimal"/>.
/// Uma coluna sem CONVERT na query derruba a extração inteira no primeiro
/// <c>GetInt32</c>/<c>GetInt64</c> que a consome, e a mensagem do driver não diz
/// qual query nem qual coluna.
/// <para>
/// A regra deste projeto é: <b>toda coluna consumida por leitura tipada declara o
/// tipo na própria query</b>. A alternativa — ler tudo como decimal e converter em
/// C# — espalharia a conversão por cada call site. Este teste é o inventário
/// dessa regra: cada linha abaixo é um par (query, coluna) que alguma leitura
/// tipada consome. Ao acrescentar uma leitura tipada, acrescente a linha.
/// </para>
/// </summary>
public sealed class QueryTypingTests
{
    [Theory]
    // escopo_sugestao.sql -> ExtractionService.LoadEscopoSugestao (GetInt32, GetString)
    [InlineData("escopo_sugestao.sql", "LojaId")]
    [InlineData("escopo_sugestao.sql", "Sku")]
    // lojas_disponiveis.sql -> ExtractionService.LoadLojas (GetInt32, GetString)
    [InlineData("lojas_disponiveis.sql", "LojaId")]
    [InlineData("lojas_disponiveis.sql", "Nome")]
    // catalogo_sugestoes.sql -> LoadCabecalhosDoCatalogo (GetInt64, GetString, GetDateTime, GetByte, GetInt32)
    [InlineData("catalogo_sugestoes.sql", "SugestaoId")]
    [InlineData("catalogo_sugestoes.sql", "Descricao")]
    [InlineData("catalogo_sugestoes.sql", "DataHora")]
    [InlineData("catalogo_sugestoes.sql", "TipoCalculo")]
    [InlineData("catalogo_sugestoes.sql", "DiasCoberturaMax")]
    // sugestao_por_id.sql -> LoadSugestaoPorId, mesmos ordinais do catálogo
    [InlineData("sugestao_por_id.sql", "SugestaoId")]
    [InlineData("sugestao_por_id.sql", "Descricao")]
    [InlineData("sugestao_por_id.sql", "DataHora")]
    [InlineData("sugestao_por_id.sql", "TipoCalculo")]
    [InlineData("sugestao_por_id.sql", "DiasCoberturaMax")]
    // catalogo_sugestoes_contagens.sql -> LoadContagensDoCatalogo (GetInt64, GetInt32, GetInt32)
    [InlineData("catalogo_sugestoes_contagens.sql", "SugestaoId")]
    [InlineData("catalogo_sugestoes_contagens.sql", "QtdLinhas")]
    [InlineData("catalogo_sugestoes_contagens.sql", "QtdLojas")]
    // sugestoes_compra_diagnostico.sql -> AvisarDivergenciaEmpresaFilial (GetInt32)
    [InlineData("sugestoes_compra_diagnostico.sql", "QtdDivergencias")]
    // sugestoes_compra.sql -> CopySugestaoHeader (GetString, GetDateTime, GetByte)
    [InlineData("sugestoes_compra.sql", "Descricao")]
    [InlineData("sugestoes_compra.sql", "DataHora")]
    [InlineData("sugestoes_compra.sql", "TipoCalculo")]
    // produtos.sql -> CopyProdutosGarantindoUniao (GetString no ordinal 0)
    [InlineData("produtos.sql", "Sku")]
    // estoques_movimentos.sql -> ReadMovements (GetInt32, GetString, GetDateTime, GetDecimal)
    [InlineData("estoques_movimentos.sql", "LojaId")]
    [InlineData("estoques_movimentos.sql", "Sku")]
    [InlineData("estoques_movimentos.sql", "Data")]
    [InlineData("estoques_movimentos.sql", "QuantidadeEmEstoque")]
    public void Coluna_lida_com_tipo_declara_o_tipo_na_query(string arquivo, string coluna)
    {
        var sql = SqlResources.Load(arquivo);

        Regex.IsMatch(sql, $@"\b{coluna}\s*=\s*(CONVERT|CAST)\s*\(")
            .Should().BeTrue(
                $"'{coluna}' em {arquivo} é lida de forma tipada e, sem CONVERT/CAST, " +
                "o numeric do PBS chega como System.Decimal e a leitura estoura em execução");
    }

    [Fact]
    public void Escopo_da_sugestao_converte_filial_e_produto()
    {
        // O caso que quebrou contra a instância real: PRODUTO tinha CONVERT e
        // FILIAL não, porque a query era texto inline no C# e ficou de fora da
        // revisão que as queries embarcadas receberam. As duas colunas moraram no
        // mesmo SELECT o tempo todo.
        var sql = SqlResources.Load("escopo_sugestao.sql");

        sql.Should().Contain("CONVERT(int, R.FILIAL)").And.Contain("CONVERT(varchar(30), R.PRODUTO)");
    }
}
