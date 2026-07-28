using System.Net;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Cenário que motivou F10. Antes do isolamento por rede, o CargaProcessor fazia
/// <c>DELETE FROM dbo.{tabela}</c> sem filtro: o import da segunda rede apagava
/// o Stage da primeira.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class MultiRedeIntegrationTests(AppHostFixture fixture)
{
    [Fact]
    public async Task Import_de_duas_redes_preserva_os_dados_de_ambas()
    {
        // Arrange — contagens de loja diferentes dão a cada rede uma assinatura
        // verificável no Stage.
        //
        // Redes próprias, não a demo (id 1): a UI escreve na demo, e `dotnet test`
        // na solução roda os projetos de teste em paralelo — o E2E subindo o próprio
        // AppHost importaria na mesma rede e as contagens divergiriam. A isolação do
        // teste não pode depender de ninguém mais tocar num inquilino compartilhado.
        var redeA = await EnsureRedeAsync("Rede A", "rede-a");
        var redeB = await EnsureRedeAsync("Rede B", "rede-b");

        using var zipA = BuildZip(qtdLojas: 3, seed: 200);
        using var zipB = BuildZip(qtdLojas: 2, seed: 300);

        // Act
        var cargaA = await ImportarAsync(zipA, "rede-a.zip", redeA);
        var cargaB = await ImportarAsync(zipB, "rede-b.zip", redeB);

        // Assert — cada rede mantém exatamente as suas lojas
        var lojasA = await fixture.StageApi.BrowseAsync("lojas", redeId: redeA);
        var lojasB = await fixture.StageApi.BrowseAsync("lojas", redeId: redeB);

        lojasA.Content!.Total.Should().Be(3,
            "o import da rede B não pode apagar o Stage da rede A");
        lojasB.Content!.Total.Should().Be(2);

        cargaA.LinhasImportadas.Should().BeGreaterThan(0);
        cargaB.LinhasImportadas.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Fixa a semântica de "cada import é dono completo do Stage da sua rede":
    /// reimportar substitui, não acumula. Sem este teste, alguém pode "corrigir"
    /// o DELETE escopado transformando-o em append.
    /// </summary>
    [Fact]
    public async Task Reimport_na_mesma_rede_substitui_o_conteudo_anterior()
    {
        var redeC = await EnsureRedeAsync("Rede C", "rede-c");

        using var primeiro = BuildZip(qtdLojas: 4, seed: 400);
        using var segundo = BuildZip(qtdLojas: 2, seed: 401);

        await ImportarAsync(primeiro, "rede-c-1.zip", redeC);
        var apos1 = await fixture.StageApi.BrowseAsync("lojas", redeId: redeC);
        apos1.Content!.Total.Should().Be(4);

        await ImportarAsync(segundo, "rede-c-2.zip", redeC);
        var apos2 = await fixture.StageApi.BrowseAsync("lojas", redeId: redeC);

        apos2.Content!.Total.Should().Be(2,
            "reimportar a mesma rede é refresh completo, não acúmulo");
    }

    /// <summary>
    /// Cria a rede ou devolve a existente. O Slug é único, então sem isto a segunda
    /// execução do teste falharia com 409 — os containers do Aspire são persistentes
    /// e o banco sobrevive entre runs.
    /// </summary>
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

    private async Task<CargaStageView> ImportarAsync(Stream zip, string nome, int redeId)
    {
        zip.Position = 0;
        var resposta = await fixture.ImportsApi.UploadAsync(
            new StreamPart(zip, nome, "application/zip"), redeId);

        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var carga = await fixture.WaitForCargaAsync(resposta.Content!.Id);
        carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");
        return carga;
    }

    /// <summary>
    /// Gera um ZIP mínimo mas válido. A contagem de lojas é a assinatura que os
    /// testes verificam no Stage.
    /// <para>
    /// Os fakers sorteiam as chaves, então volume alto em janela curta colide com as
    /// PKs do Stage e a carga falha por motivo alheio ao que se está testando.
    /// Defesas: janela de um ano (dilui <c>Vendas</c>/<c>EstoquesDiarios</c>) e uma
    /// única linha de IQVIA — a PK dela é (Mes, PrincipioAtivo, UF), e com um só
    /// princípio ativo e uma só UF duas linhas colidem sempre que caem no mesmo mês.
    /// </para>
    /// </summary>
    private static Stream BuildZip(int qtdLojas, int seed)
    {
        var lojas = new LojaFaker(seed: seed).Generate(qtdLojas);
        var produtos = new ProdutoFaker(seed: seed + 1).Generate(4);
        var lojaIds = lojas.Select(l => l.LojaId).ToList();
        var skus = produtos.Select(p => p.Sku).ToList();
        var inicio = new DateOnly(2026, 1, 1);
        var fim = new DateOnly(2026, 12, 31);

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(new VendaFaker(lojaIds, skus, inicio, fim, seed: seed + 2).Generate(8))
            .WithEstoquesDiarios(new EstoqueDiarioFaker(lojaIds, skus, inicio, fim, seed: seed + 3).Generate(8))
            .WithCompras(new CompraFaker(lojaIds, skus, inicio, fim, seed: seed + 4).Generate(2))
            .WithPromocoes(new PromocaoFaker(lojaIds, skus, inicio, fim, seed: seed + 5).Generate(1))
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], inicio, fim, seed: seed + 6).Generate(1))
            .Build();
    }
}
