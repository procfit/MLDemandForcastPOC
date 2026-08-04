using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task DELETE_de_sessao_aguardando_dados_remove_e_sai_da_listagem()
    {
        var rede = await EnsureRedeAsync("Comparacoes Exclusao", "comparacoes-exclusao");

        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest("Para excluir"), rede);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = criada.Content!.Id;

        var exclusao = await fixture.ComparacoesApi.ExcluirAsync(id, rede);
        exclusao.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await fixture.ComparacoesApi.GetAsync(id, rede)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var lista = await fixture.ComparacoesApi.ListAsync(rede);
        lista.Content.Should().NotContain(s => s.Id == id);
    }

    /// <summary>
    /// O detalhe por item sai por <c>ON DELETE CASCADE</c> da FK, e não por código: o
    /// endpoint usa <c>ExecuteDelete</c>, que emite um DELETE cru e **não** faz cascata do
    /// lado do cliente. Se a cascata for removida do <c>EngineDbContext</c> algum dia, este
    /// teste falha por violação de FK — que é melhor do que sessão apagada e detalhe órfão
    /// ocupando a tabela para sempre, sem pai para dizer de quem era.
    /// </summary>
    [Fact]
    public async Task DELETE_leva_o_detalhe_por_item_junto()
    {
        var rede = await EnsureRedeAsync("Comparacoes Exclusao Itens", "comparacoes-exclusao-itens");

        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest("Com itens"), rede);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = criada.Content!.Id;

        await using (var db = await AbrirEngineAsync(CancellationToken.None))
        {
            db.ComparacaoSessaoItens.AddRange(
                new ComparacaoSessaoItem { SessaoId = id, LojaId = 1, Sku = "SKU-A", CompraSugeridaPbs = 10m },
                new ComparacaoSessaoItem { SessaoId = id, LojaId = 1, Sku = "SKU-B", CompraSugeridaPbs = 20m });
            await db.SaveChangesAsync();
        }

        var exclusao = await fixture.ComparacoesApi.ExcluirAsync(id, rede);
        exclusao.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var db = await AbrirEngineAsync(CancellationToken.None))
        {
            var restantes = await db.ComparacaoSessaoItens.CountAsync(i => i.SessaoId == id);
            restantes.Should().Be(0, "apagar a sessão tem de levar o detalhe por item junto");
        }
    }

    /// <summary>
    /// A recusa protege o job em voo, não o dado. Nas três fases em andamento existe uma
    /// carga, um treino ou uma comparação trabalhando pela sessão — apagá-la deixaria o
    /// worker escrevendo resultado para uma sessão que não existe mais.
    /// </summary>
    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados)]
    [InlineData(SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Comparando)]
    public async Task DELETE_em_fase_em_andamento_retorna_409_e_preserva_a_sessao(SessaoStatus fase)
    {
        var rede = await EnsureRedeAsync("Comparacoes Exclusao Fase", "comparacoes-exclusao-fase");

        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest($"Em {fase}"), rede);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = criada.Content!.Id;

        await using (var db = await AbrirEngineAsync(CancellationToken.None))
        {
            var sessao = await db.ComparacaoSessoes.SingleAsync(s => s.Id == id);
            sessao.Status = fase;
            sessao.AtualizadoEm = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var exclusao = await fixture.ComparacoesApi.ExcluirAsync(id, rede);

        exclusao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "409 e não 400: a requisição está bem formada e a mesma chamada funciona quando a fase terminar");

        (await fixture.ComparacoesApi.GetAsync(id, rede)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a sessão recusada tem de continuar existindo");
    }

    [Fact]
    public async Task DELETE_de_outra_rede_retorna_404_e_nao_apaga_nada()
    {
        var redeA = await EnsureRedeAsync("Comparacoes Exclusao Dona", "comparacoes-exclusao-dona");
        var redeB = await EnsureRedeAsync("Comparacoes Exclusao Vizinha", "comparacoes-exclusao-vizinha");

        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest("Da rede A"), redeA);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = criada.Content!.Id;

        var comoOutraRede = await fixture.ComparacoesApi.ExcluirAsync(id, redeB);

        comoOutraRede.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "404 e não 403 — um 403 confirmaria a quem sondasse que a sessão existe em outro inquilino");

        (await fixture.ComparacoesApi.GetAsync(id, redeA)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a sessão da rede A não pode ser apagada por um DELETE vindo da rede B");
    }

    private async Task<EngineDbContext> AbrirEngineAsync(CancellationToken ct)
    {
        var connStr = await fixture.GetEngineConnectionStringAsync(ct);
        var options = new DbContextOptionsBuilder<EngineDbContext>().UseSqlServer(connStr).Options;
        return new EngineDbContext(options);
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
