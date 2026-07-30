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

        // Sem isto o teste é racy: o clique dispara CreateAsync (round-trip SignalR +
        // HTTP) e só então Nav.NavigateTo — ler o body logo após o ClickAsync (que não
        // espera esse trabalho assíncrono nem a troca de página) às vezes lê a lista
        // ainda em "/". Espera pela URL de destino e por um texto que só existe na
        // página de sessão, em vez de body inteiro logo após o clique — mesmo padrão
        // dos demais testes deste projeto (AuthorizationE2ETests, ImportsE2ETests).
        await page.WaitForURLAsync(u => u.Contains("/comparacoes/"), new() { Timeout = 30_000 });
        await page.GetByText("extrator").First.WaitForAsync(new() { Timeout = 15_000 });

        var corpo = await page.TextContentAsync("body") ?? "";
        corpo.Should().Contain("extrator",
            $"a sessao nova deve instruir a extracao. Conteudo real: <<<{corpo.Trim()}>>>");
    }
}
