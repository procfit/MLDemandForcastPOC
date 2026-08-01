using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using CosmosPro.ML.DemandForCast.Forecasting.Comparison;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Comparison;

using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// Tela técnica do comparativo F13 (<c>/comparacao</c>). Não confundir com
/// <see cref="ComparacoesE2ETests"/>, que cobre a sessão guiada de F14.
///
/// <para>
/// Os testes do bloco de resultados rodam contra uma execução <b>semeada</b> no banco
/// <c>engine</c>: a página só emite esse bloco quando existe execução concluída, e produzir
/// uma pelo caminho legítimo exigiria importar dado e treinar um modelo dentro do E2E. O
/// <c>ResultadoJson</c> é montado com os tipos reais (<see cref="ComparacaoOutput"/> e as
/// três camadas) e serializado com as mesmas opções do Worker, então ele atravessa o
/// <c>ComparisonApiClient</c> de verdade — um JSON à mão só provaria que a página lê o JSON
/// que a própria página espera.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class ComparacaoE2ETests(AppHostFixture fixture)
{
    /// <summary>
    /// Treino inexistente, usado só como etiqueta da linha semeada: a página não o resolve,
    /// e a fixture apaga as linhas com este id antes de inserir — o banco é persistente e
    /// reexecutar o teste acumularia execuções idênticas na grade.
    /// </summary>
    private static readonly Guid TreinoSentinela = new("0199e2e0-0000-7000-8000-00000000f13a");

    /// <summary>
    /// Janela deliberadamente antiga e improvável: é por ela que o teste encontra a linha
    /// semeada na grade, mesmo com outras execuções da mesma rede na lista.
    /// </summary>
    private static readonly DateOnly JanelaInicio = new(2019, 2, 1);
    private static readonly DateOnly JanelaFim = new(2019, 2, 15);

    private const byte TipoCalculo = 2;

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
    /// O bloco de resultados inteiro (linhas que só existem quando há execução concluída)
    /// renderiza sem erro de servidor. Um parâmetro Radzen inexistente compila e só falha
    /// no render, então este é o único teste que prova que o caminho existe de fato —
    /// inclusive o <c>RadzenTabs</c> aninhado dentro de um <c>RadzenTabsItem</c>.
    /// </summary>
    [Fact]
    public async Task Bloco_de_resultados_renderiza_para_uma_execucao_concluida()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().NotContain("Desculpe, houve um erro",
            "erro de render vira 500 e a página inteira some");
        corpo.Should().Contain("Camada A — previsão do ERP contra previsão do ML");
        corpo.Should().Contain("Camada B — decisão de compra do ERP contra decisão do ML");
        corpo.Should().Contain("Camada C — sugestão do ERP contra aprovação do comprador");

        // As abas aninhadas do drill-down (RadzenTabs dentro de RadzenTabsItem) são o
        // ponto sem precedente no repositório: o rótulo só existe se elas renderizaram.
        corpo.Should().Contain("Drill-down por hierarquia");
        corpo.Should().Contain("Categoria (2)",
            $"a aba aninhada do drill-down precisa existir. Conteúdo real: <<<{corpo}>>>");
    }

    /// <summary>
    /// O estado real de hoje e a asserção mais valiosa desta tela: camada B sem comparação
    /// nenhuma tem de mostrar a explicação <b>no lugar</b> dos números, nunca uma tabela
    /// zerada — que leria como "nenhuma diferença encontrada" entre ERP e ML.
    /// </summary>
    [Fact]
    public async Task Camada_B_nao_utilizavel_mostra_a_explicacao_no_lugar_dos_numeros_de_decisao()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Esta camada não produziu comparação (estado: ForaDoHorizonteMl)");
        corpo.Should().Contain("horizonte de 7 dia(s) do pipeline atual",
            "o horizonte sai dos próprios itens recusados, não de um número fixo na tela");
        corpo.Should().Contain("é ausência de comparação");
        corpo.Should().Contain("É o estado esperado hoje");

        corpo.Should().NotContain("Unidades compradas",
            "a tabela de decisão não pode aparecer quando nada foi comparado");
        corpo.Should().NotContain("Placar item a item",
            "placar 0×0 leria como empate entre ERP e ML");
        corpo.Should().NotContain("Drill-down da decisão");
    }

    /// <summary>
    /// Taxa nula tem estado próprio. Reconciliar a população inteira sem comparar item
    /// nenhum não é 100% de concordância — é ausência do portão.
    /// </summary>
    [Fact]
    public async Task Taxa_de_concordancia_nula_renderiza_como_estado_proprio_e_nunca_como_100()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Taxa de concordância: não apurada");
        corpo.Should().Contain("Exibir 100% aqui leria como comparação bem-sucedida onde não houve comparação nenhuma");
        corpo.Should().NotContain("concordância 100",
            "nulo não é 100% — o badge de concordância não pode aparecer");
    }

    /// <summary>
    /// Cada motivo de saída da população é uma linha própria com o que significa. Lidos
    /// como um balde só, esconderiam de que subconjunto o resultado foi calculado.
    /// </summary>
    [Fact]
    public async Task Contadores_de_exclusao_aparecem_em_linhas_distintas_e_nao_como_um_balde_so()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Sem série no histórico (camada A)");
        corpo.Should().Contain("Janela além do histórico importado (camada A)");
        corpo.Should().Contain("Sem série ou sem eMax (camada B)");
        corpo.Should().Contain("Janela além do histórico importado (camada B)");
        corpo.Should().Contain("Fora do orçamento de SKUs");

        // Contagens deliberadamente improváveis: se as cinco aparecem, elas não foram
        // somadas numa só.
        corpo.Should().Contain("101");
        corpo.Should().Contain("202");
        corpo.Should().Contain("303");
        corpo.Should().Contain("404");
        corpo.Should().Contain("505");
        corpo.Should().Contain("1515", "o KPI de total excluído é a soma das cinco linhas");
    }

    /// <summary>A ressalva metodológica viaja com os números, na mesma página deles.</summary>
    [Fact]
    public async Task Ressalva_de_treino_versus_servico_esta_na_pagina_junto_dos_resultados()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Ressalva metodológica que acompanha estes números");
        corpo.Should().Contain("TREINADO com o preço realizado de cada dia",
            "o texto da ressalva vem do próprio resultado e é renderizado antes dos números");
        corpo.Should().Contain("Camada A — previsão do ERP contra previsão do ML",
            "a ressalva precisa estar na mesma página dos resultados, não numa tela à parte");
    }

    /// <summary>
    /// Média global esconde regressão local (CLAUDE.md §6). Com o WAPE global favorecendo
    /// o ML e uma categoria em que ele perde, a tela precisa dizer as duas coisas.
    /// </summary>
    [Fact]
    public async Task Regressao_por_dimensao_e_sinalizada_mesmo_com_o_resultado_global_favorecendo_o_ML()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("1 de 2 chave(s) com o ML PERDENDO para o ERP nesta dimensão");
        corpo.Should().Contain("A média global está escondendo estes segmentos",
            "o alerta só qualifica assim quando o resultado global favorece o ML");
        corpo.Should().Contain("ML perde aqui?");
    }

    // --- Infra do teste ------------------------------------------------------

    private static readonly SemaphoreSlim Portao = new(1, 1);
    private static string? _corpoComResultado;

    /// <summary>
    /// Semeia a execução concluída, abre a página, seleciona a linha e devolve o corpo
    /// renderizado. Feito uma vez por execução do processo: o resultado semeado é o mesmo
    /// para todas as asserções, e repetir login + carga + clique por teste só somaria
    /// segundos sem cobrir nada a mais.
    /// </summary>
    private async Task<string> ResultadoRenderizadoAsync()
    {
        await Portao.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_corpoComResultado is not null) return _corpoComResultado;

            await fixture.SemearComparacaoConcluidaAsync(
                TreinoSentinela, JanelaInicio, JanelaFim, TipoCalculo, ResultadoJson(),
                TestContext.Current.CancellationToken);

            var page = await fixture.NovaPaginaLogadaAsync();
            await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/comparacao");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.GetByText("Nova execução").First.WaitForAsync(new() { Timeout = 30_000 });

            // A grade traz a janela formatada; é o que distingue a linha semeada de
            // qualquer execução real que a mesma rede tenha.
            var linha = page.Locator("tr", new() { HasText = JanelaInicio.ToString("dd/MM/yyyy") }).First;
            await linha.WaitForAsync(new() { Timeout = 30_000 });
            await linha.Locator("button:has-text('Ver resultado')").First.ClickAsync();

            await page.GetByText("Ressalva metodológica que acompanha estes números")
                      .First.WaitForAsync(new() { Timeout = 30_000 });

            _corpoComResultado = await CorpoNormalizadoAsync(page);
            return _corpoComResultado;
        }
        finally
        {
            Portao.Release();
        }
    }

    /// <summary>Mesmas opções do <c>ComparacaoProcessor</c> — enum como string.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Resultado no estado que a POC produz hoje — camada A com pares pontuados e camada B
    /// <c>ForaDoHorizonteMl</c> —, com três desvios escolhidos para que a tela tenha o que
    /// provar: contagens de exclusão improváveis e distintas entre si, taxa de concordância
    /// nula por reconciliação completa sem comparação, e uma categoria em que o ML perde
    /// enquanto o WAPE global o favorece.
    /// </summary>
    private static string ResultadoJson()
    {
        var metricasZeradas = new ForecastMetrics(0, 0, 0, 0, null);

        var erp = new ArmResult(
            "erp",
            new ForecastMetrics(400, 3.0, 4.2, 0.42, 0.55),
            new Dictionary<string, IReadOnlyDictionary<string, ForecastMetrics>>
            {
                ["Categoria"] = new Dictionary<string, ForecastMetrics>
                {
                    ["Analgésico"] = new(240, 2.4, 3.3, 0.36, 0.48),
                    ["Antialérgico"] = new(160, 1.8, 2.4, 0.20, 0.27),
                },
            });

        // WAPE global do ML abaixo do ERP (0,31 × 0,42) e, ainda assim, pior em
        // "Antialérgico" (0,44 × 0,20): é exatamente o que uma média única apagaria.
        var ml = new ArmResult(
            "lightgbm",
            new ForecastMetrics(400, 2.1, 3.0, 0.31, 0.40),
            new Dictionary<string, IReadOnlyDictionary<string, ForecastMetrics>>
            {
                ["Categoria"] = new Dictionary<string, ForecastMetrics>
                {
                    ["Analgésico"] = new(240, 1.5, 2.2, 0.22, 0.29),
                    ["Antialérgico"] = new(160, 3.4, 4.1, 0.44, 0.52),
                },
            });

        var previsao = new ComparisonResult(
            ParesAvaliados: 400,
            ParesDescartados: 60,
            Unidade: UnidadeMetrica.ErroPorParNaJanela,
            Erp: erp,
            Ml: ml,
            Vitoria: new WinRate(400, 260, 138, 2),
            VitoriaPorDimensao: new Dictionary<string, IReadOnlyDictionary<string, WinRate>>
            {
                ["Categoria"] = new Dictionary<string, WinRate>
                {
                    ["Analgésico"] = new(240, 200, 40, 0),
                    ["Antialérgico"] = new(160, 60, 98, 2),
                },
            },
            Detalhe: []);

        var reconciliacao = new ReconciliacaoResumo(
            Itens: 300,
            Reconciliados: 300,
            Divergentes: 0,
            BracoMlIndeterminado: 0,
            ItensComparados: 0,
            DivergenciaAbsMedia: 0m,
            DivergenciaAbsMaxima: 0m,
            PorCurva: new Dictionary<string, ReconciliacaoPorCurva>
            {
                ["A"] = new("A", 300, 300, 0, 0, 0m, 0m),
            });

        var bracoZerado = new ArmDecisionResult(
            "erp", 0m, 0m, 0m, 0m, 0m, new VendaPerdidaIlustrativa(0m, 0m), metricasZeradas);

        var decisao = new DecisionComparisonResult(
            ItensNaPopulacao: 300,
            ItensComparados: 0,
            ItensDescartadosPorRuptura: 0,
            ItensSemPrecoCompra: 0,
            Utilidade: UtilidadeComparacao.ForaDoHorizonteMl,
            ItensComFallbackEstoqueSeguranca: 0,
            Reconciliacao: reconciliacao,
            DetalheReconciliacao: [],
            ForaDoHorizonteMl:
            [
                new(9001, 21, "SKU-E2E-1", 15, 7),
                new(9001, 21, "SKU-E2E-2", 15, 7),
            ],
            Erp: bracoZerado,
            Ml: bracoZerado with { Nome = "ml" },
            Vitoria: new WinRate(0, 0, 0, 0),
            VitoriaPorDimensao: new Dictionary<string, IReadOnlyDictionary<string, WinRate>>(),
            Detalhe: []);

        var figuras = new HumanOverrideFigures(
            Base: 2000m,
            ComDenominador: 1980m,
            ComOverride: 300m,
            Vetos: 40m,
            Adicoes: 20m,
            AjustesParaCima: 150m,
            AjustesParaBaixo: 90m,
            DesvioRelativoMedioAbsoluto: 0.21,
            DesvioRelativoMedioAssinado: 0.04);

        var intervencao = new HumanOverrideResult(
            ItensNaPopulacao: 2000,
            ItensSemPreco: 2000,
            NaoPonderado: figuras,
            Ponderado: null,
            PorCurva: new Dictionary<string, HumanOverrideResumoCurva>
            {
                ["A"] = new("A", 900, 900, figuras, null),
            });

        var saida = new ComparacaoOutput(
            GeradoEm: DateTimeOffset.UtcNow,
            TreinoJobId: TreinoSentinela,
            ModeloTreinadoAte: new DateOnly(2019, 1, 31),
            TreinoAte: new DateOnly(2019, 2, 1),
            TipoCalculo: TipoCalculo,
            JanelaInicio: JanelaInicio,
            JanelaFim: JanelaFim,
            Sugestoes: 4,
            ItensDaSugestao: 2000,
            ItensCamadaA: 400,
            ItensForaCamadaA: 101,
            ItensForaCamadaAAlemDoHistorico: 202,
            ItensCamadaB: 300,
            ItensForaCamadaB: 303,
            ItensForaCamadaBAlemDoHistorico: 404,
            ItensForaOrcamentoSkus: 505,
            RessalvaTreinoServe: ComparacaoOutput.RessalvaPadraoTreinoServe,
            Previsao: previsao,
            Decisao: decisao,
            Intervencao: intervencao);

        return JsonSerializer.Serialize(saida, Json);
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
