using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
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

            var erro = envio.Error?.Content ?? "";
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

    // --- Infra do teste ------------------------------------------------------

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
