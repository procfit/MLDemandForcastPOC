using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// As três guardas que impedem uma sessão de mentir para o comprador: fase abandonada, sessão
/// sem declaração da sugestão e duas sessões da mesma rede em voo.
///
/// <para>
/// Todas exigem um estado que a API não sabe produzir — job largado em <c>Processando</c> por um
/// worker que morreu, sessão em <c>ProcessandoDados</c> sem sugestão vinculada —, então o
/// arranjo é escrito direto no banco <c>engine</c> e o que se observa é o <c>SessaoWorker</c>
/// real reagindo a ele. Nenhum destes casos treina nada: são segundos, não minutos.
/// </para>
///
/// <para>
/// Cada caso usa rede própria. O bloqueio de sessão concorrente é <b>por rede</b>, e compartilhar
/// rede faria um caso bloquear o outro por efeito colateral em vez de por defeito.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class SessaoGuardasIntegrationTests(AppHostFixture fixture)
{
    /// <summary>
    /// Fase reclamada há muito mais que <see cref="ComparacaoSessao.LimiteDeFaseSemProgresso"/>:
    /// dois dias, para que nenhuma lentidão de ambiente possa explicar o desfecho.
    /// </summary>
    private static readonly TimeSpan IdadeDoAbandono = TimeSpan.FromDays(2);

    /// <summary>
    /// O modo de falha que este teste fecha: worker reiniciado no meio do treino. O
    /// <c>TreinoJob</c> fica em <c>Processando</c> para sempre — as filas reclamam com
    /// <c>Status = 'Pendente'</c>, sem lease e sem heartbeat, então ninguém o devolve —, e antes
    /// desta guarda a sessão ficava em <c>Treinando</c> sendo repescada a cada 5 segundos, para
    /// sempre, com o comprador olhando um spinner sem motivo e sem próxima ação.
    /// </summary>
    [Fact]
    public async Task Sessao_presa_em_fase_abandonada_termina_com_motivo_em_vez_de_girar()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Rede Sessao Fase Abandonada", "sessao-fase-abandonada");
        var reclamadoEm = DateTimeOffset.UtcNow - IdadeDoAbandono;

        await using var db = await AbrirEngineAsync(ct);

        var treino = new TreinoJob
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = TreinoStatus.Processando,
            DataAgendamento = reclamadoEm,
            DataInicioProcessamento = reclamadoEm,
            MaxSkus = 80,
            TreinoAte = new DateOnly(2026, 7, 1),
        };
        db.TreinoJobs.Add(treino);

        var sessaoId = Guid.CreateVersion7();
        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = sessaoId,
            RedeId = redeId,
            Nome = "Treino abandonado",
            Status = SessaoStatus.Treinando,
            CriadoEm = reclamadoEm,
            AtualizadoEm = reclamadoEm,
            SugestaoId = 7301,
            SugestaoDataHora = new DateTime(2026, 7, 1, 9, 30, 0),
            SugestaoTipoCalculo = 2,
            TreinoJobId = treino.Id,
        });

        await db.SaveChangesAsync(ct);

        var sessao = await AguardarTerminoAsync(sessaoId, redeId);

        sessao.Status.Should().Be("Falha",
            "sem estado terminal o comprador espera para sempre por um treino que ninguém vai terminar");
        sessao.MensagemErro.Should().NotBeNullOrWhiteSpace();
        sessao.MensagemErro.Should().Contain("Envie os dados novamente",
            "quem lê é comprador de farmácia: a mensagem tem de terminar numa próxima ação");
    }

    /// <summary>
    /// O ramo de inviabilidade do próprio avanço: a carga concluiu, mas a sessão não sabe qual
    /// sugestão avaliar. Sem corte não há treino honesto e sem método não há contra o que
    /// disputar, então a sessão para com um motivo em vez de caminhar três fases para morrer na
    /// última. Cobre a <b>gravação</b> do estado no caminho de falha, não só a fábrica que
    /// devolve <c>null</c>.
    /// </summary>
    [Fact]
    public async Task Sessao_sem_declaracao_da_sugestao_para_em_inviavel_e_nao_enfileira_treino()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Rede Sessao Sem Declaracao", "sessao-sem-declaracao");
        var agora = DateTimeOffset.UtcNow;

        await using var db = await AbrirEngineAsync(ct);

        // Carga já concluída: o import de verdade não precisa acontecer porque o avanço só lê o
        // Status dela. É o que torna este caso barato.
        var carga = new CargaStage
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = CargaStageStatus.Concluida,
            DataAgendamento = agora,
            DataInicioProcessamento = agora,
            DataConclusao = agora,
            NomeArquivoOriginal = "sem-declaracao.zip",
            BlobKey = "sem-declaracao.zip",
            LinhasImportadas = 0,
        };
        db.CargasStage.Add(carga);

        var sessaoId = Guid.CreateVersion7();
        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = sessaoId,
            RedeId = redeId,
            Nome = "Sem declaracao",
            Status = SessaoStatus.ProcessandoDados,
            CriadoEm = agora,
            AtualizadoEm = agora,
            CargaStageId = carga.Id,
        });

        await db.SaveChangesAsync(ct);

        var sessao = await AguardarTerminoAsync(sessaoId, redeId);

        sessao.Status.Should().Be("Inviavel",
            "nada quebrou: faltou pré-condição no que foi enviado, e o remédio é gerar o envio de novo");
        sessao.MensagemErro.Should().BeNull("Inviavel é resposta, não falha");
        sessao.MotivoInviabilidade.Should().NotBeNullOrWhiteSpace();
        sessao.MotivoInviabilidade.Should().Contain("extrator");

        var treinos = await fixture.TrainingApi.ListAsync(redeId, ct: ct);
        treinos.IsSuccessStatusCode.Should().BeTrue();
        treinos.Content!.Should().NotContain(t => t.DataAgendamento >= agora,
            "sessão inviável não pode deixar treino enfileirado atrás de si");
    }

    /// <summary>
    /// O Stage é por rede e cada import o substitui inteiro, então duas sessões em voo na mesma
    /// rede não competem — elas se destroem. A variante grave é silenciosa: se a sugestão nova
    /// cair no mesmo dia e método da anterior, a primeira pontua a sugestão da segunda contra o
    /// próprio modelo e o resultado <b>parece</b> válido.
    ///
    /// <para>
    /// A sessão que ocupa a rede é plantada com <c>AtualizadoEm</c> recente de propósito: o
    /// bloqueio vale enquanto a outra sessão está <b>viva</b>, e é isso que se está afirmando.
    /// O <c>finally</c> a encerra para não deixar a rede ocupada para as execuções seguintes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Segunda_sessao_da_mesma_rede_nao_consegue_enviar_dados_e_ouve_qual_esta_rodando()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Rede Sessao Concorrente", "sessao-concorrente");
        var agora = DateTimeOffset.UtcNow;

        await using var db = await AbrirEngineAsync(ct);

        // A fase precisa existir e estar viva: o SessaoWorker mata em segundos a sessão cujo job
        // de fase não existe mais (fase perdida), e aí a rede deixaria de estar ocupada antes da
        // asserção. Em Processando e com reclamação recente, ninguém a toca — o
        // ComparacaoWorker só reclama Pendente.
        var comparacao = new ComparacaoPbs
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = ComparacaoPbsStatus.Processando,
            DataAgendamento = agora,
            DataInicioProcessamento = agora,
            TreinoJobId = Guid.CreateVersion7(),
            JanelaInicio = new DateOnly(2026, 7, 1),
            JanelaFim = new DateOnly(2026, 7, 1),
            TipoCalculo = 2,
        };
        db.ComparacoesPbs.Add(comparacao);

        var emVooId = Guid.CreateVersion7();
        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = emVooId,
            RedeId = redeId,
            Nome = "Comparacao de julho",
            Status = SessaoStatus.Comparando,
            CriadoEm = agora,
            AtualizadoEm = agora,
            SugestaoId = 7302,
            SugestaoDataHora = new DateTime(2026, 7, 1, 9, 30, 0),
            SugestaoTipoCalculo = 2,
            ComparacaoPbsId = comparacao.Id,
        });
        await db.SaveChangesAsync(ct);

        try
        {
            var criada = await fixture.ComparacoesApi.CreateAsync(
                new CreateSessaoRequest("Segunda comparacao"), redeId, ct);
            criada.StatusCode.Should().Be(HttpStatusCode.Created,
                "criar sessão não escreve no Stage e continua permitido — o bloqueio é do envio");

            using var zip = new MemoryStream([0x50, 0x4B, 0x03, 0x04]);
            var envio = await fixture.ComparacoesApi.UploadDadosAsync(
                criada.Content!.Id, new Refit.StreamPart(zip, "segunda.zip", "application/zip"), redeId, ct);

            envio.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "o envio apagaria o Stage que a outra sessão está usando");

            // Cast porque no Refit 14 `Error` é `ApiExceptionBase`, que só carrega o lado
            // da requisição; o corpo da resposta desceu para `ApiException`.
            var erro = (envio.Error as Refit.ApiException)?.Content ?? "";
            erro.Should().Contain("Comparacao de julho",
                "a recusa tem de nomear a comparação que está ocupando a rede, senão não há o que esperar");
            erro.Should().Contain("comparando os dois métodos",
                "a fase precisa aparecer em linguagem de comprador, não como nome de enum");
        }
        finally
        {
            await db.ComparacaoSessoes
                .Where(s => s.Id == emVooId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, SessaoStatus.Falha)
                    .SetProperty(x => x.MensagemErro, "Encerrada pelo teste de guarda de concorrência.")
                    .SetProperty(x => x.AtualizadoEm, DateTimeOffset.UtcNow), CancellationToken.None);

            await db.ComparacoesPbs
                .Where(c => c.Id == comparacao.Id)
                .ExecuteUpdateAsync(c => c
                    .SetProperty(x => x.Status, ComparacaoPbsStatus.Falha)
                    .SetProperty(x => x.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.MensagemErro, "Encerrada pelo teste de guarda de concorrência."),
                    CancellationToken.None);
        }
    }

    /// <summary>
    /// Método de cálculo que o ERP não tem. É o gatilho mais barato para uma falha
    /// <b>permanente</b> na criação do job da fase seguinte: a fila da comparação carrega
    /// <c>CK_ComparacoesPbs_TipoCalculo</c>, então o <c>INSERT</c> estoura igual em toda
    /// tentativa. A fronteira do ZIP passou a recusar isto (<c>ManifestoLeitor</c>), e é por
    /// isso que o arranjo aqui é escrito direto no banco: o que se está afirmando não é este
    /// valor, é que <b>nenhuma</b> falha permanente de avanço gira.
    /// </summary>
    private const byte MetodoInexistente = 3;

    /// <summary>
    /// O modo de falha que esta guarda fecha, e ele é pior que um spinner eterno. A inserção do
    /// job da fase seguinte falhava fora de qualquer <c>try</c>, então a exceção caía no handler
    /// genérico do laço, que dorme 5 segundos e reclama a mesma sessão de novo — para sempre,
    /// numa falha que não passa. E cada reclamação toca <c>AtualizadoEm</c>, que é exatamente o
    /// campo que <c>SessaoConcorrenteAsync</c> lê para decidir se a rede tem sessão viva: a rede
    /// ficava <b>trancada para todo envio futuro</b>, sem cicatrizar nunca.
    ///
    /// <para>
    /// O relógio de fase abandonada não resgata este caso — ele só é alcançado enquanto o job da
    /// fase ainda não terminou, e aqui o treino está <c>Concluido</c>. Quem tem de intervir é o
    /// limite de tentativas do avanço, e as duas asserções abaixo são o par que importa: a
    /// sessão chega a estado terminal, e a rede volta a aceitar envio <b>logo depois</b>, com o
    /// <c>AtualizadoEm</c> da sessão morta ainda recente.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Falha_permanente_ao_criar_o_job_da_proxima_fase_encerra_a_sessao_e_nao_tranca_a_rede()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Rede Sessao Avanco Impossivel", "sessao-avanco-impossivel");
        var agora = DateTimeOffset.UtcNow;

        await using var db = await AbrirEngineAsync(ct);

        // Treino já concluído: é o que faz o avanço tentar criar a comparação nesta volta, e é
        // o que põe a fase fora do alcance do relógio de abandono.
        var treino = new TreinoJob
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = TreinoStatus.Concluido,
            DataAgendamento = agora,
            DataInicioProcessamento = agora,
            DataConclusao = agora,
            MaxSkus = 80,
            TreinoAte = new DateOnly(2026, 7, 1),
        };
        db.TreinoJobs.Add(treino);

        var travadaId = Guid.CreateVersion7();
        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = travadaId,
            RedeId = redeId,
            Nome = "Avanco impossivel",
            Status = SessaoStatus.Treinando,
            CriadoEm = agora,
            AtualizadoEm = agora,
            SugestaoId = 7303,
            SugestaoDataHora = new DateTime(2026, 7, 1, 9, 30, 0),
            SugestaoTipoCalculo = MetodoInexistente,
            TreinoJobId = treino.Id,
        });

        await db.SaveChangesAsync(ct);

        var travada = await AguardarTerminoAsync(travadaId, redeId);

        travada.Status.Should().Be("Falha",
            "esgotado o limite de tentativas a sessão termina; girar deixaria o comprador sem desfecho e a " +
            "rede sem saída");
        travada.MensagemErro.Should().NotBeNullOrWhiteSpace();
        travada.MensagemErro.Should().Contain("Envie os dados novamente",
            "quem lê é comprador de farmácia: a mensagem tem de terminar numa próxima ação");

        // A asserção que importa: o bloqueio de sessão concorrente é por rede e olha
        // AtualizadoEm, que a sessão morta acabou de tocar. Estado terminal tem de bastar.
        var outra = await fixture.ComparacoesApi.CreateAsync(
            new CreateSessaoRequest("Depois da falha"), redeId, ct);
        outra.StatusCode.Should().Be(HttpStatusCode.Created);

        using var zip = ZipMinimo();
        var envio = await fixture.ComparacoesApi.UploadDadosAsync(
            outra.Content!.Id,
            new Refit.StreamPart(zip, "depois-da-falha.zip", "application/zip"), redeId, ct);

        envio.StatusCode.Should().Be(HttpStatusCode.Accepted,
            because: "a rede não pode ficar trancada por uma sessão que morreu: " +
                     ((envio.Error as Refit.ApiException)?.Content ?? "sem detalhe na resposta"));

        var seguinte = await AguardarTerminoAsync(outra.Content.Id, redeId);
        seguinte.Status.Should().Be("Inviavel",
            "o envio andou de verdade — este ZIP não declara sugestão, então o desfecho correto é inviável");
    }

    // --- Infra do teste ------------------------------------------------------

    /// <summary>
    /// ZIP com os sete CSVs que o validador do upload exige e nada além: sem declaração da
    /// sugestão, de propósito. O que se afirma com ele é que o envio foi <b>aceito</b>; a
    /// sessão terminar em <c>Inviavel</c> depois é o desfecho correto de um envio sem sugestão,
    /// e mantém o caso em segundos — nenhum treino roda.
    /// </summary>
    private static MemoryStream ZipMinimo()
    {
        const int lojaId = 9801;
        const string sku = "GRD-A";
        var inicio = new DateOnly(2026, 6, 1);
        var fim = new DateOnly(2026, 7, 10);

        var vendas = new List<VendaRow>();
        for (var d = inicio; d <= fim; d = d.AddDays(1))
        {
            vendas.Add(new VendaRow(d, lojaId, sku, 5m, 10.50m, 52.50m));
        }

        return new CsvZipBuilder()
            .WithLojas([new(lojaId, "Loja Guarda", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true)])
            .WithProdutos([new(sku, "Produto Guarda", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true)])
            .WithVendas(vendas)
            .WithEstoquesDiarios([new(inicio, lojaId, sku, 500m)])
            .WithCompras(new CompraFaker([lojaId], [sku], inicio, fim, seed: 940).Generate(1))
            .WithPromocoes(new PromocaoFaker([lojaId], [sku], inicio, fim, seed: 941).Generate(1))
            .WithMercadoIqvia(new MercadoIqviaFaker(["Dipirona Sódica"], ["SP"], inicio, fim, seed: 942).Generate(1))
            .Build();
    }

    private async Task<EngineDbContext> AbrirEngineAsync(CancellationToken ct)
    {
        var connStr = await fixture.GetEngineConnectionStringAsync(ct);
        var options = new DbContextOptionsBuilder<EngineDbContext>().UseSqlServer(connStr).Options;
        return new EngineDbContext(options);
    }

    private async Task<SessaoView> AguardarTerminoAsync(Guid sessaoId, int redeId)
    {
        var limite = TimeSpan.FromMinutes(2);
        var deadline = DateTimeOffset.UtcNow + limite;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await fixture.ComparacoesApi.GetAsync(
                sessaoId, redeId, TestContext.Current.CancellationToken);

            if (resp.Content is { Status: "Concluida" or "Inviavel" or "Falha" } sessao)
            {
                return sessao;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"Sessão {sessaoId} não atingiu estado terminal em {limite.TotalMinutes:F0} min. " +
            "Verifique os logs do worker (SessaoWorker).");
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
}
