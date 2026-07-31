using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// Tela técnica do comparativo F13 (<c>/comparacao</c>). Não confundir com
/// <see cref="ComparacoesE2ETests"/>, que cobre a sessão guiada de F14.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class ComparacaoE2ETests(AppHostFixture fixture)
{
    [Fact]
    public async Task Anonimo_e_levado_ao_login()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/comparacao");
        await page.WaitForURLAsync(u => u.Contains("/login"), new() { Timeout = 30_000 });

        page.Url.Should().Contain("/login", "a tela do comparativo expõe dado comercial da rede");
    }

    /// <summary>
    /// Renderiza para um usuário autenticado. As asserções são sobre o TEXTO do corpo, e
    /// não sobre tags de cabeçalho: <c>RadzenText</c> não emite <c>h1</c>/<c>h4</c>, então
    /// seletor de heading não é âncora confiável nesta aplicação.
    /// </summary>
    [Fact]
    public async Task Pagina_renderiza_para_usuario_autenticado_com_as_tres_camadas_e_as_abas_por_metodo()
    {
        var page = await fixture.NovaPaginaLogadaAsync();

        await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/comparacao");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // O conteúdo só aparece depois do circuito interativo assumir (o carregamento
        // é pulado no prerender de propósito), então espera-se pelo texto, não pela URL.
        await page.GetByText("Nova execução").First.WaitForAsync(new() { Timeout = 30_000 });

        var corpo = await CorpoNormalizadoAsync(page);

        corpo.Should().NotContain("Acesso negado");
        corpo.Should().Contain("Comparativo do ERP contra o ML",
            $"a página /comparacao deveria ter carregado. Conteúdo real: <<<{corpo}>>>");

        corpo.Should().Contain("Método de cálculo do ERP", "a seleção de método faz parte da tela");
        corpo.Should().Contain("Emax e Eseg");
        corpo.Should().Contain("Dias de Reposição");
        corpo.Should().Contain("nunca são somados nem promediados",
            "os dois métodos são baselines distintos e a tela precisa dizer isso na página");
    }

    /// <summary>
    /// Sem execução selecionada, cada aba de método precisa dizer que não há resultado
    /// dela — e não exibir uma tabela vazia, que leria como "nenhuma diferença encontrada".
    /// </summary>
    [Fact]
    public async Task Aba_sem_execucao_selecionada_explica_a_ausencia_em_vez_de_mostrar_tabela_vazia()
    {
        var page = await fixture.NovaPaginaLogadaAsync();

        await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/comparacao");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.GetByText("Nova execução").First.WaitForAsync(new() { Timeout = 30_000 });

        var corpo = await CorpoNormalizadoAsync(page);

        corpo.Should().Contain("Esta aba não recebe números de outro método",
            $"a aba precisa explicar a ausência. Conteúdo real: <<<{corpo}>>>");
    }

    /// <summary>
    /// Texto do corpo com espaços colapsados: o Razor quebra as frases longas em várias
    /// linhas, e o <c>textContent</c> preserva as quebras — asserção sobre a frase inteira
    /// falharia por indentação, não por conteúdo ausente.
    /// </summary>
    private static async Task<string> CorpoNormalizadoAsync(IPage page)
    {
        var corpo = await page.TextContentAsync("body") ?? "";
        return Regex.Replace(corpo, @"\s+", " ").Trim();
    }
}
