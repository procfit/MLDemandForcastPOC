using System.Net;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

[Collection(AspireCollection.Name)]
public sealed class ImportsIntegrationTests(AppHostFixture fixture)
{
    [Fact]
    public async Task Upload_de_ZIP_valido_retorna_202_e_aparece_na_listagem()
    {
        // Arrange — ZIP realista via fakers
        var lojas = new LojaFaker(seed: 100).Generate(3);
        var produtos = new ProdutoFaker(seed: 101).Generate(5);
        var lojaIds = lojas.Select(l => l.LojaId).ToList();
        var skus = produtos.Select(p => p.Sku).ToList();
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 5, 1);

        using var zip = new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(new VendaFaker(lojaIds, skus, start, end, seed: 102).Generate(10))
            .WithEstoquesDiarios(new EstoqueDiarioFaker(lojaIds, skus, start, end, seed: 103).Generate(10))
            .WithCompras(new CompraFaker(lojaIds, skus, start, end, seed: 104).Generate(3))
            .WithPromocoes(new PromocaoFaker(lojaIds, skus, start, end, seed: 105).Generate(2))
            .Build();

        // Act
        var streamPart = new StreamPart(zip, "test.zip", "application/zip");
        var uploadResp = await fixture.ImportsApi.UploadAsync(streamPart, AppHostFixture.RedeDemoId);

        // Assert — upload
        uploadResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        uploadResp.Content.Should().NotBeNull();
        uploadResp.Content!.Status.Should().Be("Pendente");
        uploadResp.Content.Id.Should().NotBeEmpty();

        // Assert — visível em GET por id
        var byId = await fixture.ImportsApi.GetAsync(uploadResp.Content.Id);
        byId.IsSuccessStatusCode.Should().BeTrue();
        byId.Content!.Id.Should().Be(uploadResp.Content.Id);
        byId.Content.NomeArquivoOriginal.Should().Be("test.zip");

        // Assert — visível em GET lista (entre as 50 mais recentes)
        var list = await fixture.ImportsApi.ListAsync(take: 50);
        list.IsSuccessStatusCode.Should().BeTrue();
        list.Content.Should().Contain(c => c.Id == uploadResp.Content.Id);
    }

    [Fact]
    public async Task Upload_de_ZIP_sem_arquivo_obrigatorio_retorna_400_com_mensagem()
    {
        // Arrange — ZIP propositalmente incompleto (sem mercado_iqvia.csv)
        using var zip = new CsvZipBuilder()
            .WithLojas(new LojaFaker().Generate(1))
            .WithProdutos(new ProdutoFaker().Generate(1))
            .WithVendas([])
            .WithEstoquesDiarios([])
            .WithCompras([])
            .WithPromocoes([])
            // mercado_iqvia.csv ausente intencionalmente
            .Build();

        // Act
        var streamPart = new StreamPart(zip, "incompleto.zip", "application/zip");
        var resp = await fixture.ImportsApi.UploadAsync(streamPart, AppHostFixture.RedeDemoId);

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GET_para_id_inexistente_retorna_404()
    {
        var resp = await fixture.ImportsApi.GetAsync(Guid.NewGuid());
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
