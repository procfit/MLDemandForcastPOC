using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Filtros combináveis e totalizadores da tela de itens comparados.
///
/// <para>
/// <b>Filtro e total precisam sair da MESMA cláusula.</b> Se divergirem, o comprador vê 31
/// itens na tela e um total apurado sobre 20.153 — e conclui sobre a loja 18 um número que
/// descreve a rede inteira. Por isso os casos abaixo sempre conferem o total contra o recorte
/// exibido, e não apenas se o filtro "funciona".
/// </para>
///
/// <para>
/// A sessão é semeada direto no banco: o que se afirma é a consulta, e produzi-la pelo caminho
/// legítimo exigiria importar um ZIP e treinar um modelo dentro de um teste de integração.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class FiltrosDosItensIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "filtros-itens";

    /// <summary>
    /// Precisa ser <b>o mesmo</b> literal de <c>ComparacoesEndpoints.FiltroAusente</c> e de
    /// <c>FiltroDeItens.Ausente</c> na Web. São três lugares em dois processos, e um valor
    /// divergente filtraria por uma categoria literalmente chamada assim — devolvendo tela
    /// vazia sem erro nenhum. O teste da Web fixa o mesmo literal do outro lado.
    /// </summary>
    private const string Ausente = "__sem__";

    [Fact]
    public async Task Filtros_combinados_estreitam_a_populacao_e_os_totais_acompanham()
    {
        var (redeId, sessaoId) = await SemearAsync();

        var tudo = await PaginaAsync(sessaoId, redeId);
        tudo.Total.Should().Be(6);
        tudo.TotalSemFiltro.Should().Be(6);
        tudo.Totais!.CompraPbsUnidades.Should().Be(60m);

        var soLoja = await PaginaAsync(sessaoId, redeId, lojaId: 1);
        soLoja.Total.Should().Be(4);
        soLoja.TotalSemFiltro.Should().Be(6, "o denominador é a sugestão inteira, e é ele que diz que isto é um recorte");
        soLoja.Totais!.CompraPbsUnidades.Should().Be(40m);

        // Loja 1 + Curva A: o exemplo que o documento do Julio usa. Duas condições ao mesmo
        // tempo, não a união delas.
        var combinado = await PaginaAsync(sessaoId, redeId, lojaId: 1, curva: "A");
        combinado.Total.Should().Be(2);
        combinado.Totais!.CompraPbsUnidades.Should().Be(20m);
        combinado.Itens.Should().OnlyContain(i => i.LojaId == 1 && i.Curva == "A");

        var comCategoria = await PaginaAsync(sessaoId, redeId, lojaId: 1, curva: "A", categoria: "ANALGESICO");
        comCategoria.Total.Should().Be(1);
        comCategoria.Totais!.CompraPbsUnidades.Should().Be(10m);
    }

    /// <summary>
    /// <b>A soma do braço de ML é nula, nunca zero, quando nenhum item do recorte tem cálculo.</b>
    /// Zero ali afirmaria "o ML mandaria não comprar nada" — o contrário de "não houve conta" —,
    /// e é a mesma regra que vale para as colunas do item. O <c>SUM</c> do SQL já devolve nulo
    /// nesse caso; o teste existe para ninguém "consertar" isso com um <c>?? 0</c>.
    /// </summary>
    [Fact]
    public async Task Sem_item_com_ml_no_recorte_a_soma_do_ml_e_nula_e_nao_zero()
    {
        var (redeId, sessaoId) = await SemearAsync();

        // A loja 2 foi semeada sem nenhum cálculo de ML.
        var pagina = await PaginaAsync(sessaoId, redeId, lojaId: 2);

        pagina.Total.Should().Be(2);
        pagina.Totais!.ItensComCompraMl.Should().Be(0);
        pagina.Totais.CompraMlUnidades.Should().BeNull();
        pagina.Totais.SobraMlUnidades.Should().BeNull();
        pagina.Totais.SobraMlValor.Should().BeNull();

        // E o lado do ERP continua somando: a ausência é do ML, não do recorte.
        pagina.Totais.CompraPbsUnidades.Should().Be(20m);
    }

    /// <summary>
    /// Recorte que não casa nenhum item: contagem zero nas unidades e <b>nulo</b> no braço de
    /// ML. É o caso em que o <c>GroupBy</c> não devolve linha nenhuma, e o valor de fallback
    /// tem de respeitar a mesma distinção.
    /// </summary>
    [Fact]
    public async Task Recorte_vazio_devolve_zero_em_unidades_e_nulo_no_ml()
    {
        var (redeId, sessaoId) = await SemearAsync();

        var pagina = await PaginaAsync(sessaoId, redeId, lojaId: 99);

        pagina.Total.Should().Be(0);
        pagina.Itens.Should().BeEmpty();
        pagina.Totais!.Itens.Should().Be(0);
        pagina.Totais.CompraPbsUnidades.Should().Be(0m);
        pagina.Totais.CompraMlUnidades.Should().BeNull("recorte vazio não é 'o ML comprava zero'");
    }

    /// <summary>
    /// O sentinela de ausência recorta os itens <b>sem</b> categoria — que é um recorte
    /// legítimo, e numa comparação executada antes da coluna existir é o recorte de todos os
    /// itens. Sem ele, "sem categoria" seria inalcançável pela query string.
    /// </summary>
    [Fact]
    public async Task Sentinela_de_ausencia_recorta_os_itens_sem_categoria()
    {
        var (redeId, sessaoId) = await SemearAsync();

        var pagina = await PaginaAsync(sessaoId, redeId, categoria: Ausente);

        pagina.Total.Should().Be(1);
        pagina.Itens.Should().OnlyContain(i => i.Categoria == null);
    }

    /// <summary>
    /// As opções oferecidas são as <b>presentes na sessão</b>, não o cadastro da rede. Oferecer
    /// uma categoria que nenhum item tem produz filtro que devolve tela vazia e parece defeito.
    /// </summary>
    [Fact]
    public async Task Opcoes_de_filtro_saem_do_que_a_sessao_tem()
    {
        var (redeId, sessaoId) = await SemearAsync();

        var resp = await fixture.ComparacoesApi.FiltrosDosItensAsync(
            sessaoId, redeId, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var f = resp.Content!;
        f.Lojas.Should().BeEquivalentTo([1, 2]);
        f.Categorias.Should().BeEquivalentTo(["ANALGESICO", "ANTIBIOTICO"]);
        f.TemItemSemCategoria.Should().BeTrue("um item foi semeado sem categoria, e a tela precisa oferecer esse recorte");
        f.Curvas.Should().BeEquivalentTo(["A", "B"]);
    }

    /// <summary>
    /// A exportação passa pela mesma cláusula da tela: uma planilha que trouxesse a população
    /// inteira quando o comprador filtrou uma loja seria pior que nenhuma — ele pediria 31
    /// itens e levaria 20 mil sem perceber.
    /// </summary>
    [Fact]
    public async Task Exportacao_respeita_o_filtro_da_tela()
    {
        var (redeId, sessaoId) = await SemearAsync();

        var resp = await fixture.ComparacoesApi.ExportarItensAsync(
            sessaoId, redeId, lojaId: 1, curva: "A", ct: TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content!.Should().HaveCount(2);
        resp.Content.Should().OnlyContain(i => i.LojaId == 1 && i.Curva == "A");
    }

    /// <summary>Sessão de outra rede: 404, e não a planilha do vizinho.</summary>
    [Fact]
    public async Task Filtros_e_exportacao_de_outra_rede_respondem_404()
    {
        var (dona, sessaoId) = await SemearAsync();
        var vizinha = await EnsureRedeAsync("Rede Filtros Vizinha", "filtros-itens-vizinha");
        vizinha.Should().NotBe(dona);

        (await fixture.ComparacoesApi.FiltrosDosItensAsync(sessaoId, vizinha, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await fixture.ComparacoesApi.ExportarItensAsync(sessaoId, vizinha, ct: TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task<SessaoItensPage> PaginaAsync(
        Guid sessaoId, int redeId, int? lojaId = null, string? categoria = null, string? curva = null) =>
        Executar(async () =>
        {
            var resp = await fixture.ComparacoesApi.ItensAsync(
                sessaoId, redeId, skip: 0, take: 50, orderBy: null, desc: true,
                lojaId: lojaId, categoria: categoria, curva: curva,
                ct: TestContext.Current.CancellationToken);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, because: resp.Error?.Message ?? "sem detalhe");
            return resp.Content!;
        });

    private static Task<T> Executar<T>(Func<Task<T>> f) => f();

    /// <summary>
    /// Seis itens desenhados para os recortes: duas lojas, duas curvas, duas categorias mais um
    /// item sem categoria, e a loja 2 inteira <b>sem</b> cálculo de ML. Compras em múltiplos de
    /// 10 para cada soma esperada ser conferível de cabeça.
    /// </summary>
    private async Task<(int RedeId, Guid SessaoId)> SemearAsync()
    {
        var redeId = await EnsureRedeAsync("Rede Filtros de Itens", Slug);
        var sessaoId = Guid.CreateVersion7();

        await using var db = await AbrirEngineAsync(TestContext.Current.CancellationToken);

        var antigas = await db.ComparacaoSessoes.Where(s => s.RedeId == redeId && s.Nome == Slug).ToListAsync();
        if (antigas.Count > 0)
        {
            db.ComparacaoSessoes.RemoveRange(antigas);
            await db.SaveChangesAsync();
        }

        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = sessaoId,
            RedeId = redeId,
            Nome = Slug,
            Status = SessaoStatus.AguardandoQuestionario,
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow,
            SugestaoId = 555,
        });

        db.ComparacaoSessaoItens.AddRange(
            Item(sessaoId, 1, "S1", "A", "ANALGESICO", compraPbs: 10m, compraMl: 1m),
            Item(sessaoId, 1, "S2", "A", "ANTIBIOTICO", compraPbs: 10m, compraMl: 1m),
            Item(sessaoId, 1, "S3", "B", "ANALGESICO", compraPbs: 10m, compraMl: 1m),
            Item(sessaoId, 1, "S4", "B", categoria: null, compraPbs: 10m, compraMl: 1m),
            Item(sessaoId, 2, "S5", "A", "ANALGESICO", compraPbs: 10m, compraMl: null),
            Item(sessaoId, 2, "S6", "B", "ANTIBIOTICO", compraPbs: 10m, compraMl: null));

        await db.SaveChangesAsync();
        return (redeId, sessaoId);
    }

    private static ComparacaoSessaoItem Item(
        Guid sessaoId, int lojaId, string sku, string curva, string? categoria,
        decimal compraPbs, decimal? compraMl) => new()
        {
            SessaoId = sessaoId,
            LojaId = lojaId,
            Sku = sku,
            NomeProduto = $"Produto {sku}",
            Curva = curva,
            Categoria = categoria,
            CompraSugeridaPbs = compraPbs,
            CompraSugeridaMl = compraMl,
            VendidoNaJanela = 1m,
            DemandaDiaPbs = 1m,
            DemandaDiaMl = compraMl is null ? null : 1m,
            DemandaDiaReal = 1m,
            SobraPbsUnidades = 5m,
            SobraMlUnidades = compraMl is null ? null : 4m,
            SobraPbsValor = 50m,
            SobraMlValor = compraMl is null ? null : 40m,
            JanelaAlemDoHistorico = false,
        };

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
