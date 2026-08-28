using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Tests.Shared.Xlsx;
using Microsoft.EntityFrameworkCore;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Exclusão de uma célula de cobertura (mês × brick) dos dados de mercado.
///
/// <para>
/// <b>A célula é a única unidade de exclusão que o modelo permite:</b> as observações não
/// guardam de qual envio vieram, porque a recarga substitui por (mês, brick). O que estes
/// testes fixam é a fronteira do DELETE — sai a célula pedida e <b>só</b> ela: o outro mês do
/// mesmo brick fica, o catálogo de produtos fica (todos os meses o usam), o painel de PDVs do
/// brick fica (os outros meses dele o usam) e o histórico de envios fica. Um DELETE mais largo
/// aqui não falharia teste nenhum dos demais — falharia a série do comprador, meses depois.
/// </para>
///
/// <para>
/// O caminho é o real — XLSX pelo endpoint de upload, fila, <c>MercadoProcessor</c> — porque a
/// fronteira só significa algo sobre dados que chegaram como chegam em produção.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class MercadoCoberturaExclusaoIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "mercado-exclusao";
    private const string Brick = "526";
    private const string Bandeira = "CONCORRENTES";
    private const string Ean = "7891721201806";

    private static readonly DateOnly MesQueSai = new(2026, 5, 1);
    private static readonly DateOnly MesQueFica = new(2026, 6, 1);

    [Fact]
    public async Task Excluir_uma_celula_preserva_o_resto_da_serie_e_o_catalogo()
    {
        var redeId = await SemearDoisMesesAsync();

        var antes = await CoberturaAsync(redeId);
        antes.Should().HaveCount(2, "o arquivo cobre dois meses do mesmo brick");

        var resp = await fixture.MercadoApi.ExcluirCoberturaAsync(
            redeId, MesQueSai, Brick, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var depois = await CoberturaAsync(redeId);
        depois.Should().ContainSingle("só a célula pedida pode sair")
            .Which.Mes.Should().Be(MesQueFica);

        // O catálogo e o painel do brick continuam de pé: o mês que ficou ainda os usa, e
        // apagá-los junto quebraria a parte C (join por EAN/CNPJ) para a série inteira.
        await using var db = await AbrirEngineAsync(TestContext.Current.CancellationToken);
        (await db.MercadoProdutos.CountAsync(p => p.RedeId == redeId && p.Ean == Ean,
            TestContext.Current.CancellationToken)).Should().Be(1, "produto é catálogo, não observação");
        (await db.MercadoBrickPdvs.CountAsync(p => p.RedeId == redeId && p.Brick == Brick,
            TestContext.Current.CancellationToken)).Should().BeGreaterThan(0, "o painel do brick serve aos meses restantes");
        (await db.MercadoCargas.CountAsync(c => c.RedeId == redeId,
            TestContext.Current.CancellationToken)).Should().BeGreaterThan(0, "o histórico de envios não é apagado");
    }

    /// <summary>
    /// Célula inexistente: 404 — e é o mesmo código para célula de outra rede, porque o
    /// escopo entra na chave. Um 200 idempotente esconderia da tela que ela está
    /// desatualizada; um 403 confirmaria a quem sondasse que o recorte existe em outro
    /// inquilino.
    /// </summary>
    [Fact]
    public async Task Celula_inexistente_e_celula_de_outra_rede_respondem_404()
    {
        var dona = await SemearDoisMesesAsync();

        (await fixture.MercadoApi.ExcluirCoberturaAsync(
            dona, new DateOnly(2031, 1, 1), Brick, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var vizinha = await EnsureRedeAsync("Rede Mercado Exclusao Vizinha", "mercado-exclusao-vizinha");
        (await fixture.MercadoApi.ExcluirCoberturaAsync(
            vizinha, MesQueFica, Brick, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a célula existe, mas na rede da dona — para a vizinha ela não pode nem parecer existir");
    }

    /// <summary>
    /// Envio em voo bloqueia a exclusão: o <c>MercadoProcessor</c> grava numa transação
    /// própria, e um DELETE concorrente ao bulk dele termina em célula meio-cheia ou em
    /// deadlock — com a culpa caindo em quem clicou. Mesmo desenho do bloqueio de sessão.
    /// </summary>
    [Fact]
    public async Task Envio_em_processamento_bloqueia_a_exclusao_com_409()
    {
        var redeId = await SemearDoisMesesAsync();

        // Carga pendente semeada direto: deixar uma real "em voo" no exato instante do
        // DELETE seria corrida de timing, e é justamente corrida que o 409 existe para evitar.
        await using (var db = await AbrirEngineAsync(TestContext.Current.CancellationToken))
        {
            db.MercadoCargas.Add(new MercadoCarga
            {
                Id = Guid.CreateVersion7(),
                RedeId = redeId,
                Status = MercadoCargaStatus.Pendente,
                DataAgendamento = DateTimeOffset.UtcNow.AddHours(2),
                NomeArquivoOriginal = "em-voo.xlsx",
                BlobKey = "em-voo.xlsx",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var resp = await fixture.MercadoApi.ExcluirCoberturaAsync(
                redeId, MesQueFica, Brick, TestContext.Current.CancellationToken);

            resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            // A pendente é removida no fim, senão o MercadoWorker a reclamaria, falharia no
            // blob inexistente e sujaria o próximo teste desta rede.
            await using var db = await AbrirEngineAsync(TestContext.Current.CancellationToken);
            await db.MercadoCargas
                .Where(c => c.RedeId == redeId && c.NomeArquivoOriginal == "em-voo.xlsx")
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task<List<MercadoCoberturaResposta>> CoberturaAsync(int redeId)
    {
        var resp = await fixture.MercadoApi.CoberturaAsync(redeId, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return resp.Content!;
    }

    /// <summary>
    /// Sobe pelo caminho real um XLSX com dois meses do mesmo brick — o mínimo que permite
    /// afirmar "saiu um e ficou o outro". Reenvio é idempotente por desenho (substituição por
    /// (mês, brick)), então cada teste pode semear sem se coordenar com os demais.
    /// </summary>
    private async Task<int> SemearDoisMesesAsync()
    {
        var redeId = await EnsureRedeAsync("Rede Mercado Exclusao", Slug);

        using var xlsx = new IqviaXlsxBuilder()
            .WithColunas(
                "Ean", "Produto Desc Longa", "Laboratorio", "Molecula",
                "Areas da Farmacia", "Nec 1", "Forma 3", "Classe 4",
                IqviaXlsxBuilder.Medida(Brick, Bandeira, "202605", "Unidades"),
                IqviaXlsxBuilder.Medida(Brick, Bandeira, "202605", "Real CPP"),
                IqviaXlsxBuilder.Medida(Brick, Bandeira, "202606", "Unidades"),
                IqviaXlsxBuilder.Medida(Brick, Bandeira, "202606", "Real CPP"))
            .AddLinha(Ean, "GLIFAGE XR 500MG X30", "MERCK", "METFORMINA",
                null, null, null, null, 10, 100.0, 7, 70.0)
            // Formato real da aba de PDVs: "BANDEIRA - CNPJ". Sem o prefixo o parser descarta
            // a linha em silêncio, e o teste do painel afirmaria ausência achando que afirma presença.
            .AddPdv(Brick, "CONCORRENTES - 00000000000000")
            .Build();

        xlsx.Position = 0;
        var upload = await fixture.MercadoApi.UploadAsync(
            new StreamPart(xlsx, "iqvia-exclusao.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            redeId, TestContext.Current.CancellationToken);
        upload.StatusCode.Should().Be(HttpStatusCode.Accepted, because: upload.Error?.Message ?? "sem detalhe");

        await AguardarCargaAsync(upload.Content!.Id, redeId);
        return redeId;
    }

    private async Task AguardarCargaAsync(Guid id, int redeId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.MercadoApi.GetCargaAsync(id, redeId, TestContext.Current.CancellationToken);
            if (resp.Content is { } carga && carga.Status is "Concluida" or "Falha")
            {
                carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Carga de mercado {id} não concluiu em 2 min.");
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
