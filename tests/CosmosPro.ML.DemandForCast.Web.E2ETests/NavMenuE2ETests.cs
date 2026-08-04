using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// O menu vive no <c>MainLayout</c>, e o <c>&lt;Routes /&gt;</c> do <c>App.razor</c> não
/// declara render mode — o layout inteiro é SSR estático. Sem uma ilha interativa no
/// <c>NavMenu</c>, o <c>RadzenPanelMenu</c> renderiza como HTML morto: os grupos aparecem no
/// DOM (é por isso que os testes que só leem o texto do corpo passavam), mas clicar não
/// expande nada, porque não há circuito que atenda o evento.
/// O grupo escolhido é "Técnico" de propósito: "Administração" tem <c>Expanded="true"</c>
/// cravado no markup, então ele aparece aberto mesmo estático e não distinguiria os dois
/// mundos.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class NavMenuE2ETests(AppHostFixture fixture)
{
    [Fact]
    public async Task Grupo_do_menu_expande_e_recolhe_ao_clique()
    {
        var page = await fixture.NovaPaginaLogadaAsync();

        // Um filho exclusivo do grupo "Técnico". "Importação manual" não aparece em nenhum
        // outro lugar do layout, então visibilidade dele é sinal do grupo, não de vizinhança.
        var filho = page.GetByRole(AriaRole.Link, new() { Name = "Importação manual" });
        var grupo = page.GetByText("Técnico", new() { Exact = true });

        await grupo.WaitForAsync(new() { Timeout = 30_000 });

        await Assertions.Expect(filho).ToBeHiddenAsync(new() { Timeout = 15_000 });

        await grupo.ClickAsync();
        await Assertions.Expect(filho).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await grupo.ClickAsync();
        await Assertions.Expect(filho).ToBeHiddenAsync(new() { Timeout = 15_000 });
    }
}
