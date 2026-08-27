using System.Net;
using System.Text.Json;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using CosmosPro.ML.DemandForCast.Worker.Purchasing;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Escopo de rede da fila de simulação de compra (<c>engine.SimulacoesCompra</c>). O claim
/// do <c>SimulacaoWorker</c> é a única leitura da linha: uma coluna fora do <c>OUTPUT</c>
/// chega ao <c>SimulacaoProcessor</c> como default do tipo, e com <c>RedeId</c> 0 o Stage
/// não devolve observação nenhuma — toda simulação morria em "Sem observações no Stage".
/// É o mesmo defeito que <see cref="TreinoCorteIntegrationTests"/> fecha do lado do treino.
///
/// <para>
/// Duas redes com os <b>mesmos</b> códigos de SKU e lojas distintas: <c>Produtos</c> tem PK
/// <c>(RedeId, Sku)</c> justamente porque código de ERP colide entre inquilinos. Assim as
/// asserções não se contentam com "o job não quebrou" — elas afirmam de quem é o dado que
/// saiu do outro lado.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class SimulacaoRedeIntegrationTests(AppHostFixture fixture)
{
    private const string SkuA = "SIM-A";
    private const string SkuB = "SIM-B";

    private const int LojaDaRede = 8901;
    private const int LojaDaOutraRede = 8902;

    private const string NomeProdutoA = "Produto Simulacao A";
    private const string NomeProdutoNaOutraRede = "Produto De Outra Rede A";

    private static readonly DateOnly Inicio = new(2026, 1, 1);
    private static readonly DateOnly Fim = new(2026, 12, 31);

    /// <summary>Mínimo aceito pela API. Janela curta encurta o replay sem enfraquecer nada aqui.</summary>
    private const int JanelaDias = 14;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Simulacao_roda_sobre_o_Stage_da_rede_do_treino_e_nao_de_outra()
    {
        // A outra rede vem primeiro e com volume maior: se o escopo escapasse, seria ela
        // que dominaria o top-N de SKUs e apareceria no resultado.
        await SeedAsync("Rede Vizinha da Simulação", "simulacao-outra-rede",
            LojaDaOutraRede, NomeProdutoNaOutraRede, quantidadeBase: 40m);

        var redeId = await SeedAsync("Rede da Simulação", "simulacao-rede",
            LojaDaRede, NomeProdutoA, quantidadeBase: 5m);

        var treino = await fixture.TrainingApi.EnqueueAsync(
            new EnqueueTrainingRequest(MaxSkus: 10, TreinoAte: null), redeId);
        treino.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var treinoConcluido = await AguardarTreinoAsync(treino.Content!.Id);
        treinoConcluido.Status.Should().Be("Concluido",
            because: treinoConcluido.MensagemErro ?? "sem mensagem de erro");

        var enfileirada = await fixture.PurchasingApi.SimulateAsync(
            new EnqueueSimulationRequest(treino.Content.Id, JanelaDias, LeadTimeDias: 7, CicloDias: 7, FatorServico: 1.65),
            TestContext.Current.CancellationToken);
        enfileirada.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var simulacao = await AguardarSimulacaoAsync(enfileirada.Content!.Id);

        simulacao.Status.Should().Be("Concluido",
            because: simulacao.MensagemErro ?? "sem mensagem de erro");
        simulacao.SeriesSimuladas.Should().Be(2,
            "a rede tem duas séries (dois SKUs numa loja); zero séries é o sintoma de RedeId perdido no claim");

        var saida = JsonSerializer.Deserialize<SimulationOutput>(simulacao.ResultadoJson!, Json);
        saida.Should().NotBeNull();

        // O nome do produto só pode ser este se a leitura de Produtos foi escopada: as duas
        // redes gravam o MESMO Sku com nomes diferentes.
        saida!.Produtos.Should().ContainKey(SkuA);
        saida.Produtos[SkuA].Should().Be(NomeProdutoA);
        saida.Produtos.Values.Should().NotContain(NomeProdutoNaOutraRede);

        var lista = saida.Resultado.Politicas.Single().ListaCompraFinal;
        lista.Should().NotBeEmpty();
        lista.Should().OnlyContain(i => i.LojaId == LojaDaRede,
            "a loja da outra rede não pode aparecer no replay desta");
        lista.Select(i => i.Sku).Distinct().Should().BeEquivalentTo([SkuA, SkuB]);
    }

    private async Task<TreinoJobView> AguardarTreinoAsync(Guid id)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.TrainingApi.GetAsync(id, TestContext.Current.CancellationToken);
            if (resp.Content is { } job && job.Status is "Concluido" or "Falha") return job;
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Treino {id} não concluiu em 8 min. Verifique os logs do worker.");
    }

    private async Task<SimulacaoJobView> AguardarSimulacaoAsync(Guid id)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.PurchasingApi.GetAsync(id, TestContext.Current.CancellationToken);
            if (resp.Content is { } job && job.Status is "Concluido" or "Falha") return job;
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Simulação {id} não concluiu em 8 min. Verifique os logs do worker.");
    }

    /// <summary>
    /// Importa o dataset da rede indicada. Mesma abordagem de
    /// <see cref="TreinoCorteIntegrationTests"/>: reimportar substitui a rede inteira, então
    /// semear por teste mantém a independência sem cruzar com a rede demo.
    /// </summary>
    private async Task<int> SeedAsync(string nome, string slug, int lojaId, string nomeProdutoA, decimal quantidadeBase)
    {
        var redeId = await EnsureRedeAsync(nome, slug);

        using var zip = BuildZip(lojaId, nomeProdutoA, quantidadeBase);
        zip.Position = 0;
        var upload = await fixture.ImportsApi.UploadAsync(
            new StreamPart(zip, $"{slug}.zip", "application/zip"), redeId,
            TestContext.Current.CancellationToken);

        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var carga = await fixture.WaitForCargaAsync(upload.Content!.Id, ct: TestContext.Current.CancellationToken);
        carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");

        return redeId;
    }

    private async Task<int> EnsureRedeAsync(string nome, string slug)
    {
        var criacao = await fixture.RedesApi.CreateAsync(new CreateRedeRequest(nome, slug));
        if (criacao.IsSuccessStatusCode) return criacao.Content!.Id;

        criacao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "só conflito de slug é aceitável aqui; outro status é falha real");

        var lista = await fixture.RedesApi.ListAsync();
        lista.IsSuccessStatusCode.Should().BeTrue();
        var existente = lista.Content!.SingleOrDefault(r => r.Slug == slug);
        existente.Should().NotBeNull($"rede '{slug}' deu 409 mas não apareceu na listagem");
        return existente!.Id;
    }

    /// <summary>
    /// Série diária de um ano para dois SKUs numa loja, sem faker nas vendas: o ano cheio é
    /// o mínimo para o walk-forward do treino ter folds, e valor sorteado tornaria a
    /// distinção entre as duas redes uma coincidência.
    /// </summary>
    private static MemoryStream BuildZip(int lojaId, string nomeProdutoA, decimal quantidadeBase)
    {
        var lojas = new List<LojaRow>
        {
            new(lojaId, $"Loja {lojaId}", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(SkuA, nomeProdutoA, "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
            new(SkuB, $"{nomeProdutoA} B", "Genérico", "Antitérmico", "ACME", "Paracetamol", "20cp 750mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>();
        var estoques = new List<EstoqueDiarioRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            foreach (var sku in new[] { SkuA, SkuB })
            {
                var qtd = quantidadeBase + d.Day % 7;
                vendas.Add(new VendaRow(d, lojaId, sku, qtd, 10.50m, qtd * 10.50m));
                estoques.Add(new EstoqueDiarioRow(d, lojaId, sku, quantidadeBase * 10m));
            }
        }

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            .WithEstoquesDiarios(estoques)
            .WithCompras(new CompraFaker([lojaId], [SkuA, SkuB], Inicio, Fim, seed: 910).Generate(2))
            .WithPromocoes([])
            .Build();
    }
}
