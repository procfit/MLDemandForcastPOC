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

    /// <summary>
    /// <b>O incidente que motivou o endpoint:</b> o arquivo da IQVIA de uma rede foi enviado
    /// na outra. Desfazer o envio tem de remover TUDO que ele deixou na rede errada — as
    /// observações, o catálogo, o painel de PDVs (que carrega CNPJs das lojas da rede certa:
    /// dado identificável de outro inquilino) e a própria linha do histórico, que exibia nome
    /// de arquivo e cobertura aos usuários da rede errada. Sobra zero, e o teste conta linha a
    /// linha em cada tabela porque "quase zero" aqui é vazamento entre inquilinos.
    /// </summary>
    [Fact]
    public async Task Desfazer_o_envio_nao_deixa_nada_do_arquivo_na_rede()
    {
        var redeErrada = await EnsureRedeAsync("Rede Mercado Envio Errado", "mercado-envio-errado");
        await LimparMercadoDaRedeAsync(redeErrada);
        var cargaId = await EnviarArquivoAsync(redeErrada);

        var resp = await fixture.MercadoApi.ExcluirEnvioAsync(
            cargaId, redeErrada, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CoberturaAsync(redeErrada)).Should().BeEmpty();

        await using var db = await AbrirEngineAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        (await db.MercadoObservacoes.CountAsync(o => o.RedeId == redeErrada, ct)).Should().Be(0);
        (await db.MercadoProdutos.CountAsync(x => x.RedeId == redeErrada, ct)).Should().Be(0,
            "sem nenhuma observação restante, o catálogo do arquivo alheio é órfão e sai");
        (await db.MercadoBrickPdvs.CountAsync(x => x.RedeId == redeErrada, ct)).Should().Be(0,
            "o painel carrega CNPJs das lojas da outra rede — é a parte mais sensível do vazamento");
        (await db.MercadoCargas.CountAsync(c => c.RedeId == redeErrada, ct)).Should().Be(0,
            "a linha do histórico exibia o arquivo alheio na tela da rede errada; desfazer inclui o rastro");
    }

    /// <summary>
    /// A contraparte do teste acima: com DOIS envios de meses distintos, desfazer um preserva
    /// o outro por inteiro — inclusive catálogo e painel, que o mês restante ainda referencia.
    /// A varredura é de órfãos, não de tudo.
    /// </summary>
    [Fact]
    public async Task Desfazer_um_envio_preserva_o_que_outro_envio_ainda_usa()
    {
        var redeId = await EnsureRedeAsync("Rede Mercado Dois Envios", "mercado-dois-envios");
        await LimparMercadoDaRedeAsync(redeId);
        var cargaMaio = await EnviarArquivoAsync(redeId, somenteMes: "202605");
        await EnviarArquivoAsync(redeId, somenteMes: "202606");

        var resp = await fixture.MercadoApi.ExcluirEnvioAsync(
            cargaMaio, redeId, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CoberturaAsync(redeId)).Should().ContainSingle().Which.Mes.Should().Be(MesQueFica);

        await using var db = await AbrirEngineAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        (await db.MercadoProdutos.CountAsync(x => x.RedeId == redeId && x.Ean == Ean, ct)).Should().Be(1,
            "o mês que ficou ainda referencia o EAN — a varredura é de órfãos, não de tudo");
        (await db.MercadoBrickPdvs.CountAsync(x => x.RedeId == redeId && x.Brick == Brick, ct))
            .Should().BeGreaterThan(0);
        (await db.MercadoCargas.CountAsync(c => c.RedeId == redeId, ct)).Should().Be(1,
            "só a linha do envio desfeito sai do histórico");
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
        await EnviarArquivoAsync(redeId);
        return redeId;
    }

    /// <summary>
    /// Envia um XLSX pelo caminho real e devolve o id da carga concluída. Com
    /// <paramref name="somenteMes"/>, o arquivo cobre um único mês (AAAAMM); sem, cobre
    /// 2026-05 e 2026-06.
    /// </summary>
    private async Task<Guid> EnviarArquivoAsync(int redeId, string? somenteMes = null)
    {
        string[] meses = somenteMes is null ? ["202605", "202606"] : [somenteMes];
        var colunas = new List<string>
        {
            "Ean", "Produto Desc Longa", "Laboratorio", "Molecula",
            "Areas da Farmacia", "Nec 1", "Forma 3", "Classe 4",
        };
        var valores = new List<object?> { Ean, "GLIFAGE XR 500MG X30", "MERCK", "METFORMINA", null, null, null, null };
        foreach (var m in meses)
        {
            colunas.Add(IqviaXlsxBuilder.Medida(Brick, Bandeira, m, "Unidades"));
            colunas.Add(IqviaXlsxBuilder.Medida(Brick, Bandeira, m, "Real CPP"));
            valores.Add(10);
            valores.Add(100.0);
        }

        using var xlsx = new IqviaXlsxBuilder()
            .WithColunas([.. colunas])
            .AddLinha([.. valores])
            // Formato real da aba de PDVs: "BANDEIRA - CNPJ". Sem o prefixo o parser descarta
            // a linha em silêncio, e o teste do painel afirmaria ausência achando que afirma presença.
            .AddPdv(Brick, "CONCORRENTES - 00000000000000")
            .Build();

        xlsx.Position = 0;
        var upload = await fixture.MercadoApi.UploadAsync(
            new StreamPart(xlsx, $"iqvia-exclusao-{somenteMes ?? "completo"}.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            redeId, TestContext.Current.CancellationToken);
        upload.StatusCode.Should().Be(HttpStatusCode.Accepted, because: upload.Error?.Message ?? "sem detalhe");

        await AguardarCargaAsync(upload.Content!.Id, redeId);
        return upload.Content.Id;
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

    /// <summary>
    /// Zera o mercado da rede antes do teste. Necessário porque o volume do SQL é persistente
    /// e a rede é reaproveitada entre execuções: as cargas do histórico acumulam, e uma
    /// asserção de contagem absoluta passaria na primeira execução e falharia na terceira —
    /// que foi exatamente como este arquivo quebrou.
    /// </summary>
    private async Task LimparMercadoDaRedeAsync(int redeId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AbrirEngineAsync(ct);
        await db.MercadoObservacoes.Where(x => x.RedeId == redeId).ExecuteDeleteAsync(ct);
        await db.MercadoProdutos.Where(x => x.RedeId == redeId).ExecuteDeleteAsync(ct);
        await db.MercadoBrickPdvs.Where(x => x.RedeId == redeId).ExecuteDeleteAsync(ct);
        await db.MercadoCargas.Where(x => x.RedeId == redeId).ExecuteDeleteAsync(ct);
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
