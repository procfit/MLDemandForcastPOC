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

        // O clique dispara CreateAsync (round-trip SignalR + HTTP) e só então
        // Nav.NavigateTo, logo é preciso esperar a página de destino antes de ler.
        await page.WaitForURLAsync(u => u.Contains("/comparacoes/"), new() { Timeout = 30_000 });

        // A espera precisa ser por um elemento que só existe NA PÁGINA DE DESTINO.
        // "extrator" solto não serve: o empty state de "/" também diz "…extrair os
        // dados no ERP com o extrator…", e ele reaparece por um instante quando o
        // circuito interativo assume (primeiro render tem _sessoes vazio, antes do
        // RefreshAsync responder). Como o Blazor troca a URL antes de trocar o DOM,
        // sob carga o locator casava com a página que estava *saindo*; o corpo lido
        // em seguida já era a sessão nova ainda em "Carregando…" e a asserção
        // falhava. A lista <ol><li> das instruções existe só em /comparacoes/{id}.
        await page.Locator("li", new() { HasText = "extrator" })
                  .First.WaitForAsync(new() { Timeout = 15_000 });

        var corpo = await page.TextContentAsync("body") ?? "";
        corpo.Should().Contain("extrator",
            $"a sessao nova deve instruir a extracao. Conteudo real: <<<{corpo.Trim()}>>>");
    }
}
