using System.Globalization;
using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Microsoft.EntityFrameworkCore;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Vínculo entre o envio do comprador e a sessão de comparação: o ZIP declara qual
/// sugestão do PBS ele traz, e é o Worker que transcreve essa declaração para a sessão.
///
/// <para>
/// Os dois campos afirmados aqui não são cosméticos. <c>SugestaoDataHora</c> é de onde
/// sai o corte anti-vazamento do treino, e a comparação se recusa a rodar sem ele;
/// <c>SugestaoTipoCalculo</c> diz contra qual dos dois métodos do ERP a disputa acontece.
/// Sem os dois gravados aqui, a fase seguinte não tem como existir.
/// </para>
///
/// <para>
/// O caminho da inviabilidade também vive aqui, e não num teste de unidade, porque o que
/// se quer provar é que a sessão <b>chega</b> a <c>Inviavel</c> — passando pelo upload,
/// pela fila e pelo Worker —, não que uma função devolveu um texto.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class SessaoManifestoIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "sessao-manifesto";
    private const int LojaId = 9701;
    private const string Sku = "SES-A";
    private const long SugestaoId = 7101;
    private const byte TipoCalculo = 2;

    /// <summary>
    /// Itens que o extrator não achou no cadastro do PBS. Diferente de zero de propósito: é a
    /// travessia do valor que se afirma, e zero seria indistinguível de coluna não escrita.
    /// </summary>
    private const int SkusSemCadastro = 3;

    private static readonly DateOnly Inicio = new(2026, 1, 1);
    private static readonly DateOnly Fim = new(2026, 8, 31);
    private static readonly DateTime SugestaoDataHora = new(2026, 7, 1, 9, 30, 0);

    [Fact]
    public async Task ZIP_com_declaracao_grava_a_sugestao_e_o_retrato_na_sessao()
    {
        var redeId = await EnsureRedeAsync("Rede Sessao Manifesto", Slug);
        var sessaoId = await CriarSessaoAsync(redeId, "Com declaracao");

        await EnviarAsync(sessaoId, redeId, comManifesto: true);

        var sessao = await AguardarSessaoAsync(sessaoId, redeId);

        sessao.MotivoInviabilidade.Should().BeNull(
            because: "o envio declara a sugestão, então nada aqui é inviável");
        sessao.SugestaoId.Should().Be(SugestaoId);
        sessao.SugestaoDescricao.Should().Be("Sugestao da sessao");
        sessao.SugestaoDataHora.Should().Be(SugestaoDataHora,
            "é desta data que sai o corte anti-vazamento do treino da fase seguinte");
        sessao.SugestaoTipoCalculo.Should().Be(TipoCalculo,
            "a comparação precisa saber contra qual dos dois métodos do ERP ela disputa");

        // Lido do banco porque a view da sessão não expõe o campo: quem o consome é a
        // materialização do resultado, não a tela de acompanhamento. É a única ponte entre o
        // manifesto — que vive num diretório temporário apagado no fim do import — e o aviso
        // "N itens sem cadastro" que o comprador precisa ler no resultado.
        var ct = TestContext.Current.CancellationToken;
        var connStr = await fixture.GetEngineConnectionStringAsync(ct);
        var options = new DbContextOptionsBuilder<EngineDbContext>().UseSqlServer(connStr).Options;
        await using var db = new EngineDbContext(options);

        var skusSemCadastro = await db.ComparacaoSessoes.AsNoTracking()
            .Where(s => s.Id == sessaoId)
            .Select(s => s.SkusSemCadastro)
            .FirstAsync(ct);

        skusSemCadastro.Should().Be(SkusSemCadastro,
            "sem isto gravado, o aviso de itens sem cadastro morre no log do import");
    }

    /// <summary>
    /// Rede própria, e não a de <see cref="ZIP_com_declaracao_grava_a_sugestao_e_o_retrato_na_sessao"/>:
    /// aquela sessão segue viva para treino e comparação depois de a sugestão ser gravada, e uma
    /// rede só admite uma sessão em voo por vez — o Stage é por rede e cada envio o substitui
    /// inteiro. Compartilhar a rede faria este caso ser recusado no upload, por concorrência, em
    /// vez de exercitar a inviabilidade.
    /// </summary>
    [Fact]
    public async Task ZIP_sem_declaracao_deixa_a_sessao_inviavel_e_nao_em_falha()
    {
        var redeId = await EnsureRedeAsync("Rede Sessao Manifesto Sem Declaracao", $"{Slug}-sem-decl");
        var sessaoId = await CriarSessaoAsync(redeId, "Sem declaracao");

        await EnviarAsync(sessaoId, redeId, comManifesto: false);

        var sessao = await AguardarSessaoAsync(sessaoId, redeId);

        sessao.Status.Should().Be("Inviavel",
            "não houve erro: faltou pré-condição, e o remédio é gerar o envio de novo");
        sessao.MensagemErro.Should().BeNull("Inviavel é resposta, não falha");
        sessao.MotivoInviabilidade.Should().NotBeNullOrWhiteSpace();
        sessao.MotivoInviabilidade.Should().Contain("extrator",
            "quem lê é comprador de farmácia: o texto tem de terminar numa próxima ação");
        sessao.SugestaoId.Should().BeNull();
    }

    /// <summary>
    /// A regressão que mais assusta nesta task: <c>CargaProcessor</c> atende TODO import,
    /// não só o de sessão. Um envio avulso não tem sugestão declarada e não pode passar a
    /// exigir uma.
    /// </summary>
    [Fact]
    public async Task Import_avulso_sem_declaracao_continua_importando_normalmente()
    {
        var redeId = await EnsureRedeAsync("Rede Sessao Manifesto Avulso", $"{Slug}-avulso");

        using var zip = BuildZip(comManifesto: false);
        zip.Position = 0;
        var upload = await fixture.ImportsApi.UploadAsync(
            new StreamPart(zip, $"{Slug}-avulso.zip", "application/zip"), redeId,
            TestContext.Current.CancellationToken);

        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var carga = await fixture.WaitForCargaAsync(
            upload.Content!.Id, ct: TestContext.Current.CancellationToken);

        carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");
        carga.LinhasImportadas.Should().BeGreaterThan(0,
            "import fora de sessão não conhece manifesto e tem de carregar o Stage como sempre fez");
    }

    private async Task<Guid> CriarSessaoAsync(int redeId, string nome)
    {
        var criada = await fixture.ComparacoesApi.CreateAsync(
            new CreateSessaoRequest(nome), redeId, TestContext.Current.CancellationToken);

        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        return criada.Content!.Id;
    }

    private async Task EnviarAsync(Guid sessaoId, int redeId, bool comManifesto)
    {
        using var zip = BuildZip(comManifesto);
        zip.Position = 0;

        var envio = await fixture.ComparacoesApi.UploadDadosAsync(
            sessaoId, new StreamPart(zip, $"{Slug}.zip", "application/zip"), redeId,
            TestContext.Current.CancellationToken);

        envio.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    /// <summary>
    /// Espera o Worker terminar de processar o envio. O sinal é a sugestão gravada, não um
    /// estado terminal: com o <c>SessaoWorker</c> em pé a sessão do caminho feliz segue para
    /// treino e comparação, e o que esta classe afirma é o vínculo, não o ciclo — quem cobre
    /// o ciclo é <see cref="SessaoOrquestracaoIntegrationTests"/>.
    /// </summary>
    private async Task<SessaoView> AguardarSessaoAsync(Guid sessaoId, int redeId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.ComparacoesApi.GetAsync(
                sessaoId, redeId, TestContext.Current.CancellationToken);

            if (resp.Content is { } sessao &&
                (sessao.SugestaoId is not null || sessao.Status is "Inviavel" or "Falha"))
            {
                return sessao;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"Sessão {sessaoId} não foi resolvida pelo Worker em 3 min. Verifique os logs do worker.");
    }

    private async Task<int> EnsureRedeAsync(string nome, string slug)
    {
        var criacao = await fixture.RedesApi.CreateAsync(
            new CreateRedeRequest(nome, slug), TestContext.Current.CancellationToken);
        if (criacao.IsSuccessStatusCode)
        {
            return criacao.Content!.Id;
        }

        criacao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "só conflito de slug é aceitável aqui; outro status é falha real");

        var lista = await fixture.RedesApi.ListAsync(TestContext.Current.CancellationToken);
        lista.IsSuccessStatusCode.Should().BeTrue();
        var existente = lista.Content!.SingleOrDefault(r => r.Slug == slug);
        existente.Should().NotBeNull($"rede '{slug}' deu 409 mas não apareceu na listagem");
        return existente!.Id;
    }

    /// <summary>
    /// Dataset mínimo que o validador do upload aceita, com a sugestão do PBS e — quando
    /// pedido — a declaração na raiz. A janela cobre dias antes e depois da sugestão, que é
    /// o que a leitura exige para considerar o envio viável.
    /// </summary>
    private static MemoryStream BuildZip(bool comManifesto)
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Sessao", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(Sku, "Produto Sessao", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            var qtd = 5m + d.Day % 7;
            vendas.Add(new VendaRow(d, LojaId, Sku, qtd, 10.50m, qtd * 10.50m));
        }

        var builder = new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            .WithEstoquesDiarios([new(new DateOnly(2026, 2, 10), LojaId, Sku, 500m)])
            .WithCompras(new CompraFaker([LojaId], [Sku], Inicio, Fim, seed: 930).Generate(2))
            .WithPromocoes(new PromocaoFaker([LojaId], [Sku], Inicio, Fim, seed: 931).Generate(1))
            .ReplaceRaw("sugestoes_compra.csv", SugestaoCsv())
            .ReplaceRaw("sugestoes_compra_itens.csv", SugestaoItensCsv());

        if (comManifesto)
        {
            builder = builder.ReplaceRaw("manifesto.json", ManifestoJson());
        }

        return builder.Build();
    }

    /// <summary>
    /// JSON escrito à mão de propósito: o teste de contrato em
    /// <c>ManifestoContratoTests</c> é que amarra esta forma à do extrator, e referenciar o
    /// extrator daqui (WinForms) arrastaria o projeto inteiro para a integração.
    /// </summary>
    private static string ManifestoJson() => string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "SugestaoId": {{SugestaoId}},
          "SugestaoDescricao": "Sugestao da sessao",
          "SugestaoDataHora": "{{SugestaoDataHora:yyyy-MM-dd}}T{{SugestaoDataHora:HH:mm:ss}}",
          "SugestaoTipoCalculo": {{TipoCalculo}},
          "JanelaInicio": "{{Inicio:yyyy-MM-dd}}",
          "JanelaFim": "{{Fim:yyyy-MM-dd}}",
          "VersaoExtractor": "1.0.0",
          "SkusSemCadastro": {{SkusSemCadastro}}
        }
        """);

    private static string SugestaoCsv() =>
        "SugestaoId,Descricao,DataHora,TipoCalculo,LeadTimeDias,DiasCurvaA,DiasCurvaB,DiasCurvaC,DiasCurvaD,DiasCurvaE,Efetividade,ConsideraPedidosPendentes,IncluiEstoqueZerado\n" +
        $"{SugestaoId},Sugestao da sessao,{SugestaoDataHora:yyyy-MM-dd}T09:30:00,{TipoCalculo},7,15,15,15,15,15,100.00,1,0\n";

    private static string SugestaoItensCsv() =>
        "SugestaoId,LojaId,Sku,Curva,DemandaDia,DemandaDiaPonderada,EstoqueSaldo,EstoqueSeguranca," +
        "EstoqueMaximo,EstoqueMinimo,DiasEstoque,PedidosPendentes,CompraSugerida,CompraAutorizada," +
        "PrecoCompra,FatorEmbalagem,Falteiro\n" +
        $"{SugestaoId},{LojaId},{Sku},A,6.0000,,10.000,,,,7,0.000,32.000,32.000,3.5000,,0\n";
}
