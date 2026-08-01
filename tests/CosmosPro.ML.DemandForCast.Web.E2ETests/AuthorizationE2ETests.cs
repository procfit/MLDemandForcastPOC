using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// Garantias de acesso da F11. Antes dela, qualquer anônimo abria qualquer página.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class AuthorizationE2ETests(AppHostFixture fixture)
{
    [Fact]
    public async Task Anonimo_e_levado_ao_login_ao_abrir_pagina_de_dados()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/dados");
        await page.WaitForURLAsync(u => u.Contains("/login"), new() { Timeout = 30_000 });

        page.Url.Should().Contain("/login", "página de dados não pode ser anônima");
    }

    [Fact]
    public async Task Anonimo_e_levado_ao_login_ao_abrir_administracao()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/admin/redes");
        await page.WaitForURLAsync(u => u.Contains("/login"), new() { Timeout = 30_000 });

        page.Url.Should().Contain("/login", "área administrativa não pode ser anônima");
    }

    [Fact]
    public async Task Senha_errada_nao_autentica()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var baseUrl = fixture.WebfrontendUrl.TrimEnd('/');

        await page.GotoAsync($"{baseUrl}/login");
        await page.FillAsync("input[name='email']", AppHostFixture.PowerUserEmail);
        await page.FillAsync("input[name='senha']", "senha-completamente-errada");
        await page.ClickAsync("button[type='submit']");

        await page.WaitForURLAsync(u => u.Contains("erro="), new() { Timeout = 30_000 });
        page.Url.Should().Contain("/login", "credencial inválida não pode autenticar");
    }

    [Fact]
    public async Task PowerUser_ve_administracao_de_redes_e_usuarios()
    {
        var page = await fixture.NovaPaginaLogadaAsync();
        var baseUrl = fixture.WebfrontendUrl.TrimEnd('/');

        // Assertivas sobre o texto da página em vez de esperar por seletor: quando
        // falha, a mensagem mostra o que a página de fato renderizou, em lugar de um
        // timeout opaco.
        await page.GotoAsync($"{baseUrl}/admin/redes");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var corpoRedes = await page.TextContentAsync("body") ?? "";
        corpoRedes.Should().NotContain("Acesso negado");
        corpoRedes.Should().Contain("Nova rede",
            $"a página /admin/redes deveria ter carregado. Conteúdo real: <<<{corpoRedes.Trim()}>>>");

        await page.GotoAsync($"{baseUrl}/admin/usuarios");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var corpoUsuarios = await page.TextContentAsync("body") ?? "";
        corpoUsuarios.Should().NotContain("Acesso negado");
        corpoUsuarios.Should().Contain("Novo usuário",
            $"a página /admin/usuarios deveria ter carregado. Conteúdo real: <<<{corpoUsuarios.Trim()}>>>");
    }
}
