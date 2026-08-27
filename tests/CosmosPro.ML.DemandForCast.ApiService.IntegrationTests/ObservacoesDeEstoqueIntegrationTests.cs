using System.Net;
using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using CosmosPro.ML.DemandForCast.Worker.Training;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// O que a posição de estoque significa para o conjunto de treino — as duas leituras
/// opostas que saem da mesma tabela.
///
/// <para>
/// <b>Estoque positivo e nenhuma venda é demanda zero de verdade</b> (alvo válido): a única
/// situação em que "vendeu 0" quer dizer "ninguém quis". <b>Estoque zerado e nenhuma venda é
/// ruptura</b> (alvo inválido): a venda não mediu a demanda, mediu a falta. Confundir as duas
/// inverte o sinal — tratar ruptura como demanda zero é o anti-pattern do CLAUDE.md §6, e
/// ignorar a demanda zero genuína é o erro simétrico, que foi o que aconteceu aqui.
/// </para>
///
/// <para>
/// <b>Por que este arquivo existe:</b> até 27/08/2026 o loader só materializava os dias de
/// <b>ruptura</b>, então um par (SKU, loja) que tinha produto na prateleira e não vendia
/// nunca entrava na série — e é a situação da maior parte do sortimento farma. O modelo era
/// ajustado quase só onde houve movimento e cobrado justamente onde não há. Medido na
/// sugestão 125595 da Retiro, nos mesmos 563 pares: o viés do ML caiu de <b>+0,4387</b> para
/// <b>+0,0038</b> un./dia, a menor previsão de 0,3289 para 0,0015, e o MAE de 0,4397 para
/// 0,0652 — contra 0,0650 do ERP, que ficou idêntico nas duas execuções e serviu de controle.
/// Se alguém reduzir esta consulta de volta a <c>WHERE QuantidadeEmEstoque &lt;= 0</c>, o viés
/// volta inteiro e nenhum outro teste percebe.
/// </para>
///
/// <para>
/// Série <b>esparsa</b> de propósito: o CLAUDE.md exige isso de todo teste que toque em
/// feature derivada de venda, porque foi numa série com venda todo dia que o vazamento de
/// preço passou batido.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class ObservacoesDeEstoqueIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "observacoes-estoque";
    private const int LojaId = 8951;

    /// <summary>Tem venda em ao menos um dia — é o par que já existia antes da mudança.</summary>
    private const string SkuComVenda = "OBS-VENDE";

    /// <summary>
    /// <b>Nunca vendeu.</b> Antes da mudança este par não tinha série nenhuma e era invisível
    /// para o treino e para a comparação; é a classe de item que a mudança recupera.
    /// </summary>
    private const string SkuSoEstoque = "OBS-SO-ESTOQUE";

    private static readonly DateOnly DiaComVenda = new(2026, 3, 2);
    private static readonly DateOnly DiaComEstoqueSemVenda = new(2026, 3, 3);
    private static readonly DateOnly DiaEmRuptura = new(2026, 3, 4);

    private const decimal QuantidadeVendida = 5m;

    [Fact]
    public async Task Dia_com_estoque_e_sem_venda_e_demanda_zero_genuina()
    {
        var obs = await CarregarAsync();

        var dia = obs.SingleOrDefault(o => o.Sku == SkuComVenda && o.Data == DiaComEstoqueSemVenda);

        dia.Should().NotBeNull(
            "sem esta observação o modelo nunca vê 'tinha na prateleira e não saiu', que é a maioria do sortimento");
        dia!.Quantidade.Should().Be(0m);
        dia.EmRuptura.Should().BeFalse(
            "havia estoque, então o zero de venda mede demanda e não falta — este é o alvo válido que faltava");
    }

    [Fact]
    public async Task Dia_com_estoque_zerado_e_sem_venda_continua_sendo_ruptura()
    {
        var obs = await CarregarAsync();

        var dia = obs.SingleOrDefault(o => o.Sku == SkuComVenda && o.Data == DiaEmRuptura);

        dia.Should().NotBeNull();
        dia!.Quantidade.Should().Be(0m);
        dia.EmRuptura.Should().BeTrue(
            "sem estoque a venda zero mede a falta, não a demanda; contá-la como demanda zero é o "
            + "anti-pattern que enviesa o modelo para baixo em SKU com ruptura frequente");
    }

    /// <summary>
    /// A leitura do estoque não pode sobrescrever a venda do dia. Um snapshot positivo no dia
    /// que vendeu tem de deixar a quantidade intacta — zerá-la apagaria o alvo real.
    /// </summary>
    [Fact]
    public async Task Dia_que_vendeu_mantem_a_quantidade_vendida()
    {
        var obs = await CarregarAsync();

        var dia = obs.SingleOrDefault(o => o.Sku == SkuComVenda && o.Data == DiaComVenda);

        dia.Should().NotBeNull();
        dia!.Quantidade.Should().Be(QuantidadeVendida);
        dia.EmRuptura.Should().BeFalse();
    }

    /// <summary>
    /// O par que a mudança recupera: só tem estoque, nunca vendeu. Antes não existia série
    /// para ele, então ele não treinava o modelo nem podia ser comparado.
    /// </summary>
    [Fact]
    public async Task Par_que_nunca_vendeu_mas_tem_estoque_entra_na_serie()
    {
        var obs = await CarregarAsync();

        var doSku = obs.Where(o => o.Sku == SkuSoEstoque).ToList();

        doSku.Should().NotBeEmpty(
            "é a classe de item mais numerosa do sortimento farma; sem ela o treino vê só quem se move");
        doSku.Should().OnlyContain(o => o.Quantidade == 0m && !o.EmRuptura);
        doSku.Should().OnlyContain(o => o.ClasseAbc == "C",
            "quem não vendeu é classe C — o default é a classificação correta aqui, não um buraco");
    }

    private async Task<IReadOnlyList<DailyObservation>> CarregarAsync()
    {
        var redeId = await SeedAsync();
        var connStr = await fixture.GetStageConnectionStringAsync(TestContext.Current.CancellationToken);
        var loader = new StageObservationLoader(connStr, NullLogger.Instance);
        return await loader.LoadAsync(
            redeId, maxSkus: null, treinoAte: null, TestContext.Current.CancellationToken);
    }

    private async Task<int> SeedAsync()
    {
        var redeId = await EnsureRedeAsync("Rede Observacoes de Estoque", Slug);

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
    /// Três dias e dois SKUs, datas explícitas. Deliberadamente <b>esparso</b>: uma venda só,
    /// um dia com estoque e sem venda, um dia zerado. Faker aqui tornaria cada asserção uma
    /// coincidência.
    /// </summary>
    private static MemoryStream BuildZip()
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Observacoes", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(SkuComVenda, "Produto Que Vende", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
            new(SkuSoEstoque, "Produto Parado", "Genérico", "Antialérgico", "ACME", "Loratadina", "12cp 10mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>
        {
            new(DiaComVenda, LojaId, SkuComVenda, QuantidadeVendida, 10.50m, QuantidadeVendida * 10.50m),
        };

        var estoques = new List<EstoqueDiarioRow>
        {
            // Dia que vendeu, com estoque sobrando: a leitura do estoque não pode zerar a venda.
            new(DiaComVenda, LojaId, SkuComVenda, 10m),
            // Estoque positivo e nenhuma venda: demanda zero genuína.
            new(DiaComEstoqueSemVenda, LojaId, SkuComVenda, 7m),
            // Estoque zerado e nenhuma venda: ruptura.
            new(DiaEmRuptura, LojaId, SkuComVenda, 0m),
            // Par que nunca vendeu: só existe pela posição de estoque.
            new(DiaComEstoqueSemVenda, LojaId, SkuSoEstoque, 4m),
        };

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            .WithEstoquesDiarios(estoques)
            .WithCompras(new CompraFaker([LojaId], [SkuComVenda], DiaComVenda, DiaEmRuptura, seed: 950).Generate(1))
            .WithPromocoes([])
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], DiaComVenda, DiaEmRuptura, seed: 951).Generate(1))
            .Build();
    }
}
