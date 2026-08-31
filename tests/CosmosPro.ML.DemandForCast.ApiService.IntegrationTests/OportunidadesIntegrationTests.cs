using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Refit;
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
    private const string Brick = "528-RJ VOLTA REDONDA RETIRO";

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


    [Fact]
    public async Task Import_com_catalogo_grava_no_engine_e_sobrevive_ao_import_seguinte()
    {
        // O ponto deste teste é o ciclo de vida, não a gravação: depois de um SEGUNDO import
        // -- que apaga o Stage da rede inteiro -- o catálogo tem de continuar de pé. Se ele
        // morasse no Stage, a tela de oportunidades zeraria a cada envio de sessão.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Catalogo pelo import", Slug + "-imp");
        await LimparCatalogoAsync(redeId);

        await ImportarAsync(redeId, catalogo:
        [
            ("100", "07896094928060", "AAS INFANTIL 10 COMPRIMIDOS"),
            ("200", "7891721201806", "GLIFAGE XR 500MG"),
        ]);

        await using (var db = await AbrirEngineAsync(ct))
        {
            (await db.RedeCatalogoEans.CountAsync(c => c.RedeId == redeId, ct)).Should().Be(2);
        }

        // Segundo import SEM catálogo, como um ZIP de extrator anterior à 0.18.0.
        await ImportarAsync(redeId, catalogo: null);

        await using var depois = await AbrirEngineAsync(ct);
        (await depois.RedeCatalogoEans.CountAsync(c => c.RedeId == redeId, ct))
            .Should().Be(2, "ZIP sem catálogo não apaga o catálogo que existia — apagar puniria "
                          + "o comprador por usar um build velho, tirando uma tela que funcionava");
    }

    [Fact]
    public async Task O_ean_e_normalizado_na_gravacao()
    {
        // O PBS manda 14 caracteres com zero à esquerda. Cru, o join com MercadoObservacoes
        // (13 dígitos) casa ZERO -- silenciosamente, sem exceção e sem log, e a tela passa a
        // oferecer o cadastro inteiro como oportunidade.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Ean normalizado", Slug + "-ean");
        await LimparCatalogoAsync(redeId);

        await ImportarAsync(redeId, catalogo:
            [("100", "07896094928060", "AAS INFANTIL 10 COMPRIMIDOS")]);

        await using var db = await AbrirEngineAsync(ct);
        var eans = await db.RedeCatalogoEans
            .Where(c => c.RedeId == redeId).Select(c => c.Ean).ToListAsync(ct);

        eans.Should().BeEquivalentTo(["7896094928060"], "sem o zero à esquerda");
    }

    [Fact]
    public async Task Ean_repetido_no_mestre_nao_derruba_o_import()
    {
        // O mestre do PBS pode ter dois SKUs com o mesmo código (apresentação cadastrada em
        // duplicidade). Sem dedupe, o SqlBulkCopy estoura violação de PK e o import falha por
        // um defeito de cadastro que não é do comprador.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Ean repetido", Slug + "-dup");
        await LimparCatalogoAsync(redeId);

        await ImportarAsync(redeId, catalogo:
        [
            ("100", "7891721201806", "GLIFAGE, PRIMEIRO CADASTRO"),
            ("999", "07891721201806", "GLIFAGE, CADASTRO DUPLICADO"),
        ]);

        await using var db = await AbrirEngineAsync(ct);
        var linhas = await db.RedeCatalogoEans.Where(c => c.RedeId == redeId).ToListAsync(ct);

        linhas.Should().HaveCount(1, "os dois normalizam para o mesmo EAN");
        linhas[0].Sku.Should().Be("100", "o primeiro vence, e a consulta do extrator ordena por produto");
    }

    [Fact]
    public async Task Linha_sem_codigo_utilizavel_e_descartada()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Ean invalido", Slug + "-inv");
        await LimparCatalogoAsync(redeId);

        await ImportarAsync(redeId, catalogo:
        [
            ("100", "", "SEM CODIGO"),
            ("200", "00000000000000", "CODIGO TODO ZERO"),
            ("300", "7891721201806", "VALIDO"),
        ]);

        await using var db = await AbrirEngineAsync(ct);
        var skus = await db.RedeCatalogoEans
            .Where(c => c.RedeId == redeId).Select(c => c.Sku).ToListAsync(ct);

        skus.Should().BeEquivalentTo(["300"],
            "código vazio ou todo zero não identifica produto, e gravado criaria chave que casa "
            + "com lixo do outro lado do join");
    }


    // ================= regras A1 e A2, contra banco real =============================

    [Fact]
    public async Task A1_traz_o_que_o_mercado_vende_e_o_cadastro_nao_tem()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMercadoAsync(Slug + "-a1",
            mercado: [("111", 500m), ("222", 300m), ("333", 50m)],
            catalogo: ["111"]);

                var resposta = await fixture.MercadoApi.OportunidadesAsync(
            redeId, corteMinimo: 0m, take: 100, ct: ct);
        resposta.IsSuccessStatusCode.Should().BeTrue();
        var pagina = resposta.Content!;

        pagina.Itens.Select(i => i.Ean).Should().BeEquivalentTo(["222", "333"],
            "o EAN que está no catálogo não é oportunidade");
        pagina.EansNoCatalogo.Should().Be(1);
    }

    [Fact]
    public async Task A2_o_corte_de_relevancia_reduz_a_lista_e_o_total()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMercadoAsync(Slug + "-a2",
            mercado: [("111", 500m), ("222", 300m), ("333", 50m)],
            catalogo: ["999"]);

                var resposta = await fixture.MercadoApi.OportunidadesAsync(
            redeId, corteMinimo: 200m, take: 100, ct: ct);
        resposta.IsSuccessStatusCode.Should().BeTrue();
        var pagina = resposta.Content!;

        pagina.Itens.Select(i => i.Ean).Should().BeEquivalentTo(["111", "222"]);
        pagina.Total.Should().Be(2, "o total acompanha o corte, não a população");
    }

    [Fact]
    public async Task So_conta_a_venda_dos_concorrentes()
    {
        // Bandeira própria não entra na conta. Somar as duas faria o corte inflar e a tela
        // ofereceria itens que o bairro quase não vende, por causa de venda da própria rede.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMercadoAsync(Slug + "-band",
            mercado: [("111", 100m)],
            catalogo: ["999"],
            unidadesDaRede: 900m);

                var resposta = await fixture.MercadoApi.OportunidadesAsync(
            redeId, corteMinimo: 500m, take: 100, ct: ct);
        resposta.IsSuccessStatusCode.Should().BeTrue();
        var pagina = resposta.Content!;

        pagina.Itens.Should().BeEmpty(
            "só 100 unidades são de concorrente, e o corte é 500 — as 900 da rede não contam");
    }

    [Fact]
    public async Task Sem_catalogo_a_consulta_recusa_em_vez_de_devolver_o_mercado_inteiro()
    {
        // O caso em que a tela mais pode mentir. Catálogo vazio significa "o comprador não
        // enviou o arquivo", NUNCA "a rede não tem produto nenhum" -- e devolver tudo seria a
        // tela errando em 100% das linhas.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMercadoAsync(Slug + "-nocat",
            mercado: [("111", 500m), ("222", 300m)],
            catalogo: []);

                var resposta = await fixture.MercadoApi.OportunidadesAsync(
            redeId, corteMinimo: 0m, take: 100, ct: ct);
        resposta.IsSuccessStatusCode.Should().BeTrue();
        var pagina = resposta.Content!;

        pagina.EansNoCatalogo.Should().Be(0);
        pagina.Itens.Should().BeEmpty("sem catálogo não há como afirmar ausência");
        pagina.Total.Should().Be(0);
        pagina.Mes.Should().BeNull("nem o mês faz sentido declarar aqui");
    }

    [Fact]
    public async Task Usa_o_mes_mais_recente_coberto()
    {
        // Regra diferente do grupo B, e de propósito: a pergunta aqui é "o que devo incluir
        // agora", não "o que o comprador poderia ter sabido quando decidiu".
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMercadoAsync(Slug + "-mes",
            mercado: [("111", 500m)],
            catalogo: ["999"],
            mes: new DateOnly(2025, 6, 1));

        await SemearObservacaoAsync(redeId, new DateOnly(2026, 6, 1), "111", 700m);

                var resposta = await fixture.MercadoApi.OportunidadesAsync(
            redeId, corteMinimo: 0m, take: 100, ct: ct);
        resposta.IsSuccessStatusCode.Should().BeTrue();
        var pagina = resposta.Content!;

        pagina.Mes.Should().Be(new DateOnly(2026, 6, 1));
        pagina.Itens.Should().ContainSingle()
            .Which.UnidadesConcorrentes.Should().Be(700m, "as unidades são as do mês escolhido");
    }

    [Fact]
    public async Task Produto_sem_linha_de_dimensao_entra_com_descricao_nula()
    {
        // A tela mostra o próprio EAN nesse caso. Descartar a linha esconderia uma
        // oportunidade real por falta de um dado de exibição.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMercadoAsync(Slug + "-dim",
            mercado: [("111", 500m)],
            catalogo: ["999"],
            comDimensao: false);

                var resposta = await fixture.MercadoApi.OportunidadesAsync(
            redeId, corteMinimo: 0m, take: 100, ct: ct);
        resposta.IsSuccessStatusCode.Should().BeTrue();
        var pagina = resposta.Content!;

        var item = pagina.Itens.Should().ContainSingle().Subject;
        item.Ean.Should().Be("111");
        item.Descricao.Should().BeNull();
    }

    [Fact]
    public async Task O_total_fala_da_populacao_e_o_take_e_limitado()
    {
        var ct = TestContext.Current.CancellationToken;
        var mercado = Enumerable.Range(1, 30)
            .Select(i => ($"90000{i:D3}", 1000m - i))
            .ToArray();
        var redeId = await SemearMercadoAsync(Slug + "-pag", mercado, catalogo: ["999"]);

                var resposta = await fixture.MercadoApi.OportunidadesAsync(
            redeId, corteMinimo: 0m, take: 5000, ct: ct);
        resposta.IsSuccessStatusCode.Should().BeTrue();
        var pagina = resposta.Content!;

        pagina.Total.Should().Be(30);
        pagina.Itens.Count.Should().BeLessThanOrEqualTo(200, "o take é limitado no servidor");
        pagina.Itens.Should().BeInDescendingOrder(i => i.UnidadesConcorrentes);
    }


    [Fact]
    public async Task Uma_rede_nao_ve_o_mercado_nem_o_catalogo_da_outra()
    {
        // O endpoint e' interno (a apiservice nao tem rota externa) e recebe redeId na query;
        // na Web ele sai do IRedeContext. O que se afirma aqui e' que o escopo existe: sem
        // ele, a lista de oportunidades de uma rede sairia montada com o cadastro da outra --
        // e um item cadastrado no vizinho apareceria como oportunidade aqui, ou vice-versa.
        var ct = TestContext.Current.CancellationToken;
        var comDado = await SemearMercadoAsync(Slug + "-t1",
            mercado: [("111", 500m)], catalogo: ["999"]);
        var vizinha = await EnsureRedeAsync("Oportunidades vizinha", Slug + "-t2");
        await LimparCatalogoAsync(vizinha);

        var minha = await fixture.MercadoApi.OportunidadesAsync(comDado, corteMinimo: 0m, ct: ct);
        minha.IsSuccessStatusCode.Should().BeTrue();
        minha.Content!.Itens.Should().ContainSingle();

        var dela = await fixture.MercadoApi.OportunidadesAsync(vizinha, corteMinimo: 0m, ct: ct);
        dela.IsSuccessStatusCode.Should().BeTrue();
        dela.Content!.EansNoCatalogo.Should().Be(0);
        dela.Content.Itens.Should().BeEmpty("a rede vizinha nao tem mercado nem catalogo proprio");
    }

    [Fact]
    public async Task Rede_inexistente_nao_devolve_lista()
    {
        var ct = TestContext.Current.CancellationToken;

        var resp = await fixture.MercadoApi.OportunidadesAsync(redeId: 999_999, ct: ct);

        resp.IsSuccessStatusCode.Should().BeFalse(
            "rede inexistente e' recusada antes da consulta, como nas demais rotas de mercado");
    }

    [Fact]
    public async Task O_corte_padrao_do_endpoint_e_o_da_regra()
    {
        // Sem corteMinimo na query, o endpoint aplica CorteMinimoPadrao (200). Um default
        // divergente aqui faria a tela mostrar volume diferente do calibrado, sem nada
        // denunciando.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMercadoAsync(Slug + "-def",
            mercado: [("111", 500m), ("222", 150m)], catalogo: ["999"]);

        var resp = await fixture.MercadoApi.OportunidadesAsync(redeId, ct: ct);

        resp.Content!.Itens.Select(i => i.Ean).Should().BeEquivalentTo(["111"],
            "222 tem 150 unidades e o corte padrao e' 200");
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
    /// <summary>
    /// Sobe um ZIP mínimo pelo endpoint real de import e espera a carga concluir. O catálogo
    /// entra como CSV cru: não há faker para ele, e o que se afirma é exatamente o parse do
    /// arquivo como o extrator o escreve.
    /// </summary>
    private async Task ImportarAsync(
        int redeId, IReadOnlyList<(string Sku, string Ean, string Nome)>? catalogo)
    {
        var ct = TestContext.Current.CancellationToken;

        var lojaIds = new[] { 1 };
        var skus = new[] { "S1" };
        var inicio = new DateOnly(2026, 1, 1);
        var fim = new DateOnly(2026, 1, 31);

        var builder = new CsvZipBuilder()
            .WithLojas(new LojaFaker(seed: 1).Generate(1))
            .WithProdutos(new ProdutoFaker(seed: 2).Generate(1))
            .WithVendas(new VendaFaker(lojaIds, skus, inicio, fim, seed: 3).Generate(2))
            .WithEstoquesDiarios(new EstoqueDiarioFaker(lojaIds, skus, inicio, fim, seed: 4).Generate(2))
            .WithCompras(new CompraFaker(lojaIds, skus, inicio, fim, seed: 5).Generate(1))
            .WithPromocoes(new PromocaoFaker(lojaIds, skus, inicio, fim, seed: 6).Generate(1));

        if (catalogo is not null)
        {
            var linhas = new List<string> { "Sku,Ean,Nome" };
            linhas.AddRange(catalogo.Select(c => $"{c.Sku},{c.Ean},{c.Nome}"));
            builder = builder.ReplaceRaw("catalogo_eans.csv", string.Join("\n", linhas) + "\n");
        }

        using var zip = builder.Build();
        var upload = await fixture.ImportsApi.UploadAsync(
            new StreamPart(zip, "catalogo-teste.zip", "application/zip"), redeId);

        upload.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var carga = await fixture.WaitForCargaAsync(upload.Content!.Id, ct: ct);
        carga.Status.Should().Be("Concluida", because: carga.MensagemErro ?? "sem mensagem de erro");
    }

    /// <summary>
    /// Zera o catálogo da rede antes do teste. O volume do SQL é persistente e a rede é
    /// reaproveitada entre execuções, então asserção de contagem absoluta passaria na primeira
    /// execução e falharia na terceira.
    /// </summary>
    private async Task LimparCatalogoAsync(int redeId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AbrirEngineAsync(ct);
        await db.RedeCatalogoEans.Where(c => c.RedeId == redeId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Semeia mercado e catálogo direto no engine. Direto, e não pelo XLSX de import, porque o
    /// que se afirma aqui é a consulta — o caminho do arquivo já tem os seus próprios testes.
    /// </summary>
    private async Task<int> SemearMercadoAsync(
        string slug,
        IReadOnlyList<(string Ean, decimal Unidades)> mercado,
        IReadOnlyList<string> catalogo,
        decimal unidadesDaRede = 0m,
        DateOnly? mes = null,
        bool comDimensao = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync($"Oportunidades {slug}", slug);
        var mesUsado = mes ?? new DateOnly(2026, 6, 1);

        await using var db = await AbrirEngineAsync(ct);

        await db.MercadoObservacoes.Where(o => o.RedeId == redeId).ExecuteDeleteAsync(ct);
        await db.MercadoProdutos.Where(x => x.RedeId == redeId).ExecuteDeleteAsync(ct);
        await db.RedeCatalogoEans.Where(c => c.RedeId == redeId).ExecuteDeleteAsync(ct);

        foreach (var (ean, unidades) in mercado)
        {
            db.MercadoObservacoes.Add(new MercadoObservacao
            {
                RedeId = redeId, Mes = mesUsado, Brick = Brick, Bandeira = "CONCORRENTES",
                Ean = ean, Unidades = unidades, ValorCpp = unidades * 10m,
            });

            if (unidadesDaRede > 0m)
            {
                db.MercadoObservacoes.Add(new MercadoObservacao
                {
                    RedeId = redeId, Mes = mesUsado, Brick = Brick, Bandeira = "DROGARIA RETIRO",
                    Ean = ean, Unidades = unidadesDaRede, ValorCpp = unidadesDaRede * 10m,
                });
            }

            if (comDimensao)
            {
                db.MercadoProdutos.Add(new MercadoProduto
                {
                    RedeId = redeId, Ean = ean, DescricaoLonga = $"PRODUTO {ean}",
                    Laboratorio = "LAB", AreaFarmacia = "MIP", Classe4 = "N02B0",
                });
            }
        }

        foreach (var ean in catalogo)
        {
            db.RedeCatalogoEans.Add(new RedeCatalogoEan
            {
                RedeId = redeId, Ean = ean, Sku = $"SKU-{ean}", Nome = $"CADASTRADO {ean}",
            });
        }

        await db.SaveChangesAsync(ct);
        return redeId;
    }

    private async Task SemearObservacaoAsync(int redeId, DateOnly mes, string ean, decimal unidades)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AbrirEngineAsync(ct);

        db.MercadoObservacoes.Add(new MercadoObservacao
        {
            RedeId = redeId, Mes = mes, Brick = Brick, Bandeira = "CONCORRENTES",
            Ean = ean, Unidades = unidades, ValorCpp = unidades * 10m,
        });
        db.MercadoProdutos.Add(new MercadoProduto
        {
            RedeId = redeId, Ean = ean + "-x", DescricaoLonga = "IRRELEVANTE",
        });
        await db.SaveChangesAsync(ct);
    }

}
