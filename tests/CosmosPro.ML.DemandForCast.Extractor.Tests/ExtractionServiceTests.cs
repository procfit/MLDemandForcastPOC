using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Cobre <see cref="ExtractionService.SkusSemCadastro"/>, o cálculo (agora puro)
/// por trás de <c>CopyProdutosGarantindoUniao</c> — a diferença entre os SKUs que
/// a sugestão referencia e os que já saíram em produtos.csv. É a peça mais
/// sujeita a erro desta fase: fabricar (ou deixar de fabricar) uma linha de
/// placeholder na hora errada quebra a FK composta (RedeId, Sku) no import.
/// </summary>
public sealed class ExtractionServiceTests
{
    [Fact]
    public void Sku_da_sugestao_ausente_do_cadastro_e_reportado()
    {
        var faltantes = ExtractionService.SkusSemCadastro(["100", "200", "300"], ["100", "300"]);

        faltantes.Should().Equal("200");
    }

    [Fact]
    public void Sku_presente_no_cadastro_nao_e_reportado()
    {
        var faltantes = ExtractionService.SkusSemCadastro(["100"], ["100"]);

        faltantes.Should().BeEmpty();
    }

    [Fact]
    public void Sem_sugestao_ou_sem_cadastro_nao_gera_faltantes_falsos()
    {
        ExtractionService.SkusSemCadastro([], []).Should().BeEmpty();
        ExtractionService.SkusSemCadastro([], ["100"]).Should().BeEmpty();
    }

    [Fact]
    public void Skus_repetidos_na_sugestao_aparecem_uma_unica_vez()
    {
        var faltantes = ExtractionService.SkusSemCadastro(["200", "200", "100"], ["100"]);

        faltantes.Should().Equal("200");
    }

    [Fact]
    public void Resultado_sai_ordenado()
    {
        var faltantes = ExtractionService.SkusSemCadastro(["300", "100", "200"], []);

        faltantes.Should().Equal("100", "200", "300");
    }

    [Fact]
    public void Comparacao_e_ordinal_zeros_a_esquerda_sao_skus_distintos()
    {
        // produtos.sql e a query de escopo da sugestão convertem PRODUTO com o
        // mesmo CONVERT(varchar(30), ...), então na prática os dois textos já
        // saem iguais para o mesmo produto. Mas o motivo de exigir comparação
        // ORDINAL (sem normalizar zero à esquerda) não é essa coincidência — é a
        // FK em si: o Worker compara a string literal de sugestoes_compra_itens.csv
        // contra a string literal de produtos.csv. Se "0123" fosse tratado como
        // igual a "123" e o placeholder para "0123" fosse pulado por já ter visto
        // "123", a FK quebraria no import porque o SQL Server não considera essas
        // strings iguais. Portanto "0123" tem de aparecer como faltante mesmo
        // com "123" já cadastrado.
        var faltantes = ExtractionService.SkusSemCadastro(["0123"], ["123"]);

        faltantes.Should().Equal("0123");
    }
}
