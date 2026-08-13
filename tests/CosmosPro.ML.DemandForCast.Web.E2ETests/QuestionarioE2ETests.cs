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

        // 1. A tela de resultado tem de renderizar em AguardandoQuestionario — não só em
        //    Concluida — e oferecer a chamada. Se o gate da tela voltasse a ser
        //    `Status == "Concluida"`, é aqui que apareceria.
        await page.GotoAsync($"{baseUrl}/comparacoes/{sessaoId}");
        var chamada = page.Locator("[data-test=chamada-questionario]");
        await chamada.WaitForAsync(new() { Timeout = 60_000 });

        await chamada.GetByText("Responder agora").ClickAsync();
        await page.WaitForURLAsync(u => u.Contains("/questionario"), new() { Timeout = 30_000 });

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
                // Escopo pela pergunta antes de casar o texto da alternativa: as sete afirmações
                // da Parte B compartilham os mesmos rótulos ("4 – Concordo" etc.), então um
                // GetByText global casaria sempre com a primeira ocorrência e este laço
                // responderia a B1 sete vezes, deixando B2..B7 em branco — e o teste passaria a
                // provar o oposto do que afirma.
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
