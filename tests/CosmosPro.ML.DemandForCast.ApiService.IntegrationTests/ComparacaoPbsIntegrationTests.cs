using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using CosmosPro.ML.DemandForCast.Worker.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Training;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Fluxo completo do comparativo contra o ERP (F13): treino com corte → sugestão do
/// PBS no Stage → job de comparação → resultado.
///
/// <para>
/// O que estes testes guardam não é funcionalidade, é metodologia. Um modelo treinado
/// sobre os dias que a sugestão do ERP ainda não conhecia venceria a comparação sem ter
/// previsto nada, e o resultado sairia bem formado — nada, além destas recusas, avisaria.
/// Por isso o caminho feliz é só um dos casos, e os outros são recusas.
/// </para>
///
/// <para>
/// Treino é caro (backtest walk-forward retreina o LightGBM a cada fold), então os três
/// regimes de corte são treinados uma vez por execução do processo e reaproveitados —
/// ver <see cref="TreinoAsync"/>.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class ComparacaoPbsIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "comparacao-pbs";
    private const string SlugOutraRede = "comparacao-pbs-outra";
    private const int LojaId = 9901;

    /// <summary>SKUs que a sugestão do ERP avaliou.</summary>
    private const string SkuA = "CMP-A";
    private const string SkuB = "CMP-B";

    /// <summary>
    /// SKU presente no Stage e <b>ausente</b> da sugestão. Existe para que "a população
    /// não passa dos trios avaliados pelo ERP" seja verificável: sem ele, não haveria
    /// nada que o comparativo pudesse alargar indevidamente.
    /// </summary>
    private const string SkuForaDaSugestao = "CMP-C";

    private const long SugestaoId = 7001;

    /// <summary>
    /// "Dias de Reposição". Escolhido porque a demanda entra direto na fórmula
    /// (<c>demanda × DiasEstoque</c>), então a reconciliação da camada B é verificável
    /// sem depender de nenhuma hipótese sobre o eMax do ERP.
    /// </summary>
    private const byte TipoCalculo = 2;

    /// <summary>
    /// Cobertura da compra. 15 dias é a cobertura corrente no PBS e excede o horizonte
    /// de 7 dias do pipeline — é o cenário real que a camada B precisa reportar como
    /// <see cref="UtilidadeComparacao.ForaDoHorizonteMl"/>, não como sucesso vazio.
    /// </summary>
    private const short DiasEstoque = 15;

    private static readonly DateOnly Inicio = new(2026, 1, 1);
    private static readonly DateOnly Fim = new(2026, 12, 31);

    /// <summary>Data da sugestão do ERP. É o corte de informação de toda a comparação.</summary>
    private static readonly DateOnly DataSugestao = new(2026, 7, 1);

    /// <summary>Corte de treino honesto: o modelo para no dia anterior à sugestão.</summary>
    private static readonly DateOnly UltimaDataTreinadaEsperada = new(2026, 6, 30);

    /// <summary>Corte tardio: existe, mas deixa o modelo aprender sobre o gabarito.</summary>
    private static readonly DateOnly CorteTardio = new(2026, 8, 1);

    private static readonly DateOnly UltimaDataTreinadaTardia = new(2026, 7, 31);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Estado compartilhado entre os métodos do teste: cada `[Fact]` recebe uma
    // instância nova da classe, então cachear em campo de instância não serviria de
    // nada — e um treino por método levaria a execução a dezenas de minutos.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, Guid> Treinos = [];
    private static int _redeId;
    private static ComparacaoPbsView? _execucaoValida;

    [Fact]
    public async Task Treino_sem_corte_e_recusado_com_o_motivo_explicito()
    {
        var redeId = await SeedAsync();
        var treino = await TreinoAsync(redeId, corte: null);

        var comparacao = await ExecutarAsync(redeId, treino);

        comparacao.Status.Should().Be("Falha",
            "sem corte o modelo aprendeu com as vendas posteriores à sugestão — o gabarito da comparação");
        comparacao.MensagemErro.Should().NotBeNullOrEmpty();
        comparacao.MensagemErro.Should().Contain("SEM data de corte",
            "a mensagem precisa nomear a causa para quem não é especialista conseguir agir");
        comparacao.MensagemErro.Should().Contain("gabarito");
        comparacao.ResultadoJson.Should().BeNull("job recusado não pode deixar resultado para trás");
    }

    [Fact]
    public async Task Modelo_treinado_alem_da_data_da_sugestao_e_recusado()
    {
        var redeId = await SeedAsync();
        var treino = await TreinoAsync(redeId, CorteTardio);

        var comparacao = await ExecutarAsync(redeId, treino);

        comparacao.Status.Should().Be("Falha",
            "o corte existe mas cai depois da sugestão: o modelo já conhece o resultado que deveria prever");
        comparacao.MensagemErro.Should().Contain(UltimaDataTreinadaTardia.ToString("dd/MM/yyyy"),
            "a recusa cita a data que o treino de fato alcançou, não o corte pedido");
        comparacao.MensagemErro.Should().Contain(DataSugestao.ToString("dd/MM/yyyy"));
    }

    [Fact]
    public async Task Execucao_valida_declara_a_data_registrada_pelo_treino()
    {
        var (_, saida) = await ExecucaoValidaAsync();

        saida.ModeloTreinadoAte.Should().Be(UltimaDataTreinadaEsperada,
            "o valor sai de TrainingResult.UltimaDataTreinada — a data que o treino carregou de fato");
        saida.ModeloTreinadoAte.Should().NotBe(DataSugestao,
            "declarar a data da sugestão faria a checagem do comparador passar sempre, por construção");
        saida.TreinoAte.Should().Be(DataSugestao, "o corte pedido no treino viaja junto para leitura");
    }

    /// <summary>
    /// Fecha o circuito do corte: o valor declarado no resultado da comparação é o
    /// mesmo que o treino gravou, e não uma segunda leitura que poderia divergir.
    /// </summary>
    [Fact]
    public async Task Data_declarada_na_comparacao_e_a_mesma_que_o_treino_gravou()
    {
        var (comparacao, saida) = await ExecucaoValidaAsync();

        var treino = await fixture.TrainingApi.GetAsync(comparacao.TreinoJobId, TestContext.Current.CancellationToken);
        treino.Content.Should().NotBeNull();

        var resultadoTreino = JsonSerializer.Deserialize<TrainingResult>(treino.Content!.ResultadoJson!, Json);
        resultadoTreino!.UltimaDataTreinada.Should().Be(saida.ModeloTreinadoAte);
    }

    [Fact]
    public async Task Populacao_nunca_excede_os_trios_que_o_ERP_avaliou()
    {
        var (_, saida) = await ExecucaoValidaAsync();

        saida.ItensDaSugestao.Should().Be(2, "a sugestão do PBS tem exatamente dois itens");
        saida.ItensCamadaA.Should().BeLessThanOrEqualTo(saida.ItensDaSugestao);
        saida.ItensCamadaB.Should().BeLessThanOrEqualTo(saida.ItensDaSugestao);

        saida.Previsao.ParesAvaliados.Should().Be(2, "os dois itens têm janela pontuável e sem ruptura");
        saida.Previsao.Detalhe.Select(p => p.Sku).Should().BeEquivalentTo([SkuA, SkuB]);
        saida.Previsao.Detalhe.Should().OnlyContain(p => p.SugestaoId == SugestaoId && p.LojaId == LojaId);
        saida.Previsao.Detalhe.Should().NotContain(p => p.Sku == SkuForaDaSugestao,
            "SKU que o ERP não avaliou não pode entrar mesmo existindo no Stage e no modelo");

        saida.Intervencao.ItensNaPopulacao.Should().Be(2);
    }

    /// <summary>
    /// O desfecho esperado hoje: cobertura de 15 dias contra horizonte de 7. Um
    /// resultado bem formado com zero itens comparados leria como empate se
    /// <c>Utilidade</c> não dissesse o contrário.
    /// </summary>
    [Fact]
    public async Task Camada_B_reporta_fora_do_horizonte_em_vez_de_sucesso_vazio()
    {
        var (_, saida) = await ExecucaoValidaAsync();

        saida.Decisao.Utilidade.Should().Be(UtilidadeComparacao.ForaDoHorizonteMl,
            "os dois itens cobrem 15 dias e o pipeline prevê 7 — não é empate, é ausência de braço ML");
        saida.Decisao.ItensComparados.Should().Be(0);
        saida.Decisao.ForaDoHorizonteMl.Should().HaveCount(2);
        saida.Decisao.ForaDoHorizonteMl.Should().OnlyContain(i => i.DiasEstoque == DiasEstoque);

        saida.Decisao.Reconciliacao.Reconciliados.Should().Be(2,
            "a aritmética do ERP foi reproduzida: o portão de validade fez o trabalho dele");
        saida.Decisao.Reconciliacao.TaxaConcordancia.Should().BeNull(
            "reconciliar 100% sem comparar nada não é 1,0 — é ausência de comparação");
    }

    /// <summary>
    /// Mesma ressalva metodológica citada na documentação do resultado precisa viajar
    /// no JSON: quem lê o número tem de encontrar a ressalva no mesmo lugar.
    /// </summary>
    [Fact]
    public async Task Resultado_carrega_a_ressalva_de_treino_versus_servico()
    {
        var (_, saida) = await ExecucaoValidaAsync();

        saida.RessalvaTreinoServe.Should().Be(ComparacaoOutput.RessalvaPadraoTreinoServe);
        saida.RessalvaTreinoServe.Should().Contain("preço congelado");
    }

    [Fact]
    public async Task GET_por_id_de_outra_rede_retorna_404_nao_403_nem_a_comparacao()
    {
        var (comparacao, _) = await ExecucaoValidaAsync();
        var outraRede = await EnsureRedeAsync("Comparacao PBS Outra Rede", SlugOutraRede);

        var comoOutraRede = await fixture.ComparisonApi.GetAsync(
            comparacao.Id, outraRede, TestContext.Current.CancellationToken);

        comoOutraRede.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a outra rede não pode nem saber que a comparação existe — 403 vazaria a existência da linha");

        var listaOutraRede = await fixture.ComparisonApi.ListAsync(
            outraRede, ct: TestContext.Current.CancellationToken);
        listaOutraRede.IsSuccessStatusCode.Should().BeTrue();
        listaOutraRede.Content.Should().NotContain(c => c.Id == comparacao.Id);
    }

    [Fact]
    public async Task POST_com_treino_de_outra_rede_e_recusado_como_inexistente()
    {
        var redeId = await SeedAsync();
        var treino = await TreinoAsync(redeId, DataSugestao);
        var outraRede = await EnsureRedeAsync("Comparacao PBS Outra Rede", SlugOutraRede);

        var resp = await fixture.ComparisonApi.EnqueueAsync(
            new EnqueueComparisonRequest(treino, DataSugestao, DataSugestao, TipoCalculo),
            outraRede, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "treino de outra rede é indistinguível de inexistente — confirmar que existe já vazaria");
    }

    // --- Infra do teste ------------------------------------------------------

    private async Task<(ComparacaoPbsView Comparacao, ComparacaoOutput Saida)> ExecucaoValidaAsync()
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_execucaoValida is null)
            {
                var redeId = await SeedSemTravaAsync();
                var treino = await TreinoSemTravaAsync(redeId, DataSugestao);
                var comparacao = await ExecutarAsync(redeId, treino);

                comparacao.Status.Should().Be("Concluido",
                    because: comparacao.MensagemErro ?? "sem mensagem de erro");
                _execucaoValida = comparacao;
            }
        }
        finally
        {
            Gate.Release();
        }

        var saida = JsonSerializer.Deserialize<ComparacaoOutput>(_execucaoValida!.ResultadoJson!, Json);
        saida.Should().NotBeNull();
        return (_execucaoValida, saida!);
    }

    private async Task<ComparacaoPbsView> ExecutarAsync(int redeId, Guid treinoJobId)
    {
        var enfileirado = await fixture.ComparisonApi.EnqueueAsync(
            new EnqueueComparisonRequest(treinoJobId, DataSugestao, DataSugestao, TipoCalculo),
            redeId, TestContext.Current.CancellationToken);

        enfileirado.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.ComparisonApi.GetAsync(
                enfileirado.Content!.Id, redeId, TestContext.Current.CancellationToken);
            if (resp.Content is { } job && job.Status is "Concluido" or "Falha")
            {
                return job;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"Comparação {enfileirado.Content!.Id} não concluiu em 6 min. Verifique os logs do worker.");
    }

    /// <summary>
    /// Treina uma vez por regime de corte e reaproveita. A chave é o corte porque é
    /// exatamente ele que cada teste está exercitando.
    /// </summary>
    private async Task<Guid> TreinoAsync(int redeId, DateOnly? corte)
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            return await TreinoSemTravaAsync(redeId, corte);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Guid> TreinoSemTravaAsync(int redeId, DateOnly? corte)
    {
        var chave = corte?.ToString("yyyy-MM-dd") ?? "sem-corte";
        if (Treinos.TryGetValue(chave, out var existente)) return existente;

        var enfileirado = await fixture.TrainingApi.EnqueueAsync(
            new EnqueueTrainingRequest(MaxSkus: 10, TreinoAte: corte), redeId,
            TestContext.Current.CancellationToken);
        enfileirado.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.TrainingApi.GetAsync(
                enfileirado.Content!.Id, TestContext.Current.CancellationToken);
            if (resp.Content is { Status: "Concluido" or "Falha" } job)
            {
                job.Status.Should().Be("Concluido", because: job.MensagemErro ?? "sem mensagem de erro");
                Treinos[chave] = job.Id;
                return job.Id;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Treino (corte {chave}) não concluiu em 10 min.");
    }

    private async Task<int> SeedAsync()
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            return await SeedSemTravaAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Importa o dataset determinístico na rede própria deste teste, uma vez por
    /// processo. Reimportar é substituição completa do Stage da rede (ver
    /// <see cref="MultiRedeIntegrationTests"/>), então repetir por método invalidaria
    /// os treinos já feitos sobre ele.
    /// </summary>
    private async Task<int> SeedSemTravaAsync()
    {
        if (_redeId != 0) return _redeId;

        var redeId = await EnsureRedeAsync("Rede Comparacao PBS", Slug);

        using var zip = BuildZip();
        zip.Position = 0;
        var upload = await fixture.ImportsApi.UploadAsync(
            new StreamPart(zip, $"{Slug}.zip", "application/zip"), redeId,
            TestContext.Current.CancellationToken);

        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var carga = await fixture.WaitForCargaAsync(upload.Content!.Id, ct: TestContext.Current.CancellationToken);
        carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");

        _redeId = redeId;
        return redeId;
    }

    private async Task<int> EnsureRedeAsync(string nome, string slug)
    {
        var criacao = await fixture.RedesApi.CreateAsync(new CreateRedeRequest(nome, slug));
        if (criacao.IsSuccessStatusCode)
        {
            return criacao.Content!.Id;
        }

        criacao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "só conflito de slug é aceitável aqui; outro status é falha real");

        var lista = await fixture.RedesApi.ListAsync();
        lista.IsSuccessStatusCode.Should().BeTrue();
        var existente = lista.Content!.SingleOrDefault(r => r.Slug == slug);
        existente.Should().NotBeNull($"rede '{slug}' deu 409 mas não apareceu na listagem");
        return existente!.Id;
    }

    /// <summary>
    /// Ano cheio de venda diária para três SKUs numa loja, mais a sugestão do PBS.
    /// Datas e quantidades explícitas, sem faker: o teste afirma limites de data e
    /// contagens exatas, e valor sorteado tornaria a asserção uma coincidência.
    /// </summary>
    private static MemoryStream BuildZip()
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Comparacao", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(SkuA, "Produto Comparacao A", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
            new(SkuB, "Produto Comparacao B", "Genérico", "Antitérmico", "ACME", "Paracetamol", "20cp 750mg", null, null, null, null, true),
            new(SkuForaDaSugestao, "Produto Comparacao C", "Genérico", "Antialérgico", "ACME", "Loratadina", "12cp 10mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            foreach (var sku in new[] { SkuA, SkuB, SkuForaDaSugestao })
            {
                var qtd = 5m + d.Day % 7;
                vendas.Add(new VendaRow(d, LojaId, sku, qtd, 10.50m, qtd * 10.50m));
            }
        }

        // Estoque folgado e longe da janela pontuada: nenhum dia de ruptura, para que
        // os dois itens cheguem à camada A em vez de caírem por ExcluirPar.
        var estoques = new List<EstoqueDiarioRow>
        {
            new(new DateOnly(2026, 2, 10), LojaId, SkuA, 500m),
        };

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            .WithEstoquesDiarios(estoques)
            .WithCompras(new CompraFaker([LojaId], [SkuA, SkuB], Inicio, Fim, seed: 910).Generate(2))
            .WithPromocoes(new PromocaoFaker([LojaId], [SkuA], Inicio, Fim, seed: 911).Generate(1))
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], Inicio, Fim, seed: 912).Generate(1))
            .ReplaceRaw("sugestoes_compra.csv", SugestaoCsv())
            .ReplaceRaw("sugestoes_compra_itens.csv", SugestaoItensCsv())
            .Build();
    }

    private static string SugestaoCsv() =>
        "SugestaoId,Descricao,DataHora,TipoCalculo,LeadTimeDias,DiasCurvaA,DiasCurvaB,DiasCurvaC,DiasCurvaD,DiasCurvaE,Efetividade,ConsideraPedidosPendentes,IncluiEstoqueZerado\n" +
        $"{SugestaoId},Sugestao de teste,{DataSugestao:yyyy-MM-dd}T09:00:00,{TipoCalculo},7,15,15,15,15,15,100.00,1,0\n";

    /// <summary>
    /// <c>CompraSugerida</c> é calculada aqui pela mesma aritmética que o
    /// <c>DecisionComparer</c> modela para <c>TipoCalculo</c> 2
    /// (<c>demanda × DiasEstoque − posição</c>, sem múltiplo de embalagem), para que a
    /// reconciliação bata: sem itens reconciliados, o teste do horizonte estaria
    /// medindo o portão de validade em vez do horizonte.
    /// </summary>
    private static string SugestaoItensCsv()
    {
        var sb = new StringBuilder();
        sb.Append("SugestaoId,LojaId,Sku,Curva,DemandaDia,DemandaDiaPonderada,EstoqueSaldo,EstoqueSeguranca,")
          .Append("EstoqueMaximo,EstoqueMinimo,DiasEstoque,PedidosPendentes,CompraSugerida,CompraAutorizada,")
          .Append("PrecoCompra,FatorEmbalagem,Falteiro\n");

        // CompraAutorizada = 0 no segundo item: um veto do comprador, para a camada C
        // ter intervenção humana a descrever.
        Linha(sb, SkuA, "A", demandaDia: 6m, estoqueSaldo: 10m, compraAutorizada: 80m, precoCompra: 3.5m);
        Linha(sb, SkuB, "B", demandaDia: 4m, estoqueSaldo: 5m, compraAutorizada: 0m, precoCompra: 2.0m);

        return sb.ToString();

        static void Linha(
            StringBuilder sb, string sku, string curva, decimal demandaDia, decimal estoqueSaldo,
            decimal compraAutorizada, decimal precoCompra)
        {
            var compraSugerida = demandaDia * DiasEstoque - estoqueSaldo;
            sb.Append(CultureInfo.InvariantCulture,
                $"{SugestaoId},{LojaId},{sku},{curva},{demandaDia:0.0000},,{estoqueSaldo:0.000},,,,")
              .Append(CultureInfo.InvariantCulture,
                $"{DiasEstoque},0.000,{compraSugerida:0.000},{compraAutorizada:0.000},{precoCompra:0.0000},,0\n");
        }
    }
}
