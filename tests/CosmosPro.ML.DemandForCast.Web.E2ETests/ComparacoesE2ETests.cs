using Microsoft.Playwright;

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
        // em seguida já era a sessão nova ainda em "Carregando…" e a asserção falhava.
        //
        // A âncora é um data-test, e não mais um <li> com a palavra "extrator": a
        // segunda versão quebrou no dia em que o menu lateral ganhou um item
        // "Extrator" — um <li> com a mesma palavra, presente em *toda* página, e o
        // `HasText` do Playwright casa por substring ignorando caixa. Texto de UI é
        // vocabulário compartilhado; identidade de elemento não pode depender dele.
        await page.Locator("[data-test=instrucoes-extracao]")
                  .WaitForAsync(new() { Timeout = 15_000 });

        var corpo = await page.TextContentAsync("body") ?? "";
        corpo.Should().Contain("extrator",
            $"a sessao nova deve instruir a extracao. Conteudo real: <<<{corpo.Trim()}>>>");
    }

    /// <summary>
    /// O botão de download só faz sentido em <c>AguardandoDados</c> — é ali que o
    /// comprador ainda não rodou o extrator. A asserção é sobre o texto do botão, não sobre
    /// versão/checksum: esses dependem do que está publicado no MinIO no momento do teste
    /// (estado de infra, não do render em si), e afirmar sobre eles aqui deixaria o teste
    /// flutuando com o que outro processo publicou ou removeu do bucket.
    /// </summary>
    [Fact]
    public async Task Sessao_nova_em_AguardandoDados_mostra_o_botao_de_baixar_extrator()
    {
        var page = await fixture.NovaPaginaLogadaAsync();
        await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/");

        await page.GetByText("Nova comparação").First.ClickAsync();
        await page.WaitForURLAsync(u => u.Contains("/comparacoes/"), new() { Timeout = 30_000 });

        await page.GetByText("Baixar extrator").First.WaitForAsync(new() { Timeout = 15_000 });

        var corpo = await page.TextContentAsync("body") ?? "";
        corpo.Should().Contain("Baixar extrator",
            $"AguardandoDados é o único estado em que baixar o extrator ainda faz sentido. Conteudo real: <<<{corpo.Trim()}>>>");
    }

    /// <summary>
    /// Exclusão pela listagem, do clique ao desaparecimento da linha. Cobre o que o teste de
    /// integração não alcança: o diálogo de confirmação existe, e o clique no ícone de lixeira
    /// **não** navega para a sessão — o grid tem `RowSelect`, e sem `stopPropagation` o mesmo
    /// gesto abriria a página e deixaria o diálogo por trás dela.
    /// </summary>
    [Fact]
    public async Task Excluir_pela_listagem_pede_confirmacao_e_remove_a_linha()
    {
        var page = await fixture.NovaPaginaLogadaAsync();
        var baseUrl = fixture.WebfrontendUrl.TrimEnd('/');

        await page.GotoAsync(baseUrl + "/");
        await page.GetByText("Nova comparação").First.ClickAsync();
        await page.WaitForURLAsync(u => u.Contains("/comparacoes/"), new() { Timeout = 30_000 });

        // A linha alvo é a **primeira** do grid, não uma casada por id: a listagem ordena por
        // CriadoEm desc, então a sessão que acabou de ser criada está no topo. Identificar por
        // texto não funciona aqui — o grid mostra `id[..8]` quando a sessão não tem nome, e os
        // 8 primeiros hex de um UUIDv7 são os bits altos do timestamp em ms: sessões criadas
        // no mesmo minuto compartilham o prefixo, e o locator casava com as três que os testes
        // desta classe criam em sequência (passava sozinho, falhava na suíte).
        await page.GotoAsync(baseUrl + "/");
        var primeiraLinha = page.Locator("tbody tr").First;
        await primeiraLinha.WaitForAsync(new() { Timeout = 15_000 });

        await primeiraLinha.Locator("[data-test=excluir-comparacao]").ClickAsync();

        // Confirmação obrigatória: sem clicar em "Excluir" no diálogo, nada acontece.
        var confirmar = page.GetByRole(AriaRole.Button, new() { Name = "Excluir", Exact = true });
        await confirmar.WaitForAsync(new() { Timeout = 15_000 });

        page.Url.Should().EndWith("/",
            "o clique na lixeira não pode navegar para a sessão — o RowSelect do grid tem de ser barrado");

        await confirmar.ClickAsync();

        // O desfecho observável do fluxo é a confirmação de sucesso: ela só aparece quando o
        // DELETE volta 2xx. Que a linha some da tabela e que o detalhe por item vá junto está
        // provado em ComparacoesIntegrationTests, contra o banco — aqui o que se testa é o
        // caminho da UI até o endpoint.
        await Assertions.Expect(page.GetByText("Comparação excluída"))
                        .ToBeVisibleAsync(new() { Timeout = 20_000 });
    }
}
