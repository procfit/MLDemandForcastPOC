using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// O catálogo de códigos de barras da rede e a tela de oportunidades de sortimento
/// (F16 parte C, grupo A).
///
/// <para>
/// <b>O que estes testes protegem é a direção do erro.</b> A tela afirma "o mercado vende
/// isto e você não tem". Um catálogo que se perde no import seguinte, ou que vaza entre
/// redes, ou que não normaliza o código de barras, não produz tela vazia — produz tela
/// cheia de itens que a rede já vende, e o comprador compra o que já tem.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class OportunidadesIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "oportunidades";

    [Fact]
    public async Task O_catalogo_faz_round_trip_e_a_recarga_substitui_a_rede_inteira()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Rede das oportunidades", Slug);

        await SubstituirCatalogoAsync(redeId,
        [
            new() { RedeId = redeId, Ean = "7891721201806", Sku = "100", Nome = "GLIFAGE XR 500MG" },
            new() { RedeId = redeId, Ean = "7896714231143", Sku = "200", Nome = "NEOSORO AD" },
        ]);

        await using (var db = await AbrirEngineAsync(ct))
        {
            (await db.RedeCatalogoEans.CountAsync(c => c.RedeId == redeId, ct)).Should().Be(2);
        }

        // Segundo envio com UM item: o catálogo é retrato do cadastro, não série histórica.
        // O item que saiu do cadastro tem de sair da tabela — senão a tela continua
        // deixando de oferecer como oportunidade um produto que a rede descadastrou.
        await SubstituirCatalogoAsync(redeId,
        [
            new() { RedeId = redeId, Ean = "7891721201806", Sku = "100", Nome = "GLIFAGE XR 500MG" },
        ]);

        await using var leitura = await AbrirEngineAsync(ct);
        var restantes = await leitura.RedeCatalogoEans
            .Where(c => c.RedeId == redeId).Select(c => c.Ean).ToListAsync(ct);

        restantes.Should().BeEquivalentTo(["7891721201806"]);
    }

    [Fact]
    public async Task Catalogo_de_uma_rede_nao_alcanca_a_outra()
    {
        // A substituição é por RedeId. Um DELETE sem o filtro apagaria o catálogo do
        // vizinho, e a tela dele passaria a oferecer o cadastro inteiro como oportunidade
        // de sortimento — dado comercial de um inquilino estragando a tela de outro.
        var ct = TestContext.Current.CancellationToken;
        var redeA = await EnsureRedeAsync("Oportunidades rede A", Slug + "-a");
        var redeB = await EnsureRedeAsync("Oportunidades rede B", Slug + "-b");

        await SubstituirCatalogoAsync(redeA,
            [new() { RedeId = redeA, Ean = "111", Sku = "A1", Nome = "DA REDE A" }]);
        await SubstituirCatalogoAsync(redeB,
            [new() { RedeId = redeB, Ean = "222", Sku = "B1", Nome = "DA REDE B" }]);

        await SubstituirCatalogoAsync(redeA,
            [new() { RedeId = redeA, Ean = "333", Sku = "A2", Nome = "DA REDE A, DE NOVO" }]);

        await using var db = await AbrirEngineAsync(ct);

        (await db.RedeCatalogoEans.Where(c => c.RedeId == redeB).Select(c => c.Ean).ToListAsync(ct))
            .Should().BeEquivalentTo(["222"], "a recarga da rede A não pode tocar a rede B");
        (await db.RedeCatalogoEans.Where(c => c.RedeId == redeA).Select(c => c.Ean).ToListAsync(ct))
            .Should().BeEquivalentTo(["333"]);
    }

    [Fact]
    public async Task O_mesmo_ean_em_duas_redes_convive()
    {
        // A PK é (RedeId, Ean), não Ean. Duas redes vendem o mesmo produto, e um EAN
        // global faria a segunda rede estourar violação de chave no import.
        var ct = TestContext.Current.CancellationToken;
        var redeA = await EnsureRedeAsync("Oportunidades EAN comum A", Slug + "-c1");
        var redeB = await EnsureRedeAsync("Oportunidades EAN comum B", Slug + "-c2");

        await SubstituirCatalogoAsync(redeA,
            [new() { RedeId = redeA, Ean = "7891721201806", Sku = "1", Nome = "GLIFAGE, REDE A" }]);
        await SubstituirCatalogoAsync(redeB,
            [new() { RedeId = redeB, Ean = "7891721201806", Sku = "9", Nome = "GLIFAGE, REDE B" }]);

        await using var db = await AbrirEngineAsync(ct);
        var linhas = await db.RedeCatalogoEans
            .Where(c => c.Ean == "7891721201806" && (c.RedeId == redeA || c.RedeId == redeB))
            .ToListAsync(ct);

        linhas.Should().HaveCount(2);
        linhas.Select(l => l.Sku).Should().BeEquivalentTo(["1", "9"]);
    }

    // --- apoio -----------------------------------------------------------------------

    /// <summary>
    /// Substitui o catálogo da rede inteiro, na mesma transação. É a operação que o import
    /// vai fazer de verdade — aqui ela é reproduzida via EF para o teste do schema não
    /// depender do pipeline de import ainda não existir.
    /// </summary>
    private async Task SubstituirCatalogoAsync(int redeId, List<RedeCatalogoEan> itens)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AbrirEngineAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.RedeCatalogoEans.Where(c => c.RedeId == redeId).ExecuteDeleteAsync(ct);
        db.RedeCatalogoEans.AddRange(itens);
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
    }

    private async Task<EngineDbContext> AbrirEngineAsync(CancellationToken ct)
    {
        var connStr = await fixture.GetEngineConnectionStringAsync(ct);
        var options = new DbContextOptionsBuilder<EngineDbContext>().UseSqlServer(connStr).Options;
        return new EngineDbContext(options);
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
}
