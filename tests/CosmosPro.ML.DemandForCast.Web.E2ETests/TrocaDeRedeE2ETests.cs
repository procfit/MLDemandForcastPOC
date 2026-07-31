using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// O seletor de rede do cabeçalho, para PowerUser — o único usuário a quem escolher rede é
/// legítimo, porque ele é autorizado em todas.
///
/// <para>
/// Este é o cenário que faltava: os testes de client sempre trocaram o <c>IRedeContext</c> por
/// um dublê de valor fixo, então ninguém exercitava a ida-e-volta da escolha. Ela é gravada
/// como claim no cookie por <c>POST /api/auth/rede</c> e lida de novo pelo circuito que nasce
/// do recarregamento; a versão anterior guardava a escolha num campo do serviço <c>scoped</c> e
/// recarregava em seguida, destruindo o circuito que a guardava — a listagem nunca saía da
/// primeira rede ativa.
/// </para>
///
/// <para>
/// Duas redes próprias, cada uma com uma comparação de nome reconhecível: sem dado
/// distinguível nos dois lados, uma listagem que não troca de escopo é indistinguível de uma
/// que troca. As asserções são sobre o <b>texto do corpo</b> — <c>RadzenText</c> não emite
/// <c>h1</c>/<c>h4</c>, então seletor de heading não é âncora confiável nesta aplicação.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class TrocaDeRedeE2ETests(AppHostFixture fixture)
{
    private const string SlugRedeA = "e2e-troca-a";
    private const string SlugRedeB = "e2e-troca-b";
    private const string NomeRedeA = "Rede E2E Troca A";
    private const string NomeRedeB = "Rede E2E Troca B";
    private const string ComparacaoDaRedeA = "Comparacao semeada na rede A do E2E";
    private const string ComparacaoDaRedeB = "Comparacao semeada na rede B do E2E";

    [Fact]
    public async Task Trocar_de_rede_no_seletor_troca_a_listagem_e_a_escolha_sobrevive_ao_F5()
    {
        var ct = TestContext.Current.CancellationToken;

        var redeA = await fixture.GarantirRedeAtivaAsync(SlugRedeA, NomeRedeA, ct);
        var redeB = await fixture.GarantirRedeAtivaAsync(SlugRedeB, NomeRedeB, ct);
        redeB.Should().NotBe(redeA, "o cenário exige dois inquilinos distintos");

        await fixture.SemearSessaoNaRedeAsync(ComparacaoDaRedeA, redeA, ct);
        await fixture.SemearSessaoNaRedeAsync(ComparacaoDaRedeB, redeB, ct);

        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/");

            await TrocarParaAsync(page, redeA);
            await page.GetByText(ComparacaoDaRedeA).First.WaitForAsync(new() { Timeout = 60_000 });

            await TrocarParaAsync(page, redeB);
            await page.GetByText(ComparacaoDaRedeB).First.WaitForAsync(new() { Timeout = 60_000 });

            var corpo = await page.TextContentAsync("body") ?? "";
            corpo.Should().NotContain(ComparacaoDaRedeA,
                "a listagem tem de trocar de escopo junto com o seletor, e não somar as redes. " +
                $"Conteúdo real: <<<{corpo.Trim()}>>>");

            // F5 é o teste real do mecanismo: a escolha mora no cookie, não no circuito.
            // Antes da correção, qualquer recarregamento devolvia o escopo à primeira rede ativa.
            await page.ReloadAsync();
            await page.GetByText(ComparacaoDaRedeB).First.WaitForAsync(new() { Timeout = 60_000 });

            (await page.Locator("select[name='redeId']").InputValueAsync())
                .Should().Be(redeB.ToString(),
                    "depois do F5 o seletor precisa refletir o escopo que as consultas usam");
        }
        finally
        {
            // Cada aba viva é um circuito Blazor fazendo poll contra o mesmo AppHost dos
            // outros cenários E2E.
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Escolhe a rede no <c>select</c> do cabeçalho. Não espera pela navegação: o
    /// <c>onchange</c> submete o form e o 302 recarrega a página, e quem espera de fato é o
    /// locator de texto seguinte — locators reavaliam através da navegação, e afirmar sobre o
    /// corpo antes disso leria a página que estava saindo.
    /// </summary>
    private static async Task TrocarParaAsync(IPage page, int redeId)
    {
        var seletor = page.Locator("select[name='redeId']");
        await seletor.WaitForAsync(new() { Timeout = 30_000 });
        await seletor.SelectOptionAsync(new SelectOptionValue { Value = redeId.ToString() });
    }
}
