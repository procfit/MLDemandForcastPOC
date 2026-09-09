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
/// O questionário como <b>última fase</b> do fluxo guiado, no navegador: da chamada na tela de
/// resultado até a sessão virar <c>Concluida</c>.
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
/// A sessão é semeada em <c>AguardandoQuestionario</c> com resultado e detalhe materializados:
/// chegar lá pelo caminho legítimo exigiria importar um ZIP e treinar um modelo dentro do E2E.
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
    public async Task Comprador_responde_o_questionario_e_a_sessao_conclui()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessaoId = await SemearAsync(ct);

        var page = await fixture.NovaPaginaLogadaAsync();
        var baseUrl = fixture.WebfrontendUrl.TrimEnd('/');

        // 1. O caminho inteiro até o instrumento, como o comprador o percorre: a tela de
        //    resultado chama o Quadro Resumo, e é de lá — da seção G, depois da avaliação — que
        //    se chega ao questionário. O atalho direto saiu quando o Quadro Resumo nasceu, e
        //    este percurso é agora a ÚNICA porta: se ele quebrar, a sessão fica presa em
        //    AguardandoQuestionario para sempre, que é a fase que nenhum worker reclama.
        //
        //    A tela precisa renderizar em AguardandoQuestionario, e não só em Concluida. Se o
        //    gate voltasse a ser `Status == "Concluida"`, é aqui que apareceria.
        await page.GotoAsync($"{baseUrl}/comparacoes/{sessaoId}");
        var chamada = page.Locator("[data-test=chamada-quadro-resumo]");
        await chamada.WaitForAsync(new() { Timeout = 60_000 });

        //    Espera-se o DOM do destino, e NAO a URL. `WaitForURLAsync` aguarda o estado
        //    "Load", que a navegacao client-side do Blazor nao dispara — no CI isso estourou
        //    em 30s enquanto a pagina ja estava na tela; localmente passava por timing. Um
        //    marcador do destino e deterministico nos dois lugares.
        //
        //    E a ULTIMA secao do quadro, nao a primeira: a pagina e pre-renderizada e depois
        //    reinicializada quando o circuito conecta, e esperar pela primeira devolve o
        //    controle no meio da segunda carga, com o botao ainda fora do DOM.
        await chamada.GetByText("Abrir Quadro Resumo").ClickAsync();
        await page.Locator("[data-test=secao-g]").WaitForAsync(new() { Timeout = 60_000 });

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
        }
        finally
        {
            await page.EmulateMediaAsync(new() { Media = Media.Screen });
        }

        // 5. E a sessão de fato concluiu — a transição é feita pelo endpoint de envio, e é a
        //    única da máquina de estados que não sai do Worker.
        await page.GotoAsync($"{baseUrl}/comparacoes/{sessaoId}");
        await page.GetByText("Comparação avaliada.").WaitForAsync(new() { Timeout = 60_000 });

        // Comparação sem diferenciar caixa: o rótulo do estado vive num RadzenBadge, que aplica
        // `text-transform: uppercase`, e o InnerTextAsync do Playwright devolve o texto
        // *renderizado* — "CONCLUÍDA", não "Concluída". Casar caixa aqui prenderia o teste a uma
        // decisão de CSS do componente.
        var corpo = await page.InnerTextAsync("body");
        corpo.Should().ContainEquivalentOf("Concluída",
            $"a sessão tem de sair de 'Aguardando avaliação' depois do envio. Corpo: <<<{corpo.Trim()}>>>");
        corpo.Should().NotContainEquivalentOf("Falta sua avaliação",
            "a chamada não pode sobreviver ao envio");
    }

    // --- Semeadura -----------------------------------------------------------

    private async Task<Guid> SemearAsync(CancellationToken ct) =>
        await fixture.SemearSessaoConcluidaAsync(
            NomeDaSessao,
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
