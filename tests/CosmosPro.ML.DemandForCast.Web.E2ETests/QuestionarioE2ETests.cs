using System.Text.Json;
using System.Text.Json.Serialization;

using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Engine.Questionarios;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;

using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// O fluxo de avaliação no navegador, do jeito que o Professor definiu: seção G a cada
/// execução, e o questionário <b>uma vez só</b>, liberado depois de duas execuções avaliadas.
///
/// <para>
/// É o cenário que os testes de integração não alcançam. Eles provam o contrato dos endpoints;
/// o que só aparece aqui é o wizard: se o <c>RadzenSteps</c> avança, se a guarda do passo
/// incompleto barra, e se o modo leitura aparece depois do envio. Foi justamente uma quebra
/// invisível ao compilador — parâmetro de rota que derrubava a tabela de endpoints inteira —
/// que motivou não confiar em "compilou" para esta camada.
/// </para>
///
/// <para>
/// <b>DUAS sessões</b> são semeadas em <c>AguardandoAvaliacao</c> com resultado e detalhe
/// materializados, porque o portão exige duas: chegar lá pelo caminho legítimo exigiria
/// importar dois ZIPs e treinar dois modelos dentro do E2E.
/// As perguntas vêm do <see cref="QuestionarioCatalogo"/> real, lidas em tempo de teste em vez
/// de escritas à mão — é o que mantém este cenário válido quando o instrumento definitivo
/// substituir o catálogo provisório.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class QuestionarioE2ETests(AppHostFixture fixture)
{
    private const string NomeDaSessao = "Questionario semeado pelo E2E";
    private const long SugestaoId = 88_301;
    private const byte TipoCalculo = 2;

    [Fact]
    public async Task O_questionario_libera_na_segunda_execucao_avaliada_e_some_depois_do_envio()
    {
        var ct = TestContext.Current.CancellationToken;

        // O QUESTIONARIO E POR COMPRADOR, e o banco destes testes e persistente: um respondido
        // numa execucao anterior da suite deixaria o portao selado, e o ATO 1 falharia sem
        // relacao aparente com a causa. Limpa tambem os vereditos dele, porque o portao conta
        // execucoes avaliadas por ele -- residuo abriria o portao cedo.
        await fixture.LimparAvaliacoesDoCompradorAsync(AppHostFixture.PowerUserEmail, ct);

        // NOMES DISTINTOS, e isto não é cosmético: `SemearSessaoConcluidaAsync` APAGA as
        // sessões com o mesmo nome antes de inserir — é a dedup dela para o banco persistente
        // entre execuções. Semear duas com o mesmo nome faria a segunda destruir a primeira, e
        // o sintoma seria um timeout esperando a tela de uma sessão que não existe mais.
        var primeira = await SemearAsync(ct, $"{NomeDaSessao} #1");
        var segunda = await SemearAsync(ct, $"{NomeDaSessao} #2");

        var page = await fixture.NovaPaginaLogadaAsync();
        var baseUrl = fixture.WebfrontendUrl.TrimEnd('/');

        // ATO 1 -- uma execucao avaliada NAO libera o questionario, e a tela EXPLICA quanto
        // falta em vez de esconder o botao. Botao ausente le-se como defeito; a regra escrita
        // le-se como regra.
        await AvaliarAsync(page, baseUrl, primeira);

        await page.Locator("[data-test=questionario-bloqueado]")
                  .WaitForAsync(new() { Timeout = 30_000 });
        (await page.Locator("[data-test=ir-para-questionario]").CountAsync())
            .Should().Be(0, "com uma execucao avaliada o questionario ainda nao abriu");

        // E a execucao avaliada JA CONCLUIU -- quem conclui passou a ser a secao G.
        //
        // ESPERA UM MARCADOR, e nao le o body direto: `GotoAsync` volta antes de o circuito
        // Blazor pintar, e o InnerText logo depois devolve o menu da moldura. "Execucao
        // avaliada." so renderiza quando o status saiu de AguardandoAvaliacao, entao esperar
        // por ele JA E a afirmacao -- e e deterministico.
        await page.GotoAsync($"{baseUrl}/comparacoes/{primeira}");
        await page.GetByText("Execução avaliada.").WaitForAsync(new() { Timeout = 60_000 });

        // Comparacao sem diferenciar caixa: o rotulo do estado vive num RadzenBadge, que aplica
        // `text-transform: uppercase`, e o InnerTextAsync devolve o texto RENDERIZADO --
        // "CONCLUÍDA", nao "Concluída". Casar caixa aqui prenderia o teste a uma decisao de CSS.
        var corpoDaPrimeira = await page.InnerTextAsync("body");
        corpoDaPrimeira.Should().ContainEquivalentOf("Concluída",
            "a secao G e o que encerra a execucao desde 19/09/2026");
        corpoDaPrimeira.Should().NotContainEquivalentOf("Falta sua avaliação",
            "a chamada nao pode sobreviver ao registro do veredito");

        // ATO 2 -- a segunda execucao avaliada ABRE o portao, e o botao aparece. Continuamos na
        //    secao G da segunda execucao, que e onde AvaliarAsync deixou a pagina.
        //
        //    O percurso ate o instrumento e o do comprador: tela de resultado -> Quadro Resumo
        //    -> secao G -> questionario. O atalho direto saiu quando o Quadro Resumo nasceu, e
        //    este e agora o UNICO caminho; se ele quebrar, ninguem responde o instrumento.
        await AvaliarAsync(page, baseUrl, segunda);

        await page.Locator("[data-test=ir-para-questionario]")
                  .WaitForAsync(new() { Timeout = 30_000 });
        (await page.Locator("[data-test=questionario-bloqueado]").CountAsync())
            .Should().Be(0, "com duas execucoes avaliadas o portao abriu");

        // ATO 3 -- responde o instrumento inteiro.
        await page.Locator("[data-test=ir-para-questionario]").ClickAsync();
        await page.GetByText("Passo 1 de").WaitForAsync(new() { Timeout = 60_000 });

        var secoes = QuestionarioCatalogo.Secoes;

        // 2. A guarda do passo incompleto: clicar em "Próximo" sem responder não pode avançar.
        //    Só vale afirmar isto quando a primeira seção tem alguma obrigatória.
        if (secoes[0].Perguntas.Any(p => p.Obrigatoria))
        {
            await page.GetByText(secoes[0].Titulo).First.WaitForAsync(new() { Timeout = 30_000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Próximo" }).ClickAsync();

            // O aviso é uma notificação Radzen; a prova de que NÃO avançou é a última seção
            // continuar ausente e o botão "Próximo" continuar na tela.
            await page.GetByText("Passo 1 de").WaitForAsync(new() { Timeout = 15_000 });
        }

        // 3. Percorre o wizard respondendo a primeira alternativa de cada pergunta.
        for (var i = 0; i < secoes.Count; i++)
        {
            await page.GetByText(secoes[i].Titulo).First.WaitForAsync(new() { Timeout = 30_000 });

            foreach (var pergunta in secoes[i].Perguntas)
            {
                // Escopo pela pergunta antes de casar o texto da alternativa: TODAS as afirmações
                // da Parte B compartilham os mesmos rótulos ("4 – Concordo" etc.), então um
                // GetByText global casaria sempre com a primeira ocorrência e este laço
                // responderia a B1 onze vezes, deixando as demais em branco — e o teste passaria
                // a provar o oposto do que afirma. O laço percorre o catálogo, então ele
                // acompanha a renumeração do instrumento sozinho; foi o que o levou de doze
                // afirmações (V5) para onze (V6) sem uma linha de mudança aqui.
                await page.Locator($"[data-test=pergunta-{pergunta.Codigo}]")
                          .GetByText(pergunta.Opcoes[0].Texto, new() { Exact = true })
                          .First.ClickAsync();
            }

            var ultimo = i == secoes.Count - 1;
            var rotulo = ultimo ? "Enviar avaliação" : "Próximo";
            await page.GetByRole(AriaRole.Button, new() { Name = rotulo }).ClickAsync();
        }

        // 4. Selado: a tela entra em modo leitura pelo status da sessão, não por estado local.
        await page.Locator("[data-test=questionario-selado]")
                  .WaitForAsync(new() { Timeout = 30_000 });

        // 4b. A VISAO DE IMPRESSAO TEM TODAS AS PERGUNTAS, e nao so as do passo ativo.
        //
        // E o defeito que o patrocinador relatou em 09/09/2026: o PDF saia com UMA pagina, com
        // a Parte B e cortado no meio da segunda pergunta. A causa e que RadzenSteps renderiza
        // somente o passo ativo -- o passo inativo NAO ESTA no HTML, entao nenhuma regra de
        // @media print podia recupera-lo. Quem imprime passou a ser um bloco plano proprio.
        //
        // O teste percorre o catalogo inteiro de proposito: preso a uma lista fixa de codigos,
        // ele passaria a afirmar menos do que precisa na proxima renumeracao do instrumento --
        // e o instrumento ja foi renumerado duas vezes.
        var impresso = await page.Locator("[data-test=impressao-respostas]").InnerTextAsync();

        foreach (var pergunta in QuestionarioCatalogo.Perguntas)
        {
            impresso.Should().Contain(pergunta.Codigo,
                $"'{pergunta.Codigo}' e do instrumento e tem de sair no papel, esteja em que passo estiver");
            impresso.Should().Contain(pergunta.Texto,
                $"o enunciado de '{pergunta.Codigo}' e o que torna a folha um registro legivel");
        }

        // E a resposta escolhida, nao so a pergunta. O laco do wizard marcou sempre a PRIMEIRA
        // alternativa, entao e o texto dela que tem de estar no papel.
        foreach (var pergunta in QuestionarioCatalogo.Perguntas)
        {
            impresso.Should().Contain(pergunta.Opcoes[0].Texto,
                $"a resposta marcada em '{pergunta.Codigo}' precisa aparecer ao lado do enunciado");
        }

        impresso.Should().NotContain("(sem resposta)",
            "o questionario foi respondido inteiro; '(sem resposta)' aqui denunciaria pergunta perdida");

        // 4c. E A REGRA DE IMPRESSAO DE FATO TROCA OS DOIS BLOCOS. O conteudo estar no HTML nao
        // basta: `.apenas-impressao` nasce com display:none e so aparece sob @media print. Ja
        // aconteceu neste repositorio um caso de CSS que compila, publica e renderiza errado sem
        // teste nenhum acusar (as faixas de secao do Quadro Resumo sairam como retangulos
        // vazios), e a folha impressa e justamente o que ninguem olha antes de entregar.
        var blocoImpresso = page.Locator("[data-test=impressao-respostas]");
        var botoes = page.Locator("[data-print=ocultar]").First;

        await Assertions.Expect(blocoImpresso).ToBeHiddenAsync();
        await Assertions.Expect(botoes).ToBeVisibleAsync();

        await page.EmulateMediaAsync(new() { Media = Media.Print });
        try
        {
            await Assertions.Expect(blocoImpresso).ToBeVisibleAsync();
            await Assertions.Expect(botoes).ToBeHiddenAsync();

            // 4d. E O LAYOUT DO RADZEN TEM DE ESTAR SOLTO, SENAO A FOLHA SAI COM UMA PAGINA SO.
            //
            // Segundo relato do patrocinador, 16/09/2026: o bloco plano de 4b ja aparecia -- a
            // correcao anterior funcionou --, mas a pre-visualizacao declarava "1 pagina" e
            // cortava no meio da Parte B. A causa nao e nossa: sao tres regras do
            // `radzen.blazor/8.4.2/staticwebassets/css/default-base.css`.
            //
            //     .rz-layout { height:100vh; overflow:hidden; display:grid; ... }
            //     .rz-body   { transform: translateZ(0) }
            //     .rz-body   { width: 100vw }
            //
            // A decisiva e o `transform`: ELEMENTO TRANSFORMADO NAO FRAGMENTA ENTRE PAGINAS --
            // o Chrome imprime o que cabe na primeira folha e descarta o resto. A regra
            // anterior do nosso @media print soltava `overflow` e `height` de
            // `html, body, .rz-body, main`, o que nao bastava: faltava o `.rz-layout`, faltava
            // matar o `transform`, e `main` nem existe neste layout.
            //
            // Afirma o efeito das regras, e nao a contagem de paginas: a contagem exigiria
            // `page.pdf()` mais um parser de PDF. Sao TRES invariantes, e cada uma morde --
            // conferido revertendo a correcao.
            //
            // CUIDADO AO ACRESCENTAR ASSERCAO AQUI: `getComputedStyle` devolve valor USADO,
            // nao declarado. `height` e `width` vem sempre em pixels, mesmo sob `auto`, entao
            // comparar com "auto" ou com "100vw" produz asserção que nunca morde -- foi o erro
            // da primeira versao deste bloco. Compare NUMEROS, ou compare propriedades cujo
            // computado e categorico (`overflow`, `transform`).
            var medidas = await page.EvaluateAsync<string[]>(
                "() => { const l = getComputedStyle(document.querySelector('.rz-layout')); "
                + "const c = getComputedStyle(document.querySelector('.rz-body')); "
                + "const bloco = document.querySelector('[data-test=impressao-respostas]'); "
                + "return [l.overflow, c.transform, l.height, String(bloco.scrollHeight)]; }");

            medidas[0].Should().Be("visible",
                "o .rz-layout tem overflow:hidden fora da impressao e cortaria a folha");

            medidas[1].Should().Be("none",
                "elemento transformado NAO fragmenta entre paginas -- e o que fazia sair 1 pagina so");

            // A altura do layout tem de acomodar a folha inteira. Presa em 100vh ela pararia na
            // altura da JANELA, com o resto do questionario fora do fluxo de impressao.
            var alturaDoLayout = double.Parse(medidas[2].Replace("px", ""),
                System.Globalization.CultureInfo.InvariantCulture);
            var alturaDaFolha = double.Parse(medidas[3], System.Globalization.CultureInfo.InvariantCulture);

            alturaDoLayout.Should().BeGreaterThanOrEqualTo(alturaDaFolha,
                "o layout precisa crescer com o conteudo; preso em 100vh ele para na altura da janela");
        }
        finally
        {
            await page.EmulateMediaAsync(new() { Media = Media.Screen });
        }

        // ATO 4 -- RESPONDIDO UMA VEZ SO. Voltando a QUALQUER execucao, o questionario nao e
        //    mais oferecido: o botao vira "ver minhas respostas". E o pedido literal do
        //    patrocinador -- "apos o envio, ele nao devera ser apresentado novamente ao mesmo
        //    comprador em novas execucoes".
        foreach (var execucao in new[] { primeira, segunda })
        {
            await AbrirSecaoGAsync(page, baseUrl, execucao);

            await page.Locator("[data-test=ver-questionario]")
                      .WaitForAsync(new() { Timeout = 30_000 });
            (await page.Locator("[data-test=ir-para-questionario]").CountAsync())
                .Should().Be(0, $"o questionario ja foi respondido; a execucao {execucao} nao pode reoferece-lo");
            (await page.Locator("[data-test=questionario-bloqueado]").CountAsync())
                .Should().Be(0, "respondido nao e o mesmo que bloqueado");
        }
    }

    /// <summary>Abre o Quadro Resumo de uma execução e espera a seção G aparecer.</summary>
    private static async Task AbrirSecaoGAsync(IPage page, string baseUrl, Guid sessaoId)
    {
        await page.GotoAsync($"{baseUrl}/comparacoes/{sessaoId}");

        var chamada = page.Locator("[data-test=chamada-quadro-resumo]");
        await chamada.WaitForAsync(new() { Timeout = 60_000 });

        //    Espera-se o DOM do destino, e NAO a URL. `WaitForURLAsync` aguarda o estado
        //    "Load", que a navegacao client-side do Blazor nao dispara.
        await chamada.GetByText("Abrir Quadro Resumo").ClickAsync();
        await page.Locator("[data-test=secao-g]").WaitForAsync(new() { Timeout = 60_000 });
    }

    /// <summary>
    /// Registra a seção G de uma execução pela tela. É o que <b>conclui</b> a execução e o que a
    /// faz contar para o portão do questionário.
    /// </summary>
    private static async Task AvaliarAsync(IPage page, string baseUrl, Guid sessaoId)
    {
        await AbrirSecaoGAsync(page, baseUrl, sessaoId);

        await page.Locator("[data-test=opcoes-avaliacao]")
                  .GetByText("Válido", new() { Exact = true })
                  .First.ClickAsync();
        await page.Locator("[data-test=registrar-avaliacao]").ClickAsync();
        await page.Locator("[data-test=avaliacao-registrada]")
                  .WaitForAsync(new() { Timeout = 30_000 });
    }

    // --- Semeadura -----------------------------------------------------------

    private async Task<Guid> SemearAsync(CancellationToken ct, string nome) =>
        await fixture.SemearSessaoConcluidaAsync(
            nome,
            SugestaoId,
            new DateTime(2026, 7, 1, 9, 30, 0),
            TipoCalculo,
            skusSemCadastro: 0,
            ResultadoJson(),
            Itens(),
            ct);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Montado com o tipo real do Worker e as mesmas opções do materializador, para o payload
    /// atravessar a apiservice e o client de verdade — um JSON à mão só provaria que a página lê
    /// o JSON que ela própria espera.
    /// </summary>
    private static string ResultadoJson()
    {
        var resultado = new SessaoResultado(
            GeradoEm: DateTimeOffset.UtcNow,
            ComparacaoPbsId: Guid.CreateVersion7(),
            SugestaoDataHora: new DateTime(2026, 7, 1, 9, 30, 0),
            TipoCalculo: TipoCalculo,
            ItensAvaliados: 1,
            VendidoNaJanelaUnidades: 30m,
            Pbs: new BracoDaSessao(CompraUnidades: 50m, SobraUnidades: 20m, SobraValor: 80m),
            Confronto: null,
            MotivoMlIndisponivel: "Cobertura além do horizonte do ML.",
            ItensComDecisaoMl: 0,
            ItensComPrevisaoMl: 0,
            UtilidadeDecisaoMl: UtilidadeComparacao.ForaDoHorizonteMl,
            Ruptura: new RupturaObservada(
                ItensComDiaSemEstoque: 0, DiasSemEstoque: 0, DiasComSnapshot: 30, DiasNaJanela: 30),
            ItensComJanelaAlemDoHistorico: 0,
            ItensSemPrecoCompra: 0,
            SkusSemCadastro: 0,
            RessalvaTreinoServe: ComparacaoOutput.RessalvaPadraoTreinoServe);

        return JsonSerializer.Serialize(resultado, Json);
    }

    private static IReadOnlyList<ComparacaoSessaoItem> Itens() =>
    [
        new()
        {
            LojaId = 1,
            Sku = "SKU-QUEST-E2E",
            NomeProduto = "Dipirona Questionario 500mg",
            Curva = "A",
            CompraSugeridaPbs = 50m,
            CompraSugeridaMl = null,
            VendidoNaJanela = 30m,
            DemandaDiaPbs = 1m,
            DemandaDiaMl = null,
            DemandaDiaReal = null,
            SobraPbsUnidades = 20m,
            SobraMlUnidades = null,
            SobraPbsValor = 80m,
            SobraMlValor = null,
            JanelaAlemDoHistorico = false,
        },
    ];
}
