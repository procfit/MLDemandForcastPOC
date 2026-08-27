using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;

namespace CosmosPro.ML.DemandForCast.ApiService.Tests;

public sealed class ImportValidatorTests
{
    private static MemoryStream BuildValidZip()
    {
        var lojas = new LojaFaker(seed: 1).Generate(2);
        var produtos = new ProdutoFaker(seed: 2).Generate(3);
        var lojaIds = lojas.Select(l => l.LojaId).ToList();
        var skus = produtos.Select(p => p.Sku).ToList();
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 5, 1);

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(new VendaFaker(lojaIds, skus, start, end, seed: 3).Generate(5))
            .WithEstoquesDiarios(new EstoqueDiarioFaker(lojaIds, skus, start, end, seed: 4).Generate(5))
            .WithCompras(new CompraFaker(lojaIds, skus, start, end, seed: 5).Generate(2))
            .WithPromocoes(new PromocaoFaker(lojaIds, skus, start, end, seed: 6).Generate(1))
            .Build();
    }

    [Fact]
    public void ZIP_completo_e_valido_passa()
    {
        using var zip = BuildValidZip();
        var result = ImportValidator.Validate(zip);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ZIP_faltando_um_arquivo_obrigatorio_falha_com_mensagem_apontando_o_arquivo()
    {
        using var zip = new CsvZipBuilder()
            .WithLojas(new LojaFaker().Generate(1))
            .WithProdutos(new ProdutoFaker().Generate(1))
            // SEM vendas.csv intencionalmente
            .WithEstoquesDiarios([])
            .WithCompras([])
            .WithPromocoes([])
            .Build();

        var result = ImportValidator.Validate(zip);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("vendas.csv"));
    }

    [Fact]
    public void Header_sem_uma_coluna_obrigatoria_falha_apontando_a_coluna_faltante()
    {
        using var zip = new CsvZipBuilder()
            .WithLojas(new LojaFaker().Generate(1))
            .WithProdutos(new ProdutoFaker().Generate(1))
            .WithVendas([])
            .WithEstoquesDiarios([])
            .WithCompras([])
            .WithPromocoes([])
            // Sobrescreve vendas.csv com header sem ValorTotal
            .ReplaceRaw("vendas.csv", "Data,LojaId,Sku,Quantidade,PrecoUnitario\n")
            .Build();

        var result = ImportValidator.Validate(zip);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("vendas.csv") && e.Contains("ValorTotal"));
    }

    [Fact]
    public void Stream_que_nao_eh_zip_falha_com_mensagem_amigavel()
    {
        using var notAZip = new MemoryStream("isto nao eh um zip"u8.ToArray());
        var result = ImportValidator.Validate(notAZip);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Arquivo não é um ZIP"));
    }
}
