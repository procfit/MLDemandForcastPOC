using System.Globalization;
using System.Net;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// A sessão anda sozinha: o comprador sobe um ZIP e o resto — importar, treinar, comparar —
/// acontece sem mais nenhum clique. Aqui o que se prova não é que existe um
/// <c>BackgroundService</c>, e sim que os dois jobs que ele cria nascem com os parâmetros de
/// que as fases seguintes dependem.
///
/// <para>
/// <b>O corte do treino é o item mais frágil de toda a fase.</b> Um <c>TreinoJob</c> sem
/// <c>TreinoAte</c>, ou com corte um dia adiante, roda liso e só é recusado na última fase —
/// e a recusa vem em forma de sessão em <c>Falha</c>, longe da causa. Por isso o valor é
/// afirmado no job de verdade que o Worker gravou, e não só na função que o deriva.
/// </para>
///
/// <para>
/// O ciclo inteiro roda <b>uma vez por processo</b> e é compartilhado pelos casos: treino é
/// caro (o backtest retreina o LightGBM a cada fold), e cada método receberia instância nova
/// da classe.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class SessaoOrquestracaoIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "sessao-orquestracao";
    private const string SlugOutraRede = "sessao-orquestracao-outra";
    private const int LojaId = 9801;
    private const string Sku = "ORQ-A";
    private const long SugestaoId = 7201;

    /// <summary>"Dias de Reposição" — o método que a sessão precisa repassar ao job de comparação.</summary>
    private const byte TipoCalculo = 2;

    /// <summary>Cobertura curta de propósito: cabe no horizonte de 7 dias e longe do fim do histórico.</summary>
    private const short DiasEstoque = 5;

    private static readonly DateOnly Inicio = new(2026, 1, 1);
    private static readonly DateOnly Fim = new(2026, 8, 31);
    private static readonly DateTime SugestaoDataHora = new(2026, 7, 1, 9, 30, 0);
    private static readonly DateOnly DiaDaSugestao = new(2026, 7, 1);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Ciclo? _ciclo;

    /// <param name="Desde">
    /// Instante anterior ao envio. O banco engine é persistente e compartilhado, então
    /// execuções anteriores deixaram jobs desta mesma rede para trás: sem este corte,
    /// "exatamente um treino" mediria o histórico do ambiente em vez desta sessão.
    /// </param>
    private sealed record Ciclo(
        int RedeId, Guid SessaoId, SessaoView Sessao, DateTimeOffset Desde,
        TreinoJobView Treino, ComparacaoPbsView Comparacao);

    [Fact]
    public async Task Sessao_caminha_do_envio_ate_concluida_sem_nenhum_clique_extra()
    {
        var ciclo = await CicloAsync();

        ciclo.Sessao.Status.Should().Be("Concluida",
            because: ciclo.Sessao.MensagemErro ?? ciclo.Sessao.MotivoInviabilidade ?? "sem motivo registrado");
        ciclo.Sessao.SugestaoId.Should().Be(SugestaoId);
    }

    /// <summary>
    /// O contrato do qual toda a última fase depende. O corte é o <b>dia da sugestão</b>
    /// porque o <c>StageObservationLoader</c> o aplica de forma exclusiva
    /// (<c>Data &lt; TreinoAte</c>) e o <c>ComparacaoProcessor</c> exige data alcançada
    /// estritamente anterior à sugestão: cortando em 01/07 o modelo para em 30/06.
    /// </summary>
    [Fact]
    public async Task Treino_criado_pela_sessao_corta_no_dia_da_sugestao()
    {
        var ciclo = await CicloAsync();

        ciclo.Treino.TreinoAte.Should().Be(DiaDaSugestao,
            "sem corte, ou com corte depois da sugestão, a comparação recusa o job e a sessão morre na última fase");
        ciclo.Treino.Status.Should().Be("Concluido", because: ciclo.Treino.MensagemErro ?? "sem mensagem de erro");
    }

    [Fact]
    public async Task Comparacao_criada_pela_sessao_mira_exatamente_a_sugestao_da_sessao()
    {
        var ciclo = await CicloAsync();

        ciclo.Comparacao.JanelaInicio.Should().Be(DiaDaSugestao);
        ciclo.Comparacao.JanelaFim.Should().Be(DiaDaSugestao,
            "a sessão está ancorada a UMA sugestão: janela mais larga passaria a depender do que mais houver no Stage");
        ciclo.Comparacao.TipoCalculo.Should().Be(TipoCalculo,
            "a disputa é contra o método que o ERP usou, declarado no envio");
        ciclo.Comparacao.TreinoJobId.Should().Be(ciclo.Treino.Id,
            "a comparação tem de usar o modelo que ESTA sessão treinou com o corte dela");
        ciclo.Comparacao.Status.Should().Be("Concluido",
            because: ciclo.Comparacao.MensagemErro ?? "sem mensagem de erro");
    }

    /// <summary>
    /// Invariante dura de multi-inquilino: os dois jobs nascem na rede da sessão. As
    /// listagens são filtradas por <c>RedeId</c> no servidor, então aparecer numa e não na
    /// outra é o que prova o valor gravado.
    /// </summary>
    [Fact]
    public async Task Jobs_criados_pela_sessao_pertencem_a_rede_dela_e_nao_vazam_para_outra()
    {
        var ciclo = await CicloAsync();
        var outraRede = await EnsureRedeAsync("Rede Sessao Orquestracao Outra", SlugOutraRede);

        var treinosDaOutra = await fixture.TrainingApi.ListAsync(
            outraRede, ct: TestContext.Current.CancellationToken);
        treinosDaOutra.IsSuccessStatusCode.Should().BeTrue();
        treinosDaOutra.Content.Should().NotContain(t => t.Id == ciclo.Treino.Id);

        var comparacoesDaOutra = await fixture.ComparisonApi.ListAsync(
            outraRede, ct: TestContext.Current.CancellationToken);
        comparacoesDaOutra.IsSuccessStatusCode.Should().BeTrue();
        comparacoesDaOutra.Content.Should().NotContain(c => c.Id == ciclo.Comparacao.Id);
    }

    /// <summary>
    /// Uma fase, um job. Um avanço que criasse job em duplicata não apareceria na tela — a
    /// sessão guarda um id só —, apareceria como treino extra rodando no ambiente.
    /// </summary>
    [Fact]
    public async Task Cada_fase_cria_um_job_e_nao_dois()
    {
        var ciclo = await CicloAsync();

        var treinos = await fixture.TrainingApi.ListAsync(
            ciclo.RedeId, ct: TestContext.Current.CancellationToken);
        treinos.Content!.Count(t => t.DataAgendamento >= ciclo.Desde).Should().Be(1);

        var comparacoes = await fixture.ComparisonApi.ListAsync(
            ciclo.RedeId, ct: TestContext.Current.CancellationToken);
        comparacoes.Content!.Count(c => c.DataAgendamento >= ciclo.Desde).Should().Be(1);
    }

    // --- Infra do teste ------------------------------------------------------

    private async Task<Ciclo> CicloAsync()
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            _ciclo ??= await RodarCicloAsync();
            return _ciclo;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Ciclo> RodarCicloAsync()
    {
        var redeId = await EnsureRedeAsync("Rede Sessao Orquestracao", Slug);

        var criada = await fixture.ComparacoesApi.CreateAsync(
            new CreateSessaoRequest("Ciclo completo"), redeId, TestContext.Current.CancellationToken);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        var sessaoId = criada.Content!.Id;

        var desde = DateTimeOffset.UtcNow;

        using (var zip = BuildZip())
        {
            zip.Position = 0;
            var envio = await fixture.ComparacoesApi.UploadDadosAsync(
                sessaoId, new StreamPart(zip, $"{Slug}.zip", "application/zip"), redeId,
                TestContext.Current.CancellationToken);
            envio.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var sessao = await AguardarTerminoAsync(sessaoId, redeId);

        var treinos = await fixture.TrainingApi.ListAsync(redeId, ct: TestContext.Current.CancellationToken);
        treinos.IsSuccessStatusCode.Should().BeTrue();
        var treino = treinos.Content!.SingleOrDefault(t => t.DataAgendamento >= desde);
        treino.Should().NotBeNull("a sessão precisa ter enfileirado exatamente um treino desde o envio");

        var comparacoes = await fixture.ComparisonApi.ListAsync(redeId, ct: TestContext.Current.CancellationToken);
        comparacoes.IsSuccessStatusCode.Should().BeTrue();
        var comparacao = comparacoes.Content!.SingleOrDefault(c => c.DataAgendamento >= desde);
        comparacao.Should().NotBeNull("a sessão precisa ter enfileirado exatamente uma comparação desde o envio");

        return new Ciclo(redeId, sessaoId, sessao, desde, treino!, comparacao!);
    }

    /// <summary>
    /// Espera a sessão chegar a estado terminal. O sinal é o próprio estado — é o mesmo
    /// mecanismo que a tela usa, e é justamente o avanço automático que está sob teste.
    /// </summary>
    private async Task<SessaoView> AguardarTerminoAsync(Guid sessaoId, int redeId)
    {
        var limite = TimeSpan.FromMinutes(15);
        var deadline = DateTimeOffset.UtcNow + limite;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.ComparacoesApi.GetAsync(
                sessaoId, redeId, TestContext.Current.CancellationToken);

            if (resp.Content is { Status: "Concluida" or "Inviavel" or "Falha" } sessao)
            {
                return sessao;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"Sessão {sessaoId} não atingiu estado terminal em {limite.TotalMinutes:F0} min. " +
            "Verifique os logs do worker (SessaoWorker, TreinoWorker, ComparacaoWorker).");
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
    /// Envio que sustenta o ciclo inteiro: histórico diário antes e depois da sugestão (o
    /// gabarito), a sugestão do PBS e a declaração na raiz. Quantidades explícitas em vez de
    /// faker — o teste afirma datas exatas.
    /// </summary>
    private static MemoryStream BuildZip()
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Orquestracao", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(Sku, "Produto Orquestracao", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            var qtd = 5m + d.Day % 7;
            vendas.Add(new VendaRow(d, LojaId, Sku, qtd, 10.50m, qtd * 10.50m));
        }

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            // Estoque folgado e longe da janela pontuada: sem dia de ruptura, o item chega à camada A.
            .WithEstoquesDiarios([new(new DateOnly(2026, 2, 10), LojaId, Sku, 500m)])
            .WithCompras(new CompraFaker([LojaId], [Sku], Inicio, Fim, seed: 940).Generate(2))
            .WithPromocoes(new PromocaoFaker([LojaId], [Sku], Inicio, Fim, seed: 941).Generate(1))
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], Inicio, Fim, seed: 942).Generate(1))
            .ReplaceRaw("sugestoes_compra.csv", SugestaoCsv())
            .ReplaceRaw("sugestoes_compra_itens.csv", SugestaoItensCsv())
            .ReplaceRaw("manifesto.json", ManifestoJson())
            .Build();
    }

    private static string ManifestoJson() => string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "SugestaoId": {{SugestaoId}},
          "SugestaoDescricao": "Sugestao do ciclo",
          "SugestaoDataHora": "{{SugestaoDataHora:yyyy-MM-dd}}T{{SugestaoDataHora:HH:mm:ss}}",
          "SugestaoTipoCalculo": {{TipoCalculo}},
          "JanelaInicio": "{{Inicio:yyyy-MM-dd}}",
          "JanelaFim": "{{Fim:yyyy-MM-dd}}",
          "VersaoExtractor": "1.0.0",
          "SkusSemCadastro": 0
        }
        """);

    private static string SugestaoCsv() =>
        "SugestaoId,Descricao,DataHora,TipoCalculo,LeadTimeDias,DiasCurvaA,DiasCurvaB,DiasCurvaC,DiasCurvaD,DiasCurvaE,Efetividade,ConsideraPedidosPendentes,IncluiEstoqueZerado\n" +
        $"{SugestaoId},Sugestao do ciclo,{SugestaoDataHora:yyyy-MM-dd}T09:30:00,{TipoCalculo},7,15,15,15,15,15,100.00,1,0\n";

    private static string SugestaoItensCsv()
    {
        const decimal demandaDia = 6m;
        const decimal estoqueSaldo = 10m;
        var compraSugerida = demandaDia * DiasEstoque - estoqueSaldo;

        return
            "SugestaoId,LojaId,Sku,Curva,DemandaDia,DemandaDiaPonderada,EstoqueSaldo,EstoqueSeguranca," +
            "EstoqueMaximo,EstoqueMinimo,DiasEstoque,PedidosPendentes,CompraSugerida,CompraAutorizada," +
            "PrecoCompra,FatorEmbalagem,Falteiro\n" +
            string.Create(CultureInfo.InvariantCulture,
                $"{SugestaoId},{LojaId},{Sku},A,{demandaDia:0.0000},,{estoqueSaldo:0.000},,,,") +
            string.Create(CultureInfo.InvariantCulture,
                $"{DiasEstoque},0.000,{compraSugerida:0.000},{compraSugerida:0.000},3.5000,,0\n");
    }
}
