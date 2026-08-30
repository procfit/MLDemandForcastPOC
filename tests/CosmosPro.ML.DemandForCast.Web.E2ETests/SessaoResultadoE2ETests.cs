using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;

using Microsoft.Playwright;
using CosmosPro.ML.DemandForCast.Engine.Mercado;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// A tela de resultado da sessão guiada (F14), lida pelo <b>comprador</b> —
/// <c>/comparacoes/{id}</c> em <c>Concluida</c>. Não confundir com
/// <see cref="ComparacaoE2ETests"/>, que cobre a tela técnica do autor (F13).
///
/// <para>
/// A sessão é <b>semeada</b> já concluída, com os agregados da manchete e o detalhe por item
/// materializados: produzir uma pelo caminho legítimo exigiria importar um ZIP e treinar um
/// modelo dentro do E2E. O <c>ResultadoJson</c> é montado com o tipo real
/// (<c>SessaoResultado</c>) e serializado com as mesmas opções do
/// <c>SessaoResultadoMaterializador</c>, então ele atravessa a apiservice e o
/// <c>ComparacoesApiClient</c> de verdade — um JSON à mão só provaria que a página lê o JSON
/// que ela própria espera.
/// </para>
///
/// <para>
/// O cenário semeado é o <b>desfecho real de hoje</b>: camada B sem decisão nenhuma
/// (cobertura de 30 dias contra horizonte de 7), um item sem preço de compra, um item com a
/// janela além do histórico e SKUs sem cadastro. É o estado em que a tela mais pode mentir.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class SessaoResultadoE2ETests(AppHostFixture fixture)
{
    private const string NomeDaSessao = "Resultado semeado pelo E2E";

    private const int LojaId = 4821;
    private const string SkuComPrevisao = "SKU-E2E-R1";
    private const string SkuSemPreco = "SKU-E2E-R2";

    /// <summary>
    /// Item <b>medido</b> que não vendeu nada: demanda real zero, não nula. É a classe mais
    /// numerosa da população depois que o loader passou a materializar os dias com estoque e
    /// sem venda (88,5% dos pares na Retiro), e é ela que obriga a tela a declarar quanto do
    /// resultado descreve a facilidade de acertar zero. Não confundir com
    /// <see cref="SkuSemPreco"/>, cuja demanda real é <b>nula</b> — "não deu para medir" e
    /// "medi, e foi zero" são afirmações diferentes, e a faixa de giro as separa.
    /// </summary>
    private const string SkuSemVenda = "SKU-E2E-R3";
    private const string NomeDoProduto = "Dipirona E2E 500mg";
    private const long SugestaoId = 88_101;
    private const byte TipoCalculo = 2;
    private const int SkusSemCadastro = 4;

    /// <summary>
    /// Motivo deliberadamente identificável. A frase real vem do Worker (que a monta a partir
    /// do estado da camada B), e um sentinela é o que prova que a página renderiza o texto do
    /// <b>payload</b> em vez de um literal escondido nela.
    /// </summary>
    private const string MotivoSemeado =
        "A compra desta sugestao cobre mais dias do que o metodo de ML consegue prever hoje (motivo semeado pelo E2E).";

    /// <summary>
    /// A asserção mais valiosa desta tela: hoje nenhuma sessão real tem coluna de ML, e o que
    /// aparece no lugar dela precisa ser uma explicação — nunca um traço, um zero ou uma
    /// célula vazia, que leriam como "o ML concordou" ou "o ML mandaria não comprar nada".
    /// </summary>
    [Fact]
    public async Task Manchete_sem_coluna_de_ML_explica_a_ausencia_no_lugar_de_qualquer_numero()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().NotContain("Desculpe, houve um erro",
            "erro de render vira 500 e a página inteira some");

        corpo.Should().Contain("Pelo PBS");
        corpo.Should().Contain("Como teria sido pelo ML");

        corpo.Should().Contain(
            "Não é possível dizer o que o método de ML teria comprado nesta sugestão, e nenhum número foi " +
            "colocado no lugar disso.",
            $"a coluna do ML precisa afirmar a ausência. Conteúdo real: <<<{corpo}>>>");

        corpo.Should().Contain(MotivoSemeado,
            "o motivo em português de comprador vem do resultado gravado, não de um literal na página");

        corpo.Should().Contain("está na área técnica",
            "com previsão disponível, a tela precisa dizer onde ela está em vez de deixar a sessão parecer inútil");
    }

    /// <summary>
    /// A manchete do braço do ERP vale por si, mesmo sem contraparte de ML: é o desfecho real
    /// da compra que foi feita.
    /// </summary>
    [Fact]
    public async Task Manchete_do_erp_mostra_o_capital_parado_e_a_ruptura_observada()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Dinheiro parado na prateleira no fim do período");
        corpo.Should().Contain("3 item(ns) desta sugestão foram acompanhados");
        corpo.Should().Contain("Faltou produto na prateleira em 3 dia(s)");
        // Program.cs fixa pt-BR (CultureInfo.DefaultThreadCurrentCulture), então "P0" para
        // 5/60 sempre sai "8%" — sem o fixador isto oscilaria com a cultura do host.
        corpo.Should().Contain("Só temos a posição de estoque de 8% dos dias do período",
            "zero dia zerado sem snapshot é 'não sabemos', não 'não faltou', e a cultura da tela é pt-BR fixo");
    }

    /// <summary>
    /// As três ressalvas que, ausentes, fariam um número parcial passar por completo:
    /// itens sem preço de compra, janela além do histórico e SKUs sem cadastro.
    /// </summary>
    [Fact]
    public async Task As_ressalvas_que_tornam_os_numeros_parciais_estao_na_pagina()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Este valor em reais está incompleto");
        corpo.Should().Contain("1 item(ns) desta sugestão não têm preço de compra cadastrado");

        corpo.Should().Contain("1 item(ns) com período incompleto");
        corpo.Should().Contain("não devem ser comparados com os demais");

        corpo.Should().Contain("4 itens sem cadastro",
            "sem este aviso o comprador descobriria a ausência como célula vazia na tabela");
    }

    /// <summary>
    /// O botão de baixar o extrator só existe em <c>AguardandoDados</c> (ver
    /// <c>ComparacoesE2ETests.Sessao_nova_em_AguardandoDados_mostra_o_botao_de_baixar_extrator</c>).
    /// Numa sessão <c>Concluida</c> o comprador já enviou o ZIP há muito tempo — o botão
    /// reaparecer aqui seria ruído sem propósito, não uma ação disponível.
    /// </summary>
    [Fact]
    public async Task Sessao_concluida_nao_mostra_o_botao_de_baixar_extrator()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().NotContain("Baixar extrator",
            "o download do extrator só faz sentido em AguardandoDados, não numa sessão já concluída");
    }

    /// <summary>
    /// Bloco fixo, nunca aba: média global esconde regressão local (CLAUDE.md §6). Sem braço
    /// de ML, ele diz que não há o que apontar — e diz também que isso não é empate.
    /// </summary>
    [Fact]
    public async Task Bloco_de_onde_o_ML_foi_pior_e_fixo_e_diz_quando_nao_ha_o_que_apontar()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Onde o ML foi pior");
        corpo.Should().Contain(
            "Na quantidade a comprar não há como apontar onde o ML foi pior nesta sugestão.");
        corpo.Should().Contain("não significa que ele empatou nem que acertou");

        corpo.Should().Contain("o ML errou mais que o seu ERP em 1 de 1 item(ns) medidos",
            "na previsão há o que apontar, e é o que salva o bloco de ficar vazio");
    }

    /// <summary>
    /// A tabela do detalhe renderiza com os nomes copiados na materialização e, onde o braço
    /// de ML não existe, com texto no lugar do número.
    /// </summary>
    [Fact]
    public async Task Tabela_de_itens_renderiza_e_nenhuma_celula_do_ML_vira_traco_ou_zero()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Item por item");
        corpo.Should().Contain("3 itens");
        corpo.Should().Contain(NomeDoProduto, "o nome é copiado na materialização porque o Stage é apagado");
        corpo.Should().Contain(SkuComPrevisao);
        corpo.Should().Contain(SkuSemPreco);

        corpo.Should().Contain("sem cálculo do ML",
            "a célula do braço de ML recebe texto, nunca traço nem zero");
        corpo.Should().Contain("sem preço de compra",
            "sobra em reais nula não pode virar R$ 0,00 — as unidades encalharam");
        corpo.Should().Contain("só o PBS foi calculado",
            "o badge de quem ficou mais perto não pode dizer 'empate' onde não houve cálculo");
        corpo.Should().Contain("período incompleto",
            "a linha com janela além do histórico precisa estar marcada na tabela");
    }

    /// <summary>
    /// A área técnica traz previsão contra previsão com o denominador visível, e diz por que
    /// venda perdida em reais não aparece em lugar nenhum desta tela.
    /// </summary>
    [Fact]
    public async Task Area_tecnica_traz_previsao_contra_previsao_e_recusa_a_venda_perdida_em_reais()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Previsão de venda: o seu ERP contra o ML");
        corpo.Should().Contain("Apurado sobre 1 de 3 item(ns) da sugestão",
            "a métrica não fala da população inteira, e a tela precisa dizer sobre quanto ela fala");
        corpo.Should().Contain("WAPE");
        corpo.Should().Contain("Abertura por curva e por loja");
        corpo.Should().Contain("ML perde aqui?");

        corpo.Should().Contain("Venda perdida em reais não é exibida em lugar nenhum desta tela");
        corpo.Should().Contain("o método que prevê mais alto compra mais",
            "a premissa que torna a estimativa circular precisa estar ao lado da recusa");
    }

    /// <summary>
    /// A camada analítica da tabela de itens: filtros combináveis, totalizadores do recorte e
    /// exportação. É o pedido do item 9 do documento do Julio, e o que transforma a tela de uma
    /// listagem de milhares de SKUs em ferramenta de análise.
    ///
    /// <para>
    /// Asserção por <b>texto</b> e não por <c>data-test</c> porque este caso lê o corpo em
    /// cache junto dos demais (<see cref="CorpoNormalizadoAsync"/> devolve texto, onde atributo
    /// nenhum aparece) — e o texto só existe se os componentes renderizaram, que é o que se
    /// quer provar. O comportamento dos filtros e dos totais é afirmado com números em
    /// <c>FiltrosDosItensIntegrationTests</c>, contra SQL Server de verdade.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Tabela_de_itens_traz_filtros_totalizadores_e_exportacao()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("Filtros");
        corpo.Should().Contain("Totais do recorte");
        corpo.Should().Contain("Itens analisados");
        corpo.Should().Contain("Exportar para Excel");
        corpo.Should().Contain("Categoria", "a coluna de classificação do produto entrou na tabela");
    }

    /// <summary>
    /// A declaração de que parte da população não vendeu nada, na tela do <b>comprador</b>.
    /// Ele lê a comparação sem ver o placar técnico, mas lê as sobras — e se a maior parte dos
    /// itens medidos não teve venda, o resultado descreve sobretudo a facilidade de acertar
    /// zero. A frase manda o leitor ao detalhe por faixa de giro, que separa em vez de somar.
    /// </summary>
    [Fact]
    public async Task Manchete_declara_quantos_itens_nao_venderam_no_periodo()
    {
        var corpo = await ResultadoRenderizadoAsync();

        corpo.Should().Contain("não venderam nada no período");
        corpo.Should().Contain("faixa de giro");
    }

    /// <summary>
    /// O botão de baixar o ZIP enviado, e a rota por trás dele respondendo <b>autenticada</b>.
    /// Sem esse download, repetir uma comparação sobre o mesmo envio depende de o cliente
    /// ainda ter o arquivo no disco — e cada import substitui o Stage inteiro da rede, então
    /// uma extração nova traz outra janela de vendas e os números das duas execuções não se
    /// comparam.
    ///
    /// <para>
    /// Único caso desta classe que abre página própria, e não por capricho: os demais leem o
    /// corpo em <b>texto</b> (<see cref="CorpoNormalizadoAsync"/>), onde nenhum atributo
    /// aparece. Asserção por <c>data-test</c> em vez de pelo rótulo porque o rótulo é redação
    /// e muda; o que não pode sumir é a oferta do download quando a sessão tem envio.
    /// </para>
    ///
    /// <para>
    /// <b>404 é o resultado esperado aqui, e é ele que carrega a asserção que importa.</b> A
    /// carga é semeada sem objeto no MinIO (ver <c>SemearCargaNaSessaoAsync</c>), então a rota
    /// chega ao armazenamento e não encontra o arquivo. O que se afirma é que ela <b>chega
    /// lá</b> — ou seja, que autenticação e resolução de rede funcionam num endpoint HTTP
    /// comum, fora de componente Razor. Esse é exatamente o caminho que quebrou em produção
    /// com 500: o <c>IRedeContext</c> lia o estado de autenticação do Blazor, que <b>lança</b>
    /// fora do escopo de um componente. Os testes de API não passavam por aqui (a apiservice
    /// não tem esse problema) e a asserção anterior, só de presença do botão, nunca clicava.
    /// A integridade dos bytes é afirmada onde há upload de verdade, em
    /// <c>SessaoDownloadIntegrationTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sessao_com_envio_oferece_o_download_e_a_rota_responde_autenticada()
    {
        // Garante a semeadura (sessão + carga) reusando o mesmo caminho dos outros casos.
        await ResultadoRenderizadoAsync();

        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            var baseUrl = fixture.WebfrontendUrl.TrimEnd('/');
            await page.GotoAsync($"{baseUrl}/comparacoes/{_sessaoId}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var botao = page.Locator("[data-test='baixar-zip-enviado']");
            await botao.First.WaitForAsync(new() { Timeout = 60_000 });
            (await botao.CountAsync()).Should().Be(1);

            var resposta = await page.GotoAsync($"{baseUrl}/comparacoes/{_sessaoId}/dados/download");

            resposta.Should().NotBeNull();
            resposta!.Status.Should().Be(404,
                "a rota tem de alcançar o armazenamento; 500 aqui significa que ela caiu antes, "
                + "ao resolver usuário ou rede — foi assim que o caminho quebrou em produção");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    // --- Infra do teste ------------------------------------------------------

    private static readonly SemaphoreSlim Portao = new(1, 1);
    private static string? _corpo;

    /// <summary>
    /// Sessão semeada por <see cref="ResultadoRenderizadoAsync"/>. Exposta para o único caso
    /// que precisa abrir a página por conta própria, em vez de reler o corpo em cache.
    /// </summary>
    private static Guid _sessaoId;

    /// <summary>
    /// Semeia a sessão, abre a página e devolve o corpo renderizado. Feito uma vez por
    /// execução do processo: o resultado é o mesmo para todas as asserções, e repetir login e
    /// navegação por teste só somaria segundos sem cobrir nada a mais.
    /// </summary>
    /// <summary>
    /// O filtro "só com alerta de mercado" na tabela de itens, clicado de verdade.
    ///
    /// <para>
    /// Abre página própria, como o caso do download, porque os demais leem o corpo em texto e
    /// aqui é preciso <b>interagir</b>. O que se afirma é a cadeia inteira: o checkbox manda
    /// <c>somenteComAlerta</c>, a apiservice filtra pela mesma cláusula que alimenta os
    /// totais, e a tabela encolhe. Um dos três itens semeados tem alerta; os outros dois não
    /// têm dado de mercado nenhum — e ausência de medição não pode entrar no recorte de
    /// alerta, senão a lista se enche de itens que ninguém mediu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_comprador_filtra_a_tabela_por_alerta_de_mercado()
    {
        await ResultadoRenderizadoAsync();

        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            var baseUrl = fixture.WebfrontendUrl.TrimEnd('/');
            await page.GotoAsync($"{baseUrl}/comparacoes/{_sessaoId}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var linhas = page.Locator("[data-test='totalizadores-itens']");
            await linhas.First.WaitForAsync(new() { Timeout = 60_000 });

            // O contador de cobertura declara sobre quantos itens do recorte há medição.
            // Sem ele, metade da tabela com travessão passa por "a rede vai bem em tudo".
            var cobertura = page.Locator("[data-test='cobertura-de-mercado']");
            (await cobertura.CountAsync()).Should().Be(1,
                "a sessão semeia 1 item com medição de 3, então a declaração precisa aparecer");
            (await cobertura.InnerTextAsync()).Should().Contain("1 de 3 itens");

            var antes = await page.Locator("[data-test='tabela-itens'] tbody tr").CountAsync();
            antes.Should().BeGreaterThan(1, "a sessão semeia três itens");

            var checkbox = page.Locator("[data-test='filtro-somente-com-alerta']");
            await checkbox.First.WaitForAsync(new() { Timeout = 30_000 });
            await checkbox.First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Espera a tabela reagir: o recalculo passa pelo servidor.
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll(\"[data-test='tabela-itens'] tbody tr\").length === 1",
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            var depois = await page.Locator("[data-test='tabela-itens'] tbody tr").CountAsync();
            depois.Should().Be(1, "só um dos três itens semeados tem alerta de mercado");

            var corpo = await page.Locator("body").InnerTextAsync();
            corpo.Should().Contain("Possível perda por ruptura",
                "o alerta aparece com texto de comprador, não com o nome interno");

            // A ressalva de preço é SEMPRE VISÍVEL, e não só num balão de ajuda: quem lê
            // "sem causa aparente" conclui que o sistema conferiu preço também. Preço é a
            // hipótese 2 da regra de negócio e não é testável com esta fonte -- a IQVIA publica
            // um preço de referência idêntico para a rede e para os concorrentes. Esconder a
            // ressalva deixaria o comprador agir sobre conclusão que o sistema não sustenta.
            var ressalva = page.Locator("[data-test=\'ressalva-preco-mercado\']");
            (await ressalva.CountAsync()).Should().Be(1);
            (await ressalva.InnerTextAsync()).Should().Contain("não preço");

            // E a célula do alerta carrega a explicação por linha num atributo do DOM, que
            // sobrevive sem hover.
            // Ancorado no data-test do próprio alerta, e não em "o último span com title": a
            // versão anterior deste teste quebrou ao mover a coluna de lugar, porque passou a
            // ler o title da célula do índice.
            var titulo = await page
                .Locator("[data-test=\'alerta-de-mercado\']")
                .First.GetAttributeAsync("title");
            titulo.Should().Contain("Preço não foi verificado");

        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task<string> ResultadoRenderizadoAsync()
    {
        await Portao.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_corpo is not null) return _corpo;

            _sessaoId = await fixture.SemearSessaoConcluidaAsync(
                NomeDaSessao,
                SugestaoId,
                new DateTime(2026, 7, 1, 9, 30, 0),
                TipoCalculo,
                SkusSemCadastro,
                ResultadoJson(),
                Itens(),
                TestContext.Current.CancellationToken);

            // Uma sessão concluída de verdade sempre veio de um envio; semear sem a carga
            // deixaria a tela sob teste num estado que produção não produz.
            await fixture.SemearCargaNaSessaoAsync(
                _sessaoId, "extracao-pbs-semeada.zip", TestContext.Current.CancellationToken);

            var page = await fixture.NovaPaginaLogadaAsync();
            try
            {
                await page.GotoAsync($"{fixture.WebfrontendUrl.TrimEnd('/')}/comparacoes/{_sessaoId}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                // O conteúdo só aparece depois do circuito interativo assumir (o carregamento
                // é pulado no prerender de propósito), e a tabela do detalhe ainda faz uma
                // segunda ida ao servidor pela primeira página. As esperas são por TEXTO:
                // RadzenText não emite h1/h4, então seletor de heading não é âncora confiável
                // nesta aplicação.
                await page.GetByText("Pelo PBS").First.WaitForAsync(new() { Timeout = 60_000 });

                // A espera pela tabela precisa de um texto que exista SÓ nela. O nome do
                // produto não serve: ele aparece também no bloco de "onde o ML foi pior", que
                // vem do agregado e chega antes — e o corpo lido em seguida pegava a grade
                // ainda em "Esta comparação não deixou nenhum item para detalhar". O segundo
                // SKU só existe no detalhe paginado.
                try
                {
                    await page.GetByText(SkuSemPreco).First.WaitForAsync(new() { Timeout = 30_000 });
                }
                catch (TimeoutException)
                {
                    // Não relança: o corpo é lido de qualquer modo, para a falha do caso vir
                    // com a página real em vez de um timeout sem conteúdo. É essa mensagem
                    // que distingue "a tabela não carregou" de "carregou e falta o texto".
                }

                _corpo = await CorpoNormalizadoAsync(page);
                return _corpo;
            }
            finally
            {
                // A página fica aberta até aqui de propósito (a leitura do corpo depende
                // dela), mas não pode sobreviver ao caso: cada aba viva é um circuito Blazor
                // fazendo poll contra o mesmo AppHost dos outros cenários E2E.
                await page.CloseAsync();
            }
        }
        finally
        {
            Portao.Release();
        }
    }

    /// <summary>Mesmas opções do <c>SessaoResultadoMaterializador</c> — enum como texto.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ResultadoJson()
    {
        var resultado = new SessaoResultado(
            GeradoEm: DateTimeOffset.UtcNow,
            ComparacaoPbsId: Guid.CreateVersion7(),
            SugestaoDataHora: new DateTime(2026, 7, 1, 9, 30, 0),
            TipoCalculo: TipoCalculo,
            ItensAvaliados: 3,
            VendidoNaJanelaUnidades: 60m,
            Pbs: new BracoDaSessao(
                CompraUnidades: 120m, SobraUnidades: 75m, SobraValor: 192.5m),
            // Nulo é o desfecho esperado hoje: a cobertura de 30 dias excede o horizonte de 7.
            Confronto: null,
            MotivoMlIndisponivel: MotivoSemeado,
            ItensComDecisaoMl: 0,
            ItensComPrevisaoMl: 1,
            UtilidadeDecisaoMl: UtilidadeComparacao.ForaDoHorizonteMl,
            // 5 dias com snapshot em 60 dias-item: 8% de cobertura, e a tela tem de dizer que
            // a ruptura observada é o mínimo, não o total.
            Ruptura: new RupturaObservada(
                ItensComDiaSemEstoque: 1, DiasSemEstoque: 3,
                DiasComSnapshot: 5, DiasNaJanela: 60),
            ItensComJanelaAlemDoHistorico: 1,
            ItensSemPrecoCompra: 1,
            SkusSemCadastro: SkusSemCadastro,
            RessalvaTreinoServe: ComparacaoOutput.RessalvaPadraoTreinoServe);

        return JsonSerializer.Serialize(resultado, Json);
    }

    /// <summary>
    /// Duas linhas, cada uma provando um estado que a tela pode falsificar: a primeira tem
    /// previsão do ML (e o ML errou mais que o ERP nela), a segunda não tem preço de compra e
    /// tem a janela além do histórico. Nenhuma tem braço de decisão do ML.
    /// </summary>
    private static IReadOnlyList<ComparacaoSessaoItem> Itens() =>
    [
        new()
        {
            LojaId = LojaId,
            Sku = SkuComPrevisao,
            NomeProduto = NomeDoProduto,
            Curva = "A",
            CompraSugeridaPbs = 100m,
            CompraSugeridaMl = null,
            VendidoNaJanela = 60m,
            DemandaDiaPbs = 2m,
            DemandaDiaMl = 2.4m,
            DemandaDiaReal = 2m,
            SobraPbsUnidades = 55m,
            SobraMlUnidades = null,
            SobraPbsValor = 192.5m,
            SobraMlValor = null,
            JanelaAlemDoHistorico = false,
            // Único item com dado de mercado, de propósito: o filtro tem de reduzir a
            // tabela, e os itens sem dado têm de ficar de fora dele.
            MercadoMes = new DateOnly(2025, 6, 1),
            MercadoBrick = "528-RJ VOLTA REDONDA RETIRO",
            MercadoUnidadesRede = 12m,
            MercadoUnidadesConcorrentes = 988m,
            MercadoIndiceDesempenho = 0.12m,
            MercadoDiasSemEstoque = 3,
            MercadoAlerta = MercadoAlertas.Ruptura,
        },
        new()
        {
            LojaId = LojaId,
            Sku = SkuSemPreco,
            NomeProduto = "Paracetamol E2E 750mg",
            Curva = "C",
            CompraSugeridaPbs = 20m,
            CompraSugeridaMl = null,
            VendidoNaJanela = 0m,
            DemandaDiaPbs = 1m,
            DemandaDiaMl = null,
            DemandaDiaReal = null,
            SobraPbsUnidades = 20m,
            SobraMlUnidades = null,
            SobraPbsValor = null,
            SobraMlValor = null,
            JanelaAlemDoHistorico = true,
        },
        new()
        {
            LojaId = LojaId,
            Sku = SkuSemVenda,
            NomeProduto = "Loratadina E2E 10mg",
            Curva = "E",
            CompraSugeridaPbs = 0m,
            // Braço de ML todo nulo, de propósito: o cenário desta classe é "nenhum item com
            // os dois métodos calculados", e preenchê-lo aqui destruiria isso. O que este item
            // contribui é apenas DemandaDiaReal = 0 — demanda medida e igual a zero, que é o
            // que a faixa de giro usa para a declaração de cobertura.
            CompraSugeridaMl = null,
            VendidoNaJanela = 0m,
            DemandaDiaPbs = 0m,
            DemandaDiaMl = null,
            DemandaDiaReal = 0m,
            SobraPbsUnidades = 3m,
            SobraMlUnidades = null,
            SobraPbsValor = 12.5m,
            SobraMlValor = null,
            JanelaAlemDoHistorico = false,
        },
    ];

    /// <summary>
    /// Texto do corpo com espaços colapsados: o Razor quebra as frases longas em várias
    /// linhas e o <c>textContent</c> preserva as quebras — asserção sobre a frase inteira
    /// falharia por indentação, não por conteúdo ausente.
    /// </summary>
    private static async Task<string> CorpoNormalizadoAsync(IPage page)
    {
        var corpo = await page.TextContentAsync("body") ?? "";
        return Regex.Replace(corpo, @"\s+", " ").Trim();
    }
}
