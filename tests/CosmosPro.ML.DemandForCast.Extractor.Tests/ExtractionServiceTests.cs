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

    [Fact]
    public void Etapa_sem_erro_devolve_o_valor_intacto()
    {
        ExtractionService.Step("lojas.csv (lojas.sql)", () => 42).Should().Be(42);
    }

    [Fact]
    public void Falha_dentro_da_etapa_ganha_o_nome_da_etapa_e_preserva_a_causa()
    {
        // O caso real: a extração morria com "Unable to cast object of type
        // 'System.Decimal' to type 'System.Int32'" e nada mais — nem a query, nem
        // o arquivo de destino. Sem isso, achar a coluna é adivinhação.
        var causa = new InvalidCastException("Unable to cast object of type 'System.Decimal' to type 'System.Int32'.");

        var acao = () => ExtractionService.Step<int>("escopo da sugestão (escopo_sugestao.sql)", () => throw causa);

        acao.Should().Throw<ExtractionStepException>()
            .Where(ex => ex.Etapa == "escopo da sugestão (escopo_sugestao.sql)")
            .Where(ex => ReferenceEquals(ex.InnerException, causa))
            .WithMessage("*escopo_sugestao.sql*")
            .WithMessage("*System.Decimal*");
    }

    [Fact]
    public void Etapa_aninhada_nao_embrulha_duas_vezes()
    {
        // A etapa mais interna é a que sabe onde quebrou; embrulhar de novo a cada
        // nível deixaria a mensagem com uma trilha de prefixos e a causa no fim.
        var acao = () => ExtractionService.Step<int>("externa", () =>
            ExtractionService.Step<int>("interna", () => throw new InvalidOperationException("raiz")));

        acao.Should().Throw<ExtractionStepException>().Where(ex => ex.Etapa == "interna");
    }

    [Fact]
    public void Cancelamento_atravessa_a_etapa_sem_embrulho()
    {
        // O modo linha de comando separa cancelamento de falha pelo código de
        // saída, e essa separação é feita pelo tipo da exceção.
        var acao = () => ExtractionService.Step<int>("vendas.csv (vendas.sql)", () => throw new OperationCanceledException());

        acao.Should().Throw<OperationCanceledException>();
    }
}
