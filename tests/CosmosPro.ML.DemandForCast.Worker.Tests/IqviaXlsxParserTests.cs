using CosmosPro.ML.DemandForCast.Tests.Shared.Xlsx;
using CosmosPro.ML.DemandForCast.Worker.Mercado;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

public sealed class IqviaXlsxParserTests
{
    private const string Brick526 = "526-RJ VOLTA REDONDA CENTRO";
    private const string Brick528 = "528-RJ VOLTA REDONDA RETIRO";
    private const string Propria = "DROGARIA RETIRO";
    private const string Concorrentes = "CONCORRENTES";

    private static IqviaXlsxBuilder BuilderPadrao() => new IqviaXlsxBuilder()
        .WithColunas(
            "Ean", "Produto Desc Longa", "Laboratorio", "Molecula",
            "Areas da Farmacia", "Nec 1", "Forma 3", "Classe 4",
            IqviaXlsxBuilder.Medida(Brick526, Concorrentes, "202506", "Unidades"),
            IqviaXlsxBuilder.Medida(Brick526, Concorrentes, "202506", "Real CPP"),
            IqviaXlsxBuilder.Medida(Brick526, Propria, "202606", "Unidades"),
            IqviaXlsxBuilder.Medida(Brick526, Propria, "202606", "Real CPP"));

    [Fact]
    public void Arquivo_valido_produz_observacoes_em_forma_longa()
    {
        using var xlsx = BuilderPadrao()
            .AddLinha("7891721201806", "GLIFAGE XR 500MG X30", "MERCK", "METFORMINA",
                "PRESCRICAO", "98 - NOT OTC", "BAA", "A10J1",
                13221, 113700.59999999999, 905, 8172.1500000000005)
            .Build();

        var resultado = IqviaXlsxParser.Parse(xlsx);

        resultado.Observacoes.Should().HaveCount(2);

        var junho2025 = resultado.Observacoes.Single(o => o.Mes == new DateOnly(2025, 6, 1));
        junho2025.Brick.Should().Be(Brick526);
        junho2025.Bandeira.Should().Be(Concorrentes);
        junho2025.Ean.Should().Be("7891721201806");
        junho2025.Unidades.Should().Be(13221m);
        // Artefato de float do XLSX arredondado para as 2 casas do dinheiro.
        junho2025.ValorCpp.Should().Be(113700.60m);

        var junho2026 = resultado.Observacoes.Single(o => o.Mes == new DateOnly(2026, 6, 1));
        junho2026.Bandeira.Should().Be(Propria);
        junho2026.Unidades.Should().Be(905m);

        var produto = resultado.Produtos.Should().ContainSingle().Subject;
        produto.Ean.Should().Be("7891721201806");
        produto.DescricaoLonga.Should().Be("GLIFAGE XR 500MG X30");
        produto.Classe4.Should().Be("A10J1");

        resultado.Resumo.Meses.Should().Equal(new DateOnly(2025, 6, 1), new DateOnly(2026, 6, 1));
        resultado.Resumo.Bricks.Should().Equal(Brick526);
        resultado.Resumo.Bandeiras.Should().BeEquivalentTo([Concorrentes, Propria]);
        resultado.Resumo.LinhasDoArquivo.Should().Be(1);
    }

    [Fact]
    public void Celula_com_medidas_zeradas_nao_vira_observacao()
    {
        using var xlsx = BuilderPadrao()
            .AddLinha("7891721201806", "PRODUTO A", null, null, null, null, null, null,
                0, 0, 10, 85.5)
            .Build();

        var resultado = IqviaXlsxParser.Parse(xlsx);

        resultado.Observacoes.Should().ContainSingle()
            .Which.Mes.Should().Be(new DateOnly(2026, 6, 1));
        resultado.Resumo.CelulasZeradas.Should().Be(1);
    }

    [Fact]
    public void Linha_sem_ean_e_descartada_e_contada()
    {
        using var xlsx = BuilderPadrao()
            .AddLinha(null, "SEM CODIGO", null, null, null, null, null, null, 5, 10.0, 0, 0)
            .AddLinha("7891721201806", "COM CODIGO", null, null, null, null, null, null, 3, 6.0, 0, 0)
            .Build();

        var resultado = IqviaXlsxParser.Parse(xlsx);

        resultado.Observacoes.Should().ContainSingle().Which.Ean.Should().Be("7891721201806");
        resultado.Produtos.Should().ContainSingle();
        resultado.Resumo.LinhasDoArquivo.Should().Be(2);
        resultado.Resumo.LinhasSemEan.Should().Be(1);
    }

    [Fact]
    public void Ean_duplicado_soma_no_mesmo_recorte_em_vez_de_duplicar_chave()
    {
        // O arquivo real tem EANs repetidos (52.837 linhas, 52.804 distintos); sem a
        // soma o PK composto de MercadoObservacoes estouraria no bulk insert.
        using var xlsx = BuilderPadrao()
            .AddLinha("7891721201806", "PRODUTO A", null, null, null, null, null, null, 10, 100.0, 0, 0)
            .AddLinha("7891721201806", "PRODUTO A", null, null, null, null, null, null, 5, 50.0, 0, 0)
            .Build();

        var resultado = IqviaXlsxParser.Parse(xlsx);

        var obs = resultado.Observacoes.Should().ContainSingle().Subject;
        obs.Unidades.Should().Be(15m);
        obs.ValorCpp.Should().Be(150m);
        resultado.Produtos.Should().ContainSingle();
    }

    [Fact]
    public void Ean_com_mascara_vira_so_digitos()
    {
        using var xlsx = BuilderPadrao()
            .AddLinha("7891721-201806 ", "PRODUTO A", null, null, null, null, null, null, 1, 2.0, 0, 0)
            .Build();

        var resultado = IqviaXlsxParser.Parse(xlsx);

        resultado.Observacoes.Should().ContainSingle().Which.Ean.Should().Be("7891721201806");
    }

    [Fact]
    public void Cabecalho_de_medida_fora_do_contrato_e_falha_com_o_texto_do_cabecalho()
    {
        // Erro de layout tem de apontar a coluna ofensora — ignorá-la perderia um
        // recorte inteiro em silêncio.
        using var xlsx = new IqviaXlsxBuilder()
            .WithColunas("Ean", "Produto Desc Longa", "Brick rpe X Bandeira Y Mes 202506 Unidaes")
            .AddLinha("789", "P", 1)
            .Build();

        var act = () => IqviaXlsxParser.Parse(xlsx);

        act.Should().Throw<FormatException>()
            .WithMessage("*Brick rpe X Bandeira Y Mes 202506 Unidaes*");
    }

    [Fact]
    public void Mes_invalido_no_cabecalho_e_falha()
    {
        using var xlsx = new IqviaXlsxBuilder()
            .WithColunas("Ean", "Produto Desc Longa",
                IqviaXlsxBuilder.Medida("B", "C", "202513", "Unidades"))
            .AddLinha("789", "P", 1)
            .Build();

        var act = () => IqviaXlsxParser.Parse(xlsx);

        act.Should().Throw<FormatException>().WithMessage("*202513*");
    }

    [Fact]
    public void Sem_aba_QUERY_e_falha_listando_as_abas_encontradas()
    {
        using var xlsx = new IqviaXlsxBuilder(abaDados: "Planilha1")
            .WithColunas("Ean", "Produto Desc Longa")
            .Build();

        var act = () => IqviaXlsxParser.Parse(xlsx);

        act.Should().Throw<FormatException>().WithMessage("*QUERY*Planilha1*");
    }

    [Fact]
    public void Sem_coluna_de_medida_e_falha()
    {
        using var xlsx = new IqviaXlsxBuilder()
            .WithColunas("Ean", "Produto Desc Longa")
            .AddLinha("789", "P")
            .Build();

        var act = () => IqviaXlsxParser.Parse(xlsx);

        act.Should().Throw<FormatException>().WithMessage("*medida*");
    }

    [Fact]
    public void Pdvs_normalizam_brick_e_ignoram_secao_de_pivot()
    {
        using var xlsx = BuilderPadrao()
            .AddLinha("789", "P", null, null, null, null, null, null, 1, 2.0, 0, 0)
            // A aba real usa '_' no brick; a QUERY usa espaço. E termina com uma
            // seção de pivot que não é linha de PDV.
            .AddPdv("526-RJ_VOLTA REDONDA_CENTRO", "CONCORRENTES - 00000000000000")
            .AddPdv("526-RJ_VOLTA REDONDA_CENTRO", "DROGARIA RETIRO - 07381852000204")
            .AddPdv("Rótulos de Linha", "Soma de Bandeira CONCORRENTES PDV")
            .Build();

        var resultado = IqviaXlsxParser.Parse(xlsx);

        resultado.Pdvs.Should().HaveCount(2);
        resultado.Pdvs.Should().OnlyContain(p => p.Brick == "526-RJ VOLTA REDONDA CENTRO");
        resultado.Pdvs.Should().ContainSingle(p => p.Cnpj == "07381852000204" && p.Bandeira == "DROGARIA RETIRO");
        resultado.Pdvs.Should().ContainSingle(p => p.Cnpj == "00000000000000" && p.Bandeira == "CONCORRENTES");
    }

    [Fact]
    public void Sem_aba_de_pdv_o_dado_principal_continua_utilizavel()
    {
        using var xlsx = new IqviaXlsxBuilder(abaPdv: null)
            .WithColunas("Ean", "Produto Desc Longa",
                IqviaXlsxBuilder.Medida(Brick526, Concorrentes, "202506", "Unidades"),
                IqviaXlsxBuilder.Medida(Brick526, Concorrentes, "202506", "Real CPP"))
            .AddLinha("789", "P", 1, 2.0)
            .Build();

        var resultado = IqviaXlsxParser.Parse(xlsx);

        resultado.Observacoes.Should().ContainSingle();
        resultado.Pdvs.Should().BeEmpty();
    }
}
