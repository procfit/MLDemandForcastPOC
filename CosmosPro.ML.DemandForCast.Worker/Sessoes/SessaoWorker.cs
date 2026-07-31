using System.Data;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker.Sessoes;

/// <summary>
/// Faz a sessão de comparação andar sozinha: importar, treinar, comparar, concluir. A cada
/// volta pega uma sessão em fase intermediária, olha o job daquela fase e — quando ele
/// termina — cria o job da fase seguinte e grava o id na sessão.
///
/// <para>
/// A regra de fluxo mora aqui e só aqui. <see cref="ImportWorker"/>,
/// <see cref="Training.TreinoWorker"/> e <see cref="Comparison.ComparacaoWorker"/> seguem
/// sem saber que sessões existem: cada um continua processando a própria fila, e quem
/// conhece a ordem das fases é este loop.
/// </para>
/// </summary>
internal sealed class SessaoWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<SessaoWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SessaoWorker iniciado. Poll interval: {Interval}s.", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var avancou = await TryAdvanceNextAsync(stoppingToken);
                if (!avancou)
                    await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado no loop do SessaoWorker.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Devolve <c>true</c> só quando a sessão de fato mudou de estado — diferente dos
    /// workers de fila, onde reclamar uma linha já significa trabalho feito. Aqui a
    /// esmagadora maioria das voltas encontra uma fase ainda rodando, e devolver
    /// <c>true</c> nesses casos transformaria o loop em espera ativa sobre o banco.
    /// </summary>
    private async Task<bool> TryAdvanceNextAsync(CancellationToken ct)
    {
        var sessao = await ClaimNextAsync(ct);
        if (sessao is null) return false;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        var (resultado, mensagemErro) = await LerFaseAsync(db, sessao, ct);
        var proximo = SessaoAvancador.ProximoEstado(sessao.Status, resultado);
        if (proximo == sessao.Status) return false;

        // A máquina de estados tem a palavra final mesmo com o SessaoAvancador amarrado a
        // ela por teste: sem esta checagem, um destino recusado viraria UPDATE de zero
        // linha e a sessão ficaria parada para sempre sem ninguém reclamar.
        if (!ComparacaoSessao.PodeTransicionar(sessao.Status, proximo))
        {
            logger.LogWarning(
                "Sessão {SessaoId}: transição {De} -> {Para} não é permitida; avanço descartado.",
                sessao.Id, sessao.Status, proximo);
            return false;
        }

        return proximo switch
        {
            SessaoStatus.Treinando => await AvancarParaTreinoAsync(db, sessao, ct),
            SessaoStatus.Comparando => await AvancarParaComparacaoAsync(db, sessao, ct),
            SessaoStatus.Concluida => await GravarStatusAsync(db, sessao, SessaoStatus.Concluida, ct: ct),
            SessaoStatus.Falha => await GravarStatusAsync(
                db, sessao, SessaoStatus.Falha, mensagemErro: mensagemErro, ct: ct),
            _ => false,
        };
    }

    private async Task<bool> AvancarParaTreinoAsync(
        EngineDbContext db, SessaoEmAndamento sessao, CancellationToken ct)
    {
        var (job, motivo) = SessaoJobs.Treino(sessao, DateTimeOffset.UtcNow);
        if (job is null)
        {
            return await GravarStatusAsync(
                db, sessao, SessaoStatus.Inviavel, motivoInviabilidade: motivo, ct: ct);
        }

        db.TreinoJobs.Add(job);
        await db.SaveChangesAsync(ct);

        var gravou = await GravarStatusAsync(db, sessao, SessaoStatus.Treinando, treinoJobId: job.Id, ct: ct);
        if (!gravou)
        {
            logger.LogWarning(
                "Sessão {SessaoId} mudou de estado durante o avanço; treino {TreinoId} ficou órfão.",
                sessao.Id, job.Id);
            return false;
        }

        logger.LogInformation(
            "Sessão {SessaoId}: treino {TreinoId} enfileirado com corte em {TreinoAte:yyyy-MM-dd} (rede {RedeId}).",
            sessao.Id, job.Id, job.TreinoAte, job.RedeId);
        return true;
    }

    private async Task<bool> AvancarParaComparacaoAsync(
        EngineDbContext db, SessaoEmAndamento sessao, CancellationToken ct)
    {
        var (job, motivo) = SessaoJobs.Comparacao(sessao, sessao.TreinoJobId!.Value, DateTimeOffset.UtcNow);
        if (job is null)
        {
            return await GravarStatusAsync(
                db, sessao, SessaoStatus.Inviavel, motivoInviabilidade: motivo, ct: ct);
        }

        db.ComparacoesPbs.Add(job);
        await db.SaveChangesAsync(ct);

        var gravou = await GravarStatusAsync(
            db, sessao, SessaoStatus.Comparando, comparacaoPbsId: job.Id, ct: ct);
        if (!gravou)
        {
            logger.LogWarning(
                "Sessão {SessaoId} mudou de estado durante o avanço; comparação {ComparacaoId} ficou órfã.",
                sessao.Id, job.Id);
            return false;
        }

        logger.LogInformation(
            "Sessão {SessaoId}: comparação {ComparacaoId} enfileirada para {Dia:yyyy-MM-dd}, método {Tipo} (rede {RedeId}).",
            sessao.Id, job.Id, job.JanelaInicio, job.TipoCalculo, job.RedeId);
        return true;
    }

    /// <summary>
    /// Grava o estado novo com o mesmo <c>WHERE</c> otimista do <see cref="CargaProcessor"/>:
    /// a linha só muda se ainda estiver no estado que o claim leu.
    ///
    /// <para>
    /// O job da fase seguinte é inserido <b>antes</b> desta gravação de propósito. Na ordem
    /// inversa, um processo que morresse no meio deixaria a sessão apontando para um job que
    /// não existe — e a volta seguinte a leria como fase perdida e a mataria em
    /// <c>Falha</c>. Nesta ordem o pior caso é um job órfão que roda e não é lido por
    /// ninguém: desperdício de CPU, não sessão morta por engano.
    /// </para>
    /// </summary>
    private async Task<bool> GravarStatusAsync(
        EngineDbContext db,
        SessaoEmAndamento sessao,
        SessaoStatus novo,
        Guid? treinoJobId = null,
        Guid? comparacaoPbsId = null,
        string? mensagemErro = null,
        string? motivoInviabilidade = null,
        CancellationToken ct = default)
    {
        // Os ids das fases anteriores são reescritos a partir do retrato do claim em vez de
        // preservados no SQL: o WHERE otimista garante que a linha não mudou desde a leitura,
        // então reescrever o mesmo valor é fiel e dispensa um COALESCE por coluna.
        var treino = treinoJobId ?? sessao.TreinoJobId;
        var comparacao = comparacaoPbsId ?? sessao.ComparacaoPbsId;

        var linhas = await db.ComparacaoSessoes
            .Where(s => s.Id == sessao.Id && s.Status == sessao.Status)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, novo)
                .SetProperty(x => x.TreinoJobId, treino)
                .SetProperty(x => x.ComparacaoPbsId, comparacao)
                .SetProperty(x => x.MensagemErro, mensagemErro)
                .SetProperty(x => x.MotivoInviabilidade, motivoInviabilidade)
                .SetProperty(x => x.AtualizadoEm, DateTimeOffset.UtcNow), ct);

        if (linhas == 0)
        {
            logger.LogWarning(
                "Sessão {SessaoId} não estava mais em {Status}; avanço para {Novo} descartado.",
                sessao.Id, sessao.Status, novo);
            return false;
        }

        if (novo is SessaoStatus.Inviavel or SessaoStatus.Falha or SessaoStatus.Concluida)
        {
            logger.LogInformation(
                "Sessão {SessaoId} terminou em {Novo}. {Detalhe}",
                sessao.Id, novo, motivoInviabilidade ?? mensagemErro ?? "");
        }

        return true;
    }

    /// <summary>
    /// Situação do job da fase corrente. A fase é decidida pelo estado da sessão, e cada
    /// estado tem exatamente um job para observar.
    /// </summary>
    private async Task<(JobResultado Resultado, string? MensagemErro)> LerFaseAsync(
        EngineDbContext db, SessaoEmAndamento sessao, CancellationToken ct)
    {
        switch (sessao.Status)
        {
            case SessaoStatus.ProcessandoDados:
            {
                if (sessao.CargaStageId is not { } id) return FasePerdida("importação dos seus dados");

                var job = await db.CargasStage.AsNoTracking()
                    .Where(c => c.Id == id)
                    .Select(c => new { c.Status, c.MensagemErro })
                    .FirstOrDefaultAsync(ct);

                if (job is null) return FasePerdida("importação dos seus dados");

                return job.Status switch
                {
                    CargaStageStatus.Concluida => (JobResultado.Concluido, null),
                    CargaStageStatus.Falha => (JobResultado.Falhou,
                        Detalhar("importar os seus dados", job.MensagemErro)),
                    _ => (JobResultado.EmAndamento, null),
                };
            }

            case SessaoStatus.Treinando:
            {
                if (sessao.TreinoJobId is not { } id) return FasePerdida("aprendizado do padrão de venda");

                var job = await db.TreinoJobs.AsNoTracking()
                    .Where(t => t.Id == id)
                    .Select(t => new { t.Status, t.MensagemErro })
                    .FirstOrDefaultAsync(ct);

                if (job is null) return FasePerdida("aprendizado do padrão de venda");

                return job.Status switch
                {
                    TreinoStatus.Concluido => (JobResultado.Concluido, null),
                    TreinoStatus.Falha => (JobResultado.Falhou,
                        Detalhar("aprender o padrão de venda das suas lojas", job.MensagemErro)),
                    _ => (JobResultado.EmAndamento, null),
                };
            }

            case SessaoStatus.Comparando:
            {
                if (sessao.ComparacaoPbsId is not { } id) return FasePerdida("comparação dos dois métodos");

                var job = await db.ComparacoesPbs.AsNoTracking()
                    .Where(c => c.Id == id)
                    .Select(c => new { c.Status, c.MensagemErro })
                    .FirstOrDefaultAsync(ct);

                if (job is null) return FasePerdida("comparação dos dois métodos");

                return job.Status switch
                {
                    ComparacaoPbsStatus.Concluido => (JobResultado.Concluido, null),
                    ComparacaoPbsStatus.Falha => (JobResultado.Falhou,
                        Detalhar("comparar os dois métodos", job.MensagemErro)),
                    _ => (JobResultado.EmAndamento, null),
                };
            }

            default:
                return (JobResultado.EmAndamento, null);
        }
    }

    /// <summary>
    /// A etapa existia e sumiu — ou nunca foi registrada. Não há como esperar por ela, e
    /// deixar a sessão girando seria pior do que terminá-la dizendo como retomar.
    /// </summary>
    private static (JobResultado, string?) FasePerdida(string fase) => (
        JobResultado.Falhou,
        $"Não encontramos mais o processamento da etapa de {fase}, então esta comparação não pode continuar. " +
        "Envie os dados novamente para recomeçar; se o problema se repetir, procure o suporte.");

    private static string Detalhar(string acao, string? mensagemDoJob)
    {
        var texto =
            $"Não foi possível {acao}. Envie os dados novamente para tentar de novo; se o problema se repetir, " +
            $"procure o suporte com este detalhe técnico: {mensagemDoJob ?? "sem detalhe registrado"}";

        return texto.Length > 2000 ? texto[..2000] : texto;
    }

    /// <summary>
    /// Pega uma sessão em fase intermediária, com o mesmo <c>UPDLOCK/READPAST</c> das filas.
    ///
    /// <para>
    /// O <c>UPDATE</c> de <c>AtualizadoEm</c> não é cosmético e faz dois trabalhos. Primeiro,
    /// é o que dá ao <c>UPDLOCK</c> algo para segurar — um <c>SELECT</c> solto liberaria a
    /// linha no fim da consulta. Segundo, rotaciona o <c>ORDER BY</c>: sem ele, uma sessão
    /// que passa dez minutos treinando seria a primeira de toda volta e as demais nunca
    /// seriam olhadas.
    /// </para>
    ///
    /// <para>
    /// <b>Tudo que este método devolve é tudo que existe da sessão adiante</b> — o claim é a
    /// única leitura da linha. Omitir uma coluna do <c>OUTPUT</c> não quebra nada
    /// visivelmente: o retrato recebe o default do tipo, e a sessão avança criando job na rede
    /// 0 ou sem corte de treino. Foi o defeito encontrado três vezes neste repositório (ver
    /// <see cref="Training.TreinoWorker"/> e <see cref="Comparison.ComparacaoWorker"/>).
    /// Cross-rede de propósito, como as demais filas: um Worker serve todos os inquilinos, e é
    /// o <c>RedeId</c> lido aqui que amarra cada job criado ao inquilino da sessão.
    /// </para>
    /// </summary>
    private async Task<SessaoEmAndamento?> ClaimNextAsync(CancellationToken ct)
    {
        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        const string sql = """
            ;WITH cte AS (
                SELECT TOP (1) *
                FROM dbo.ComparacaoSessoes WITH (UPDLOCK, READPAST)
                WHERE Status IN ('ProcessandoDados', 'Treinando', 'Comparando')
                ORDER BY AtualizadoEm
            )
            UPDATE cte
                SET AtualizadoEm = SYSDATETIMEOFFSET()
                OUTPUT INSERTED.Id, INSERTED.RedeId, INSERTED.Status,
                       INSERTED.CargaStageId, INSERTED.TreinoJobId, INSERTED.ComparacaoPbsId,
                       INSERTED.SugestaoDataHora, INSERTED.SugestaoTipoCalculo;
            """;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new SessaoEmAndamento(
            Id: reader.GetGuid(0),
            RedeId: reader.GetInt32(1),
            Status: Enum.Parse<SessaoStatus>(reader.GetString(2)),
            CargaStageId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
            TreinoJobId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
            ComparacaoPbsId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
            SugestaoDataHora: reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            SugestaoTipoCalculo: reader.IsDBNull(7) ? null : reader.GetByte(7));
    }
}
