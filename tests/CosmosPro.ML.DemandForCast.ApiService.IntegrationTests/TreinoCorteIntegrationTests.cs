using System.Net;
using System.Text.Json;
using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using CosmosPro.ML.DemandForCast.Worker.Training;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Corte de informação do treino (<c>TreinoJob.TreinoAte</c>). O risco que estes
/// testes cobrem não é funcional, é metodológico: sem o corte o modelo é ajustado
/// sobre os dias posteriores à sugestão do ERP — exatamente os que servem de
/// gabarito na comparação —, e o número que sai não mede previsão.
///
/// <para>
/// O <see cref="StageObservationLoader"/> só existe contra SQL Server, então a
/// verificação vive aqui, no projeto que sobe o AppHost real, e não num teste de
/// unidade com o loader escondido atrás de uma interface.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class TreinoCorteIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "treino-corte";
    private const int LojaId = 8801;
    private const string SkuA = "CORTE-A";
    private const string SkuB = "CORTE-B";

    private static readonly DateOnly Inicio = new(2026, 1, 1);
    private static readonly DateOnly Fim = new(2026, 12, 31);

    /// <summary>Corte usado por todos os testes. Nada em 2026-07-01 ou depois pode entrar.</summary>
    private static readonly DateOnly Corte = new(2026, 7, 1);

    /// <summary>Último dia com venda antes do corte — o máximo que o treino pode alcançar.</summary>
    private static readonly DateOnly UltimoDiaValido = new(2026, 6, 30);

    /// <summary>Ruptura sem venda no mesmo dia, antes do corte: precisa sobreviver ao filtro.</summary>
    private static readonly DateOnly RupturaAntes = new(2026, 6, 25);

    /// <summary>
    /// Ruptura sem venda no mesmo dia, depois do corte. É a armadilha: a observação
    /// não nasce de <c>Vendas</c> (que o corte já filtra) e sim de
    /// <c>EstoquesDiarios</c>. Filtrar só a venda deixaria este dia entrar.
    /// </summary>
    private static readonly DateOnly RupturaDepois = new(2026, 7, 20);

    /// <summary>Dia dentro de promoção iniciada antes do corte.</summary>
    private static readonly DateOnly DiaEmPromocao = new(2026, 6, 6);

    [Fact]
    public async Task Com_corte_nenhuma_observacao_alcanca_a_data_de_corte()
    {
        var redeId = await SeedAsync();
        var observacoes = await CarregarAsync(redeId, Corte);

        observacoes.Should().NotBeEmpty();
        observacoes.Should().OnlyContain(o => o.Data < Corte,
            "o corte é o instante da decisão — qualquer dia a partir dele é o gabarito, não histórico");
        observacoes.Max(o => o.Data).Should().Be(UltimoDiaValido);

        observacoes.Should().NotContain(o => o.Data == RupturaDepois,
            "EstoquesDiarios materializa observação sozinha; sem corte nela o dia posterior entraria mesmo com Vendas filtrada");

        var ruptura = observacoes.SingleOrDefault(o => o.Data == RupturaAntes && o.Sku == SkuA);
        ruptura.Should().NotBeNull("ruptura anterior ao corte é histórico legítimo e precisa continuar vindo");
        ruptura!.EmRuptura.Should().BeTrue();
        ruptura.Quantidade.Should().Be(0);

        var emPromocao = observacoes.SingleOrDefault(o => o.Data == DiaEmPromocao && o.Sku == SkuA);
        emPromocao.Should().NotBeNull();
        emPromocao!.EmPromocao.Should().BeTrue(
            "o filtro de Promocoes corta por DataInicio; campanha iniciada antes do corte continua valendo");
    }

    /// <summary>
    /// Guarda o comportamento anterior <b>e</b> impede que o teste do corte seja
    /// vacuoso: se o Stage não tivesse dados depois de <see cref="Corte"/>, filtrar
    /// não provaria nada.
    /// </summary>
    [Fact]
    public async Task Sem_corte_o_historico_posterior_continua_vindo()
    {
        var redeId = await SeedAsync();
        var observacoes = await CarregarAsync(redeId, treinoAte: null);

        observacoes.Max(o => o.Data).Should().Be(Fim,
            "com treinoAte nulo o loader mantém o comportamento de treinar sobre todo o histórico");
        observacoes.Should().Contain(o => o.Data == RupturaDepois && o.Sku == SkuA);
    }

    /// <summary>
    /// Fecha o circuito: o corte pedido na API sobrevive ao claim do
    /// <c>TreinoWorker</c> e é o que o <c>TreinoProcessor</c> de fato aplica.
    /// Sem este teste, os anteriores provariam apenas que o loader sabe filtrar —
    /// não que alguém lhe passa o corte.
    /// </summary>
    [Fact]
    public async Task Job_treinado_com_corte_registra_o_que_foi_aplicado()
    {
        var redeId = await SeedAsync();

        var enfileirado = await fixture.TrainingApi.EnqueueAsync(
            new EnqueueTrainingRequest(MaxSkus: 10, TreinoAte: Corte), redeId);

        enfileirado.StatusCode.Should().Be(HttpStatusCode.Accepted);
        enfileirado.Content!.TreinoAte.Should().Be(Corte, "o corte precisa ser persistido no job, não só aceito");

        var concluido = await AguardarTreinoAsync(enfileirado.Content.Id);
        concluido.Status.Should().Be("Concluido", because: concluido.MensagemErro ?? "sem mensagem de erro");
        concluido.TreinoAte.Should().Be(Corte);

        var resultado = JsonSerializer.Deserialize<TrainingResult>(
            concluido.ResultadoJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        resultado.Should().NotBeNull();
        resultado!.TreinoAte.Should().Be(Corte);
        resultado.UltimaDataTreinada.Should().Be(UltimoDiaValido,
            "é este o valor que a comparação declara como ModeloTreinadoAte — derivá-lo do corte seria adivinhar");
    }

    private async Task<IReadOnlyList<DailyObservation>> CarregarAsync(int redeId, DateOnly? treinoAte)
    {
        var connStr = await fixture.GetStageConnectionStringAsync(TestContext.Current.CancellationToken);
        var loader = new StageObservationLoader(connStr, NullLogger.Instance);
        return await loader.LoadAsync(redeId, maxSkus: 10, treinoAte, TestContext.Current.CancellationToken);
    }

    private async Task<TreinoJobView> AguardarTreinoAsync(Guid id)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.TrainingApi.GetAsync(id, TestContext.Current.CancellationToken);
            if (resp.Content is { } job && job.Status is "Concluido" or "Falha")
            {
                return job;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Treino {id} não concluiu em 8 min. Verifique os logs do worker.");
    }

    /// <summary>
    /// Importa o dataset determinístico na rede própria deste teste. Reimportar é
    /// substituição completa (ver <see cref="MultiRedeIntegrationTests"/>), então
    /// chamar por teste mantém cada um independente sem cruzar com a rede demo.
    /// </summary>
    private async Task<int> SeedAsync()
    {
        var redeId = await EnsureRedeAsync("Rede Corte de Treino", Slug);

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
    /// Série diária inteira de 2026 para duas SKUs numa loja. Datas explícitas, sem
    /// faker: o teste afirma limites de data exatos, e valor sorteado tornaria a
    /// asserção uma coincidência. O ano cheio é o mínimo para o backtest
    /// walk-forward do treino ter folds do lado anterior ao corte.
    /// </summary>
    private static MemoryStream BuildZip()
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Corte", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(SkuA, "Produto Corte A", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
            new(SkuB, "Produto Corte B", "Genérico", "Antitérmico", "ACME", "Paracetamol", "20cp 750mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            foreach (var sku in new[] { SkuA, SkuB })
            {
                // Sem venda nos dias de ruptura: assim a observação daqueles dias só
                // pode ter vindo de EstoquesDiarios, que é o que se quer observar.
                if (sku == SkuA && (d == RupturaAntes || d == RupturaDepois)) continue;

                var qtd = 5m + d.Day % 7;
                vendas.Add(new VendaRow(d, LojaId, sku, qtd, 10.50m, qtd * 10.50m));
            }
        }

        var estoques = new List<EstoqueDiarioRow>
        {
            new(RupturaAntes, LojaId, SkuA, 0m),
            new(RupturaDepois, LojaId, SkuA, 0m),
        };

        var promocoes = new List<PromocaoRow>
        {
            new(new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 8), SkuA, null, "desconto", 15m),
            // Iniciada depois do corte: o loader não deve nem carregá-la.
            new(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 8), SkuA, null, "desconto", 20m),
        };

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            .WithEstoquesDiarios(estoques)
            .WithCompras(new CompraFaker([LojaId], [SkuA, SkuB], Inicio, Fim, seed: 900).Generate(2))
            .WithPromocoes(promocoes)
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], Inicio, Fim, seed: 901).Generate(1))
            .Build();
    }
}
