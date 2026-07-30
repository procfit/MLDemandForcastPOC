namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

[Collection(AspireCollection.Name)]
public sealed class ComparacoesE2ETests(AppHostFixture fixture)
{
    [Fact]
    public async Task Cria_sessao_e_ve_instrucoes_de_extracao()
    {
        var page = await fixture.NovaPaginaLogadaAsync();
        await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/");

        await page.GetByText("Nova comparação").First.ClickAsync();

        var corpo = await page.TextContentAsync("body") ?? "";
        corpo.Should().Contain("extrator",
            $"a sessao nova deve instruir a extracao. Conteudo real: <<<{corpo.Trim()}>>>");
    }
}
