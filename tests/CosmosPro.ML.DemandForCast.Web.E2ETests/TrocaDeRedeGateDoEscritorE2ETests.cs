using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// O gate do <b>escritor</b> em <c>POST /api/auth/rede</c> — a metade de
/// <c>RedeContext.PodeAtivarAsync</c> que recusa gravar o claim de escolha para quem não é
/// PowerUser.
///
/// <para>
/// Ele é redundante com o gate do <b>leitor</b> (<c>RedeContext</c> só consulta o claim
/// quando o principal é PowerUser) — por isso um teste que só olhasse o conteúdo das
/// páginas depois do POST nunca pegaria a remoção deste gate especificamente: o leitor
/// continuaria escondendo o efeito. O que este teste prova é mais estreito e é exatamente
/// o que o gate do escritor controla: para um usuário operacional, o endpoint tem de
/// recusar <b>antes</b> de reemitir o cookie de sessão. Sem o gate, <c>SignInAsync</c> roda
/// incondicionalmente e o cookie muda — mesmo que o valor do claim acabe sendo ignorado no
/// próximo request.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class TrocaDeRedeGateDoEscritorE2ETests(AppHostFixture fixture)
{
    private const string SlugRedeDoUsuario = "e2e-gate-escritor-propria";
    private const string SlugRedeAlvo = "e2e-gate-escritor-alvo";
    private const string NomeRedeDoUsuario = "Rede E2E Gate Escritor Propria";
    private const string NomeRedeAlvo = "Rede E2E Gate Escritor Alvo";
    private const string CookieDeSessao = ".AspNetCore.Identity.Application";

    [Fact]
    public async Task Post_forjado_de_usuario_operacional_nao_reemite_o_cookie_de_sessao()
    {
        var ct = TestContext.Current.CancellationToken;

        var redeDoUsuario = await fixture.GarantirRedeAtivaAsync(
            SlugRedeDoUsuario, NomeRedeDoUsuario, ct);
        var redeAlvo = await fixture.GarantirRedeAtivaAsync(SlugRedeAlvo, NomeRedeAlvo, ct);
        redeAlvo.Should().NotBe(redeDoUsuario,
            "o forjamento só faz sentido contra uma rede diferente da própria");

        await fixture.GarantirUsuarioRedeAsync(redeDoUsuario, ct);

        var page = await fixture.NovaPaginaLogadaAsync(
            AppHostFixture.UsuarioRedeEmail, AppHostFixture.UsuarioRedeSenha);
        try
        {
            var cookieAntes = await CookieDeSessaoAsync(page);

            // MaxRedirects = 0: o endpoint sempre redireciona (recusado ou aceito), então
            // seguir o redirect esconderia justamente o cabeçalho Set-Cookie da resposta do
            // POST atrás da navegação seguinte.
            var resposta = await page.APIRequest.PostAsync(
                fixture.WebfrontendUrl.TrimEnd('/') + "/api/auth/rede",
                new APIRequestContextOptions
                {
                    Form = page.APIRequest.CreateFormData()
                        .Set("redeId", redeAlvo.ToString())
                        .Set("retorno", "/"),
                    MaxRedirects = 0,
                });

            resposta.Status.Should().Be(302,
                "o endpoint sempre redireciona, recusando ou aceitando a troca");
            resposta.Headers.Should().NotContainKey("set-cookie",
                "usuário operacional não pode fazer o endpoint reemitir o cookie de " +
                "sessão — só o caminho de aceite (vedado a ele) chama SignInAsync");

            var cookieDepois = await CookieDeSessaoAsync(page);
            cookieDepois.Should().Be(cookieAntes,
                "o gate do escritor tem de recusar antes de qualquer SignInAsync para " +
                "usuário não-PowerUser");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task<string> CookieDeSessaoAsync(IPage page)
    {
        var cookies = await page.Context.CookiesAsync();
        return cookies.Single(c => c.Name == CookieDeSessao).Value;
    }
}
