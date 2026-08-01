using System.Net;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Cobre a propriedade de maior risco desta feature: uma rede não pode ler, listar
/// ou criar sessão de comparação fora do próprio escopo. Mesmo formato de
/// isolamento por rede validado em <see cref="MultiRedeIntegrationTests"/>, aplicado
/// aos endpoints de <c>/api/comparacoes</c>.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class ComparacoesIntegrationTests(AppHostFixture fixture)
{
    [Fact]
    public async Task GET_por_id_de_outra_rede_retorna_404_nao_403_nem_a_sessao()
    {
        var redeA = await EnsureRedeAsync("Comparacoes Rede A", "comparacoes-rede-a");
        var redeB = await EnsureRedeAsync("Comparacoes Rede B", "comparacoes-rede-b");

        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest(null), redeA);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);

        var comoOutraRede = await fixture.ComparacoesApi.GetAsync(criada.Content!.Id, redeB);

        comoOutraRede.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "rede B não pode nem saber que a sessão existe — 403 vazaria a existência da linha");
    }

    [Fact]
    public async Task Listagem_de_uma_rede_nao_inclui_sessao_de_outra()
    {
        var redeA = await EnsureRedeAsync("Comparacoes Rede C", "comparacoes-rede-c");
        var redeB = await EnsureRedeAsync("Comparacoes Rede D", "comparacoes-rede-d");

        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest(null), redeA);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);

        var listaB = await fixture.ComparacoesApi.ListAsync(redeB);

        listaB.IsSuccessStatusCode.Should().BeTrue();
        listaB.Content.Should().NotContain(s => s.Id == criada.Content!.Id,
            "a sessão da rede A não pode vazar para a listagem da rede B");
    }

    [Fact]
    public async Task POST_com_rede_inexistente_retorna_400()
    {
        var resp = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest(null), redeId: -1);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "ValidateRedeAsync deve barrar um redeId sem rede correspondente antes de tocar em ComparacaoSessoes");
    }

    /// <summary>
    /// Mesma abordagem de <see cref="MultiRedeIntegrationTests.EnsureRedeAsync"/>:
    /// cria a rede ou devolve a existente, porque o banco é persistente entre runs
    /// e o Slug é único.
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
}
