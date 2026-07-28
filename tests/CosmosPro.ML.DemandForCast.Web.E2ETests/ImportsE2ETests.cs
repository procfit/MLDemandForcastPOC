using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

[Collection(AspireCollection.Name)]
public sealed class ImportsE2ETests(AppHostFixture fixture)
{
    [Fact]
    public async Task Upload_pelo_botao_Importar_mostra_notificacao_e_aparece_no_datagrid()
    {
        // Arrange — ZIP em arquivo temporário com filename único (identificador
        // da linha no datagrid). UUIDv7 GUIDs próximos compartilham 8 chars iniciais,
        // então não usamos GUID prefix como seletor.
        var lojas = new LojaFaker(seed: 200).Generate(2);
        var produtos = new ProdutoFaker(seed: 201).Generate(3);
        var lojaIds = lojas.Select(l => l.LojaId).ToList();
        var skus = produtos.Select(p => p.Sku).ToList();
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 3, 1);

        using var zipStream = new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(new VendaFaker(lojaIds, skus, start, end, seed: 202).Generate(5))
            .WithEstoquesDiarios(new EstoqueDiarioFaker(lojaIds, skus, start, end, seed: 203).Generate(5))
            .WithCompras(new CompraFaker(lojaIds, skus, start, end, seed: 204).Generate(2))
            .WithPromocoes(new PromocaoFaker(lojaIds, skus, start, end, seed: 205).Generate(1))
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona"], ["SP"], start, end, seed: 206).Generate(2))
            .Build();

        var uniqueFileName = $"e2e-{Guid.NewGuid():N}.zip";
        var tempZip = Path.Combine(Path.GetTempPath(), uniqueFileName);
        await File.WriteAllBytesAsync(tempZip, zipStream.ToArray());

        try
        {
            // Depois de F11 a página exige autenticação — o login vem primeiro e o
            // redirect pós-login já cai em "/".
            var page = await fixture.NovaPaginaLogadaAsync();

            // Act — em "/" (página única), seta o file no <input> escondido disparado
            // pelo botão Importar (testa o controle direto pra evitar depender de JS
            // interop frágil sob test headless).
            await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await page.Locator("input[type=file]#hidden-zip-input").SetInputFilesAsync(tempZip);

            // Assert — notificação Radzen verde aparece.
            var successToast = page.Locator(".rz-notification-success, .rz-notification.rz-success, .rz-notification-content:has-text(\"Carga enfileirada\")");
            await successToast.First.WaitForAsync(new() { Timeout = 30_000 });

            // Assert — linha com filename único aparece no DataGrid (na mesma página).
            // O DataGrid renderiza filename em <td title="...">. Usamos seletor por texto.
            await page.Locator($"text={uniqueFileName}").First.WaitForAsync(new() { Timeout = 15_000 });
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }
}
