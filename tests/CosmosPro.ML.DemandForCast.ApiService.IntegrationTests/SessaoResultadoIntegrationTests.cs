using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Forecasting.Comparison;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using CosmosPro.ML.DemandForCast.Worker.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// A materialização do resultado da sessão: uma linha por item da sugestão do ERP e os
/// agregados da manchete, gravados no fim da fase de comparação — enquanto o Stage que os
/// originou ainda existe.
///
/// <para>
/// <b>Por que este teste não treina nada.</b> O que está sob prova é a travessia
/// Stage → linhas gravadas, não o modelo: o resultado da comparação é semeado com os tipos
/// reais (<see cref="ComparacaoOutput"/> e as três camadas), do mesmo jeito que o E2E da
/// tela do comparativo faz. Isso deixa o caso rodar em segundos e, mais importante, permite
/// fixar a camada B <b>vazia</b> — o desfecho real de hoje, com cobertura de 30 dias contra
/// horizonte de 7 —, que é justamente o cenário em que um zero gravado mentiria.
/// </para>
///
/// <para>
/// <b>As duas redes têm o MESMO código de loja e os MESMOS SKUs de propósito.</b>
/// <c>LojaId</c> e <c>Sku</c> são códigos de ERP e colidem entre inquilinos: é essa colisão
/// que faz um <c>JOIN</c> sem <c>RedeId</c> trazer o nome do produto e a venda da outra
/// rede sem quebrar nada. Com nomes e volumes diferentes nas duas, o vazamento aparece como
/// valor errado em vez de passar calado.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class SessaoResultadoIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "sessao-resultado";
    private const string SlugOutraRede = "sessao-resultado-outra";

    /// <summary>Rede da sessão cuja materialização falha sempre. Não recebe import.</summary>
    private const string SlugIlegivel = "sessao-resultado-ilegivel";

    private const int LojaId = 9821;
    private const string SkuComPrevisao = "RES-A";
    private const string SkuSemPrevisao = "RES-B";
    private const long SugestaoId = 7501;

    /// <summary>"Dias de Reposição", o método declarado na sessão.</summary>
    private const byte TipoCalculo = 2;

    /// <summary>
    /// Cobertura de 30 dias: excede o horizonte de 7 do pipeline, que é o motivo real pelo
    /// qual a camada B não decide nada hoje.
    /// </summary>
    private const short DiasEstoque = 30;

    /// <summary>Venda diária da rede desta sessão. Constante para a soma da janela ser exata.</summary>
    private const decimal VendaDiaria = 2m;

    /// <summary>Venda diária da outra rede: se um JOIN esquecer o RedeId, o número aparece.</summary>
    private const decimal VendaDiariaDaOutraRede = 50m;

    private const decimal CompraSugerida = 100m;

    /// <summary>Compra do segundo item, que não tem preço cadastrado nem venda na cobertura.</summary>
    private const decimal CompraSemPreco = 20m;

    private const decimal EstoqueSaldo = 10m;
    private const decimal PedidosPendentes = 5m;
    private const decimal PrecoCompra = 3.5m;
    private const int SkusSemCadastro = 3;

    private const string NomeDoProduto = "Produto Resultado A";
    private const string NomeNaOutraRede = "Produto Da Outra Rede";

    private static readonly DateOnly Inicio = new(2026, 5, 1);
    private static readonly DateOnly Fim = new(2026, 8, 31);
    private static readonly DateTime SugestaoDataHora = new(2026, 7, 1, 9, 30, 0);
    private static readonly DateOnly DiaDaSugestao = new(2026, 7, 1);

    private static readonly DateOnly UltimaDataTreinada = new(2026, 6, 30);

    /// <summary>Previsão do ML para o item que entrou na camada A, em unidades/dia.</summary>
    private const double DemandaDiaMl = 2.4;
    private const double DemandaDiaReal = 2.0;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Cenario? _cenario;

    private sealed record Cenario(int RedeId, Guid SessaoId, Guid ComparacaoPbsId, SessaoView Sessao);

    /// <summary>
    /// Venda esperada na cobertura: a janela é <see cref="DiasEstoque"/> dias a partir do dia
    /// da sugestão, a mesma que a camada B pontuaria.
    /// </summary>
    private const decimal VendidoEsperado = VendaDiaria * DiasEstoque;

    [Fact]
    public async Task Sessao_concluida_materializa_uma_linha_por_item_avaliado_pelo_erp()
    {
        var cenario = await CenarioAsync();

        cenario.Sessao.Status.Should().Be("Concluida",
            because: cenario.Sessao.MensagemErro ?? cenario.Sessao.MotivoInviabilidade ?? "sem motivo registrado");

        var itens = await ItensAsync(cenario.SessaoId);

        itens.Should().HaveCount(2, "a população é a da sugestão do ERP: nem alargada, nem encolhida");
        itens.Select(i => i.Sku).Should().BeEquivalentTo([SkuComPrevisao, SkuSemPrevisao]);
        itens.Should().OnlyContain(i => i.LojaId == LojaId);
    }

    /// <summary>
    /// O defeito que esta task existiu para consertar. A camada B não decidiu por nenhum item
    /// (cobertura de 30 dias contra horizonte de 7), e as colunas do braço de ML têm de sair
    /// <b>nulas</b> no banco — zero diria ao comprador que o ML mandaria não comprar nada.
    /// </summary>
    [Fact]
    public async Task Item_sem_decisao_do_ml_fica_nulo_no_banco_e_nunca_zero()
    {
        var cenario = await CenarioAsync();
        var itens = await ItensAsync(cenario.SessaoId);

        itens.Should().OnlyContain(i => i.CompraSugeridaMl == null,
            "a coluna é anulável justamente para separar 'não calculamos' de 'o ML disse zero'");
        itens.Should().OnlyContain(i => i.SobraMlUnidades == null && i.SobraMlValor == null);
    }

    /// <summary>
    /// A camada A produz valores dentro do horizonte dela, então a previsão do ML pode existir
    /// exatamente onde a decisão não existe. Item fora da camada A não recebe zero: recebe
    /// nulo, e a demanda do próprio ERP continua gravada nos dois.
    /// </summary>
    [Fact]
    public async Task Previsao_da_camada_a_e_gravada_item_a_item_sem_contaminar_quem_ficou_fora()
    {
        var cenario = await CenarioAsync();
        var itens = await ItensAsync(cenario.SessaoId);

        var comPrevisao = itens.Single(i => i.Sku == SkuComPrevisao);
        comPrevisao.DemandaDiaMl.Should().Be(2.4m);
        comPrevisao.DemandaDiaReal.Should().Be(2m);

        var semPrevisao = itens.Single(i => i.Sku == SkuSemPrevisao);
        semPrevisao.DemandaDiaMl.Should().BeNull("este item não entrou na camada A");
        semPrevisao.DemandaDiaReal.Should().BeNull(
            "zero afirmaria que ele não vendeu nada por dia, o que é medição e não ausência");

        itens.Should().OnlyContain(i => i.DemandaDiaPbs == 2m,
            "a previsão do próprio ERP vem da linha da sugestão e não depende de camada nenhuma");
    }

    /// <summary>
    /// A sobra do braço do ERP é a conta do <see cref="SobraCalculator"/> sobre a venda real da
    /// cobertura, com os pedidos pendentes na posição — a mesma regra da camada B.
    /// </summary>
    [Fact]
    public async Task Sobra_do_erp_bate_com_o_SobraCalculator_sobre_a_venda_real_da_janela()
    {
        var cenario = await CenarioAsync();
        var item = (await ItensAsync(cenario.SessaoId)).Single(i => i.Sku == SkuComPrevisao);

        var esperada = SobraCalculator.Calcular(
            comprado: CompraSugerida,
            estoqueInicial: EstoqueSaldo,
            pedidosPendentes: PedidosPendentes,
            vendido: VendidoEsperado,
            precoCompra: PrecoCompra);

        item.VendidoNaJanela.Should().Be(VendidoEsperado,
            "a janela é a cobertura do item, contada do dia da sugestão");
        item.CompraSugeridaPbs.Should().Be(CompraSugerida);
        item.SobraPbsUnidades.Should().Be(esperada.Unidades);
        item.SobraPbsValor.Should().Be(esperada.Valor);
        item.SobraPbsUnidades.Should().Be(55m, "100 comprados + 10 em estoque + 5 pendentes − 60 vendidos");
    }

    /// <summary>
    /// Item sem nenhuma venda na cobertura precisa aparecer com zero em vez de desaparecer da
    /// tabela: compra que não vendeu nada é o caso mais interessante da tela. E sem
    /// <c>PrecoCompra</c> a sobra em reais sai <b>nula</b> no banco, não zero: as 20 unidades
    /// encalharam, e só o valor delas é que é desconhecido.
    /// </summary>
    [Fact]
    public async Task Item_sem_venda_e_sem_preco_aparece_sem_valor_em_vez_de_desaparecer()
    {
        var cenario = await CenarioAsync();
        var item = (await ItensAsync(cenario.SessaoId)).Single(i => i.Sku == SkuSemPrevisao);

        item.VendidoNaJanela.Should().Be(0m);
        item.SobraPbsUnidades.Should().Be(20m, "20 comprados, nada em estoque, nada vendido");
        item.SobraPbsValor.Should().BeNull("sem preço de compra cadastrado não há valor a afirmar");
    }

    /// <summary>
    /// A cobertura das duas linhas termina dentro do histórico importado, então a marca por
    /// linha sai limpa — e é ela, não só o contador da manchete, que permite ao comprador
    /// saber qual linha da tabela ele pode conferir contra a memória dele.
    /// </summary>
    [Fact]
    public async Task Nenhuma_linha_e_marcada_quando_a_cobertura_cabe_no_historico_importado()
    {
        var cenario = await CenarioAsync();
        var itens = await ItensAsync(cenario.SessaoId);

        itens.Should().OnlyContain(i => !i.JanelaAlemDoHistorico,
            "a cobertura de 30 dias a partir de 01/07 termina antes do fim das vendas importadas");
        (await ResultadoAsync(cenario.SessaoId)).ItensComJanelaAlemDoHistorico.Should().Be(0);
    }

    /// <summary>
    /// Multi-inquilino, a invariante dura. As duas redes têm o mesmo <c>LojaId</c> e os mesmos
    /// SKUs, com nome de produto e volume de venda diferentes: um <c>JOIN</c> sem
    /// <c>RedeId</c> traria o nome e a venda da outra rede sem erro nenhum aparecer.
    /// </summary>
    [Fact]
    public async Task Linhas_carregam_o_cadastro_e_a_venda_da_rede_da_sessao_e_nao_da_outra()
    {
        var cenario = await CenarioAsync();
        var item = (await ItensAsync(cenario.SessaoId)).Single(i => i.Sku == SkuComPrevisao);

        item.NomeProduto.Should().Be(NomeDoProduto);
        item.NomeProduto.Should().NotBe(NomeNaOutraRede);
        item.Curva.Should().Be("A");

        item.VendidoNaJanela.Should().Be(VendidoEsperado,
            $"a outra rede vende {VendaDiariaDaOutraRede}/dia no mesmo SKU e na mesma loja");
    }

    /// <summary>
    /// A manchete tem de ser legível mesmo sem braço de ML: o confronto sai nulo e no lugar
    /// dele vai o motivo em português de comprador, para a tela explicar em vez de mostrar
    /// células vazias.
    /// </summary>
    [Fact]
    public async Task Agregado_da_manchete_diz_que_a_decisao_do_ml_nao_pode_ser_calculada()
    {
        var cenario = await CenarioAsync();
        var resultado = await ResultadoAsync(cenario.SessaoId);

        resultado.ItensAvaliados.Should().Be(2);
        resultado.Confronto.Should().BeNull("nenhum item teve os dois braços");
        resultado.ItensComDecisaoMl.Should().Be(0);
        resultado.ItensComPrevisaoMl.Should().Be(1);
        resultado.UtilidadeDecisaoMl.Should().Be(UtilidadeComparacao.ForaDoHorizonteMl);
        resultado.MotivoMlIndisponivel.Should().NotBeNullOrWhiteSpace();
        resultado.MotivoMlIndisponivel.Should().Contain("prever");

        resultado.Pbs.SobraUnidades.Should().Be(75m, "55 do primeiro item mais 20 do segundo");
        resultado.Pbs.SobraValor.Should().Be(192.5m, "só o primeiro item tem preço de compra");
        resultado.ItensSemPrecoCompra.Should().Be(1,
            "a figura em R$ acima está subestimada, e sem este número a tela a mostraria como completa");
        resultado.VendidoNaJanelaUnidades.Should().Be(VendidoEsperado);
        resultado.ComparacaoPbsId.Should().Be(cenario.ComparacaoPbsId);
        resultado.RessalvaTreinoServe.Should().NotBeNullOrWhiteSpace(
            "a ressalva metodológica viaja com os números, não só na documentação");
    }

    /// <summary>
    /// Ruptura observada, com a cobertura do snapshot ao lado. Os dois números são
    /// inseparáveis: um dia zerado em cinco snapshots dentro de uma janela de sessenta
    /// dias-item não autoriza dizer que houve ruptura em 1/60 do período — autoriza dizer que
    /// só sabemos de cinco deles. E a outra rede tem exatamente os mesmos snapshots na mesma
    /// loja e no mesmo SKU, então um <c>JOIN</c> sem <c>RedeId</c> dobraria estes números.
    /// </summary>
    [Fact]
    public async Task Ruptura_observada_vem_com_a_cobertura_do_snapshot_e_escopada_na_rede()
    {
        var cenario = await CenarioAsync();
        var ruptura = (await ResultadoAsync(cenario.SessaoId)).Ruptura;

        ruptura.DiasSemEstoque.Should().Be(1, "um único snapshot da cobertura está zerado");
        ruptura.ItensComDiaSemEstoque.Should().Be(1);
        ruptura.DiasComSnapshot.Should().Be(5, "o segundo item não tem snapshot nenhum");
        ruptura.DiasNaJanela.Should().Be(2 * DiasEstoque, "dois itens com cobertura de 30 dias cada");
    }

    /// <summary>
    /// O aviso do manifesto atravessa import, treino e comparação para chegar à tela: sem
    /// isso o comprador descobriria os itens sem cadastro como célula vazia.
    /// </summary>
    [Fact]
    public async Task Aviso_de_skus_sem_cadastro_chega_ao_resultado_materializado()
    {
        var cenario = await CenarioAsync();
        var resultado = await ResultadoAsync(cenario.SessaoId);

        resultado.SkusSemCadastro.Should().Be(SkusSemCadastro);
    }

    /// <summary>
    /// Materializar duas vezes duplicaria o detalhe que o comprador pagina. O claim do
    /// <c>SessaoWorker</c> não impede duas reclamações da mesma sessão — <c>UPDLOCK</c> morre
    /// com a consulta —, então quem garante é o <c>UPDATE</c> final com
    /// <c>WHERE ... AND Status = &lt;fase reclamada&gt;</c> dentro da mesma transação das
    /// linhas: aqui a segunda passagem é reproduzida chamando o materializador com o retrato
    /// que um segundo processo teria lido.
    /// </summary>
    [Fact]
    public async Task Materializacao_nao_acontece_duas_vezes_para_a_mesma_sessao()
    {
        var ct = TestContext.Current.CancellationToken;
        var cenario = await CenarioAsync();

        var antes = await ItensAsync(cenario.SessaoId);

        var materializador = await MaterializadorAsync(ct);
        var segundaTentativa = await materializador.MaterializarAsync(
            new SessaoEmAndamento(
                Id: cenario.SessaoId,
                RedeId: cenario.RedeId,
                // Comparando: é o que um segundo processo teria lido antes de o primeiro
                // concluir. A sessão já está em Concluida, então o WHERE otimista recusa.
                Status: SessaoStatus.Comparando,
                CargaStageId: null,
                TreinoJobId: null,
                ComparacaoPbsId: cenario.ComparacaoPbsId,
                SugestaoDataHora: SugestaoDataHora,
                SugestaoTipoCalculo: TipoCalculo,
                SkusSemCadastro: SkusSemCadastro),
            ct);

        segundaTentativa.Should().BeFalse("a sessão não estava mais na fase reclamada");

        var depois = await ItensAsync(cenario.SessaoId);
        depois.Should().HaveCount(antes.Count, "a transação recusada não pode deixar linha atrás de si");
    }

    /// <summary>
    /// O outro lado da retentativa: a materialização falha <b>sempre</b> (o resultado da
    /// comparação está ilegível), e o limite de tentativas do <c>SessaoWorker</c> tem de levar
    /// a sessão a <c>Falha</c> com uma mensagem que diz ao comprador o que fazer — em vez de
    /// deixá-la sendo repescada para sempre com o comprador olhando um spinner.
    ///
    /// <para>
    /// Este caso não passa pelo Stage: a leitura do resultado da comparação acontece antes, e
    /// é justamente ela que quebra. Por isso a rede aqui não precisa de import.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Materializacao_que_falha_sempre_termina_em_falha_com_motivo_para_o_comprador()
    {
        var ct = TestContext.Current.CancellationToken;

        var redeId = await EnsureRedeAsync("Rede Sessao Resultado Ilegivel", SlugIlegivel);
        await using var db = await AbrirEngineAsync(ct);
        var agora = DateTimeOffset.UtcNow;

        var comparacao = new ComparacaoPbs
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = ComparacaoPbsStatus.Concluido,
            DataAgendamento = agora,
            DataInicioProcessamento = agora,
            DataConclusao = agora,
            TreinoJobId = Guid.CreateVersion7(),
            JanelaInicio = DiaDaSugestao,
            JanelaFim = DiaDaSugestao,
            TipoCalculo = TipoCalculo,
            // JSON truncado: quebra na desserialização a cada tentativa, sem depender de
            // indisponibilidade de banco — é a falha permanente que o limite existe para parar.
            ResultadoJson = "{\"geradoEm\":\"2026-07-01T00:00:00+00:00\"",
        };
        db.ComparacoesPbs.Add(comparacao);

        var sessaoId = Guid.CreateVersion7();
        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = sessaoId,
            RedeId = redeId,
            Nome = "Materializacao ilegivel",
            Status = SessaoStatus.Comparando,
            CriadoEm = agora,
            AtualizadoEm = agora,
            SugestaoId = SugestaoId,
            SugestaoDataHora = SugestaoDataHora,
            SugestaoTipoCalculo = TipoCalculo,
            ComparacaoPbsId = comparacao.Id,
        });

        await db.SaveChangesAsync(ct);

        var sessao = await AguardarTerminoAsync(sessaoId, redeId);

        sessao.Status.Should().Be("Falha", "a falha é permanente e o limite de tentativas foi esgotado");
        sessao.MensagemErro.Should().NotBeNullOrWhiteSpace();
        sessao.MensagemErro.Should().Contain("Envie os dados novamente",
            "quem lê é comprador de farmácia: a mensagem tem de dizer a próxima ação");

        var itens = await ItensAsync(sessaoId);
        itens.Should().BeEmpty("nenhuma tentativa pode ter deixado linha atrás de si");
    }

    // --- Infra do teste ------------------------------------------------------

    private async Task<Cenario> CenarioAsync()
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            _cenario ??= await MontarCenarioAsync();
            return _cenario;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Cenario> MontarCenarioAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var redeId = await EnsureRedeAsync("Rede Sessao Resultado", Slug);
        var outraRedeId = await EnsureRedeAsync("Rede Sessao Resultado Outra", SlugOutraRede);

        await ImportarAsync(redeId, NomeDoProduto, VendaDiaria);
        await ImportarAsync(outraRedeId, NomeNaOutraRede, VendaDiariaDaOutraRede);

        await using var db = await AbrirEngineAsync(ct);
        var agora = DateTimeOffset.UtcNow;

        var comparacao = new ComparacaoPbs
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = ComparacaoPbsStatus.Concluido,
            DataAgendamento = agora,
            DataInicioProcessamento = agora,
            DataConclusao = agora,
            TreinoJobId = Guid.CreateVersion7(),
            JanelaInicio = DiaDaSugestao,
            JanelaFim = DiaDaSugestao,
            TipoCalculo = TipoCalculo,
            ResultadoJson = ResultadoDaComparacao(),
        };
        db.ComparacoesPbs.Add(comparacao);

        var sessaoId = Guid.CreateVersion7();
        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = sessaoId,
            RedeId = redeId,
            Nome = "Materializacao do resultado",
            Status = SessaoStatus.Comparando,
            CriadoEm = agora,
            AtualizadoEm = agora,
            SugestaoId = SugestaoId,
            SugestaoDataHora = SugestaoDataHora,
            SugestaoTipoCalculo = TipoCalculo,
            SkusSemCadastro = SkusSemCadastro,
            ComparacaoPbsId = comparacao.Id,
        });

        await db.SaveChangesAsync(ct);

        var sessao = await AguardarTerminoAsync(sessaoId, redeId);
        return new Cenario(redeId, sessaoId, comparacao.Id, sessao);
    }

    private async Task<List<ComparacaoSessaoItem>> ItensAsync(Guid sessaoId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AbrirEngineAsync(ct);

        return await db.ComparacaoSessaoItens.AsNoTracking()
            .Where(i => i.SessaoId == sessaoId)
            .OrderBy(i => i.Sku)
            .ToListAsync(ct);
    }

    private async Task<SessaoResultado> ResultadoAsync(Guid sessaoId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AbrirEngineAsync(ct);

        var json = await db.ComparacaoSessoes.AsNoTracking()
            .Where(s => s.Id == sessaoId)
            .Select(s => s.ResultadoJson)
            .FirstAsync(ct);

        json.Should().NotBeNullOrWhiteSpace("a sessão concluída tem de deixar os agregados da manchete");
        return JsonSerializer.Deserialize<SessaoResultado>(json!, Json)!;
    }

    /// <summary>
    /// Materializador ligado ao ambiente real do AppHost. Instanciado à mão porque o que se
    /// quer exercitar é a segunda passagem — a que o Worker de verdade nunca faz, já que a
    /// sessão saiu da fase reclamada.
    /// </summary>
    private async Task<SessaoResultadoMaterializador> MaterializadorAsync(CancellationToken ct)
    {
        var engine = await fixture.GetEngineConnectionStringAsync(ct);
        var stage = await fixture.GetStageConnectionStringAsync(ct);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:engine"] = engine,
                ["ConnectionStrings:Stage"] = stage,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDbContext<EngineDbContext>(o => o.UseSqlServer(engine));

        return new SessaoResultadoMaterializador(
            config, services.BuildServiceProvider(), NullLogger<SessaoResultadoMaterializador>.Instance);
    }

    private async Task<EngineDbContext> AbrirEngineAsync(CancellationToken ct)
    {
        var connStr = await fixture.GetEngineConnectionStringAsync(ct);
        var options = new DbContextOptionsBuilder<EngineDbContext>().UseSqlServer(connStr).Options;
        return new EngineDbContext(options);
    }

    private async Task<SessaoView> AguardarTerminoAsync(Guid sessaoId, int redeId)
    {
        var limite = TimeSpan.FromMinutes(3);
        var deadline = DateTimeOffset.UtcNow + limite;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.ComparacoesApi.GetAsync(
                sessaoId, redeId, TestContext.Current.CancellationToken);

            if (resp.Content is { Status: "Concluida" or "Inviavel" or "Falha" } sessao)
            {
                return sessao;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"Sessão {sessaoId} não atingiu estado terminal em {limite.TotalMinutes:F0} min. " +
            "Verifique os logs do worker (SessaoWorker).");
    }

    private async Task ImportarAsync(int redeId, string nomeDoProduto, decimal vendaDiaria)
    {
        var ct = TestContext.Current.CancellationToken;

        using var zip = BuildZip(nomeDoProduto, vendaDiaria);
        zip.Position = 0;

        var upload = await fixture.ImportsApi.UploadAsync(
            new StreamPart(zip, $"{Slug}-{redeId}.zip", "application/zip"), redeId, ct);

        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var carga = await fixture.WaitForCargaAsync(upload.Content!.Id, ct: ct);
        carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");
    }

    private async Task<int> EnsureRedeAsync(string nome, string slug)
    {
        var criacao = await fixture.RedesApi.CreateAsync(
            new CreateRedeRequest(nome, slug), TestContext.Current.CancellationToken);
        if (criacao.IsSuccessStatusCode)
        {
            return criacao.Content!.Id;
        }

        criacao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "só conflito de slug é aceitável aqui; outro status é falha real");

        var lista = await fixture.RedesApi.ListAsync(TestContext.Current.CancellationToken);
        lista.IsSuccessStatusCode.Should().BeTrue();
        var existente = lista.Content!.SingleOrDefault(r => r.Slug == slug);
        existente.Should().NotBeNull($"rede '{slug}' deu 409 mas não apareceu na listagem");
        return existente!.Id;
    }

    /// <summary>
    /// Envio avulso (sem sessão): o Stage precisa existir, mas nada aqui depende do vínculo
    /// entre carga e sessão — a sessão é semeada direto, já em <c>Comparando</c>.
    ///
    /// <para>
    /// <c>SkuSemPrevisao</c> aparece no cadastro e na sugestão, e <b>não</b> nas vendas: é o
    /// item que prova que compra sem venda nenhuma na cobertura continua na tabela.
    /// </para>
    /// </summary>
    private static MemoryStream BuildZip(string nomeDoProduto, decimal vendaDiaria)
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Resultado", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(SkuComPrevisao, nomeDoProduto, "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
            new(SkuSemPrevisao, $"{nomeDoProduto} B", "Genérico", "Antitérmico", "ACME", "Paracetamol", "20cp 750mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            vendas.Add(new VendaRow(d, LojaId, SkuComPrevisao, vendaDiaria, 10.50m, vendaDiaria * 10.50m));
        }

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            // Cinco snapshots dentro da cobertura de 30 dias, um deles zerado: a manchete
            // precisa distinguir "um dia sem estoque" de "não temos snapshot", e a cobertura
            // parcial do dado é justamente o que impede ler zero como ausência de ruptura.
            .WithEstoquesDiarios(
            [
                new(DiaDaSugestao, LojaId, SkuComPrevisao, 500m),
                new(DiaDaSugestao.AddDays(1), LojaId, SkuComPrevisao, 400m),
                new(DiaDaSugestao.AddDays(2), LojaId, SkuComPrevisao, 0m),
                new(DiaDaSugestao.AddDays(3), LojaId, SkuComPrevisao, 200m),
                new(DiaDaSugestao.AddDays(4), LojaId, SkuComPrevisao, 100m),
            ])
            .WithCompras(new CompraFaker([LojaId], [SkuComPrevisao], Inicio, Fim, seed: 950).Generate(2))
            .WithPromocoes(new PromocaoFaker([LojaId], [SkuComPrevisao], Inicio, Fim, seed: 951).Generate(1))
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], Inicio, Fim, seed: 952).Generate(1))
            .ReplaceRaw("sugestoes_compra.csv", SugestaoCsv())
            .ReplaceRaw("sugestoes_compra_itens.csv", SugestaoItensCsv())
            .Build();
    }

    private static string SugestaoCsv() =>
        "SugestaoId,Descricao,DataHora,TipoCalculo,LeadTimeDias,DiasCurvaA,DiasCurvaB,DiasCurvaC,DiasCurvaD,DiasCurvaE,Efetividade,ConsideraPedidosPendentes,IncluiEstoqueZerado\n" +
        $"{SugestaoId},Sugestao do resultado,{SugestaoDataHora:yyyy-MM-dd}T09:30:00,{TipoCalculo},7,30,30,30,30,30,100.00,1,0\n";

    private static string SugestaoItensCsv() =>
        "SugestaoId,LojaId,Sku,Curva,DemandaDia,DemandaDiaPonderada,EstoqueSaldo,EstoqueSeguranca," +
        "EstoqueMaximo,EstoqueMinimo,DiasEstoque,PedidosPendentes,CompraSugerida,CompraAutorizada," +
        "PrecoCompra,FatorEmbalagem,Falteiro\n" +
        string.Create(CultureInfo.InvariantCulture,
            $"{SugestaoId},{LojaId},{SkuComPrevisao},A,{VendaDiaria:0.0000},,{EstoqueSaldo:0.000},,,,") +
        string.Create(CultureInfo.InvariantCulture,
            $"{DiasEstoque},{PedidosPendentes:0.000},{CompraSugerida:0.000},{CompraSugerida:0.000},{PrecoCompra:0.0000},,0\n") +
        string.Create(CultureInfo.InvariantCulture,
            $"{SugestaoId},{LojaId},{SkuSemPrevisao},C,{VendaDiaria:0.0000},,0.000,,,,") +
        string.Create(CultureInfo.InvariantCulture,
            $"{DiasEstoque},0.000,{CompraSemPreco:0.000},{CompraSemPreco:0.000},,,0\n");

    /// <summary>
    /// Resultado da comparação como o <c>ComparacaoProcessor</c> o grava, montado com os tipos
    /// reais: a camada A avaliou um item e a camada B <b>nenhum</b>, com
    /// <see cref="UtilidadeComparacao.ForaDoHorizonteMl"/> — o desfecho documentado de hoje.
    /// Um JSON escrito à mão só provaria que a materialização lê o que ela própria espera.
    /// </summary>
    private static string ResultadoDaComparacao()
    {
        var metricas = new ForecastMetrics(1, 0.4, 0.4, 0.2, 0.2);
        var bracoA = new ArmResult(
            "erp", metricas, new Dictionary<string, IReadOnlyDictionary<string, ForecastMetrics>>());

        var previsao = new ComparisonResult(
            ParesAvaliados: 1,
            ParesDescartados: 0,
            Unidade: UnidadeMetrica.ErroPorParNaJanela,
            Erp: bracoA,
            Ml: bracoA with { Nome = "ml" },
            Vitoria: new WinRate(1, 1, 0, 0),
            VitoriaPorDimensao: new Dictionary<string, IReadOnlyDictionary<string, WinRate>>(),
            Detalhe:
            [
                new ParComparado(
                    SugestaoId: SugestaoId,
                    LojaId: LojaId,
                    Sku: SkuComPrevisao,
                    DiasAvaliados: 7,
                    DemandaDiaReal: DemandaDiaReal,
                    DemandaDiaErp: (double)VendaDiaria,
                    DemandaDiaMl: DemandaDiaMl,
                    ErroAbsErp: 0.0,
                    ErroAbsMl: 0.4,
                    Resultado: ResultadoPar.VitoriaErp),
            ]);

        var bracoB = new ArmDecisionResult(
            "erp", 0m, 0m, 0m, 0m, 0m, new VendaPerdidaIlustrativa(0m, 0m),
            new ForecastMetrics(0, 0, 0, 0, null));

        var decisao = new DecisionComparisonResult(
            ItensNaPopulacao: 2,
            ItensComparados: 0,
            ItensDescartadosPorRuptura: 0,
            ItensSemPrecoCompra: 0,
            Utilidade: UtilidadeComparacao.ForaDoHorizonteMl,
            ItensComFallbackEstoqueSeguranca: 0,
            Reconciliacao: new ReconciliacaoResumo(
                2, 2, 0, 0, 0, 0m, 0m, new Dictionary<string, ReconciliacaoPorCurva>()),
            DetalheReconciliacao: [],
            ForaDoHorizonteMl:
            [
                new ItemForaDoHorizonte(SugestaoId, LojaId, SkuComPrevisao, DiasEstoque, 7),
                new ItemForaDoHorizonte(SugestaoId, LojaId, SkuSemPrevisao, DiasEstoque, 7),
            ],
            Erp: bracoB,
            Ml: bracoB with { Nome = "ml" },
            Vitoria: new WinRate(0, 0, 0, 0),
            VitoriaPorDimensao: new Dictionary<string, IReadOnlyDictionary<string, WinRate>>(),
            Detalhe: []);

        var figuras = new HumanOverrideFigures(0m, 0m, 0m, 0m, 0m, 0m, 0m, null, null);

        var saida = new ComparacaoOutput(
            GeradoEm: DateTimeOffset.UtcNow,
            TreinoJobId: Guid.CreateVersion7(),
            ModeloTreinadoAte: UltimaDataTreinada,
            TreinoAte: DiaDaSugestao,
            TipoCalculo: TipoCalculo,
            JanelaInicio: DiaDaSugestao,
            JanelaFim: DiaDaSugestao,
            Sugestoes: 1,
            ItensDaSugestao: 2,
            ItensCamadaA: 1,
            ItensForaCamadaA: 1,
            ItensForaCamadaAAlemDoHistorico: 0,
            ItensCamadaB: 0,
            ItensForaCamadaB: 0,
            ItensForaCamadaBAlemDoHistorico: 0,
            ItensForaOrcamentoSkus: 0,
            RessalvaTreinoServe: ComparacaoOutput.RessalvaPadraoTreinoServe,
            Previsao: previsao,
            Decisao: decisao,
            Intervencao: new HumanOverrideResult(
                2, 1, figuras, null, new Dictionary<string, HumanOverrideResumoCurva>()));

        return JsonSerializer.Serialize(saida, Json);
    }
}
