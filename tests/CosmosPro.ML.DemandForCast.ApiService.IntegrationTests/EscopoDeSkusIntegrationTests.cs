using System.Net;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using CosmosPro.ML.DemandForCast.Worker.Purchasing;
using CosmosPro.ML.DemandForCast.Worker.Training;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Escopo de SKUs dos carregadores do Stage: o orçamento opcional
/// (<c>TreinoJob.MaxSkus</c>) e o join com tabela temporária que o substituiu
/// (<see cref="EscopoDeSkus"/>).
///
/// <para>
/// <b>Por que aqui e não em unidade:</b> o que se afirma é comportamento do SQL Server —
/// que um escopo de milhares de SKUs não estoura o limite de parâmetros por comando, e
/// que o join filtra o que o <c>IN</c> filtrava. Um duplo em memória provaria apenas que
/// o C# sabe montar string.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class EscopoDeSkusIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "escopo-skus";
    private const int LojaId = 8901;

    /// <summary>Volumes deliberadamente separados: o recorte por teto é por volume.</summary>
    private const string SkuAlta = "ESCOPO-ALTA";
    private const string SkuMedia = "ESCOPO-MEDIA";
    private const string SkuBaixa = "ESCOPO-BAIXA";

    private static readonly DateOnly Inicio = new(2026, 1, 1);
    private static readonly DateOnly Fim = new(2026, 12, 31);

    private static readonly DateOnly DiaDoEstoque = new(2026, 6, 10);
    private static readonly DateOnly PrimeiroDiaSimulacao = new(2026, 6, 11);
    private const decimal EstoqueDoDia = 42m;

    /// <summary>
    /// O default do fluxo da sessão. Sem teto, todo SKU com histórico entra — inclusive o
    /// de menor volume, que é a maioria do catálogo farma e onde o erro do modelo aparece.
    /// </summary>
    [Fact]
    public async Task Sem_teto_o_loader_traz_todo_sku_com_historico()
    {
        var redeId = await SeedAsync();

        var observacoes = await CarregarAsync(redeId, maxSkus: null);

        observacoes.Select(o => o.Sku).Distinct()
            .Should().BeEquivalentTo([SkuAlta, SkuMedia, SkuBaixa]);
    }

    /// <summary>
    /// A contraprova do teste acima: com teto o recorte é <b>por volume</b>, e some
    /// justamente o item esparso. Mantido para experimento, e mantido testado para que a
    /// diferença entre os dois modos fique visível em vez de virar folclore — na Retiro o
    /// teto de mil não chegava a apertar (só 991 SKUs tinham venda antes do corte), e sem
    /// este teste ninguém saberia dizer se o modo com teto ainda recorta algo.
    /// </summary>
    [Fact]
    public async Task Com_teto_o_loader_recorta_pelos_de_maior_volume()
    {
        var redeId = await SeedAsync();

        var observacoes = await CarregarAsync(redeId, maxSkus: 1);

        observacoes.Select(o => o.Sku).Distinct().Should().BeEquivalentTo([SkuAlta]);
    }

    /// <summary>
    /// O orçamento que a comparação usa para decidir <c>ItensForaOrcamentoSkus</c>. Sem
    /// teto ele é "todo SKU com venda antes do corte", então nenhum item da sugestão sai
    /// por escolha nossa — só por falta de histórico.
    /// </summary>
    [Fact]
    public async Task Sem_teto_o_orcamento_abc_cobre_todo_sku_com_historico()
    {
        var redeId = await SeedAsync();
        var connStr = await fixture.GetStageConnectionStringAsync(TestContext.Current.CancellationToken);
        var loader = new StageObservationLoader(connStr, NullLogger.Instance);

        var abc = await loader.LoadOrcamentoAbcAsync(
            redeId, maxSkus: null, treinoAte: null, TestContext.Current.CancellationToken);

        abc.Keys.Should().BeEquivalentTo([SkuAlta, SkuMedia, SkuBaixa]);
        abc.Values.Should().OnlyContain(c => c == "A" || c == "B" || c == "C");
    }

    /// <summary>
    /// <b>A regressão que motivou o <see cref="EscopoDeSkus"/>.</b> O
    /// <c>Sku IN (@s0, …, @sN)</c> gastava um parâmetro por SKU contra os 2100 que o SQL
    /// Server aceita por comando: com 3000 SKUs no escopo, o comando <b>quebrava</b> — não
    /// ficava lento. Era esse limite de implementação, e não uma decisão de modelagem, que
    /// obrigava o orçamento do treino a ter teto.
    ///
    /// <para>
    /// O escopo tem 3000 entradas e só uma existe no Stage: o teste afirma as duas coisas
    /// de uma vez — que o tamanho não quebra, e que o join continua filtrando (não devolve
    /// as outras 2999 nem ignora o filtro devolvendo a rede inteira).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Escopo_com_mais_de_2100_skus_nao_estoura_o_limite_de_parametros()
    {
        var redeId = await SeedAsync();
        var connStr = await fixture.GetStageConnectionStringAsync(TestContext.Current.CancellationToken);
        var loader = new StageEstoqueInicialLoader(connStr, NullLogger.Instance);

        var escopo = Enumerable.Range(0, 2999)
            .Select(i => $"INEXISTENTE-{i:D5}")
            .Append(SkuAlta)
            .ToList();

        var estoque = await loader.LoadAsync(
            redeId, escopo, PrimeiroDiaSimulacao, TestContext.Current.CancellationToken);

        estoque.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<(string, int), decimal>((SkuAlta, LojaId), EstoqueDoDia));
    }

    private async Task<IReadOnlyList<Features.Models.DailyObservation>> CarregarAsync(int redeId, int? maxSkus)
    {
        var connStr = await fixture.GetStageConnectionStringAsync(TestContext.Current.CancellationToken);
        var loader = new StageObservationLoader(connStr, NullLogger.Instance);
        return await loader.LoadAsync(redeId, maxSkus, treinoAte: null, TestContext.Current.CancellationToken);
    }

    private async Task<int> SeedAsync()
    {
        var redeId = await EnsureRedeAsync("Rede Escopo de SKUs", Slug);

        using var zip = BuildZip();
        zip.Position = 0;
        var upload = await fixture.ImportsApi.UploadAsync(
            new StreamPart(zip, $"{Slug}.zip", "application/zip"), redeId,
            TestContext.Current.CancellationToken);

        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var carga = await fixture.WaitForCargaAsync(upload.Content!.Id, ct: TestContext.Current.CancellationToken);
        carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");

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
    /// Ano cheio para três SKUs numa loja, com volumes diários fixos e bem separados
    /// (20 / 8 / 1). Sem faker no volume: o recorte por teto é uma ordenação por soma, e
    /// quantidade sorteada faria a asserção depender de sorte.
    /// </summary>
    private static MemoryStream BuildZip()
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Escopo", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(SkuAlta, "Produto Escopo Alta", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
            new(SkuMedia, "Produto Escopo Média", "Genérico", "Antitérmico", "ACME", "Paracetamol", "20cp 750mg", null, null, null, null, true),
            new(SkuBaixa, "Produto Escopo Baixa", "Genérico", "Antialérgico", "ACME", "Loratadina", "12cp 10mg", null, null, null, null, true),
        };

        var volumes = new (string Sku, decimal Qtd)[] { (SkuAlta, 20m), (SkuMedia, 8m), (SkuBaixa, 1m) };

        var vendas = new List<VendaRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            foreach (var (sku, qtd) in volumes)
            {
                vendas.Add(new VendaRow(d, LojaId, sku, qtd, 10.50m, qtd * 10.50m));
            }
        }

        var estoques = new List<EstoqueDiarioRow>
        {
            new(DiaDoEstoque, LojaId, SkuAlta, EstoqueDoDia),
        };

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            .WithEstoquesDiarios(estoques)
            .WithPromocoes([])
            .WithCompras(new CompraFaker([LojaId], [SkuAlta, SkuMedia, SkuBaixa], Inicio, Fim, seed: 910).Generate(3))
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], Inicio, Fim, seed: 911).Generate(1))
            .Build();
    }
}
