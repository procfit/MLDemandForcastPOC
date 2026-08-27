using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker;

/// <summary>
/// Encerra job reclamado por processo que nunca voltou.
///
/// <para>
/// As quatro filas reclamam com <c>WHERE Status = 'Pendente'</c> e gravam <c>Processando</c>,
/// sem lease e sem heartbeat. Quem morre no meio deixa a linha em <c>Processando</c> para
/// sempre: nenhum worker a reclama de novo (o <c>WHERE</c> não a alcança) e nenhum a encerra.
/// Em produção isso custou dois dias — o processo do worker saiu, o container parado foi
/// removido pela limpeza do Docker junto com o log, e a carga ficou eternamente "importando"
/// na tela do comprador.
/// </para>
///
/// <para>
/// <b>Isto não substitui <see cref="SessaoJobs.FaseAbandonada"/>, complementa.</b> Aquele
/// encerra a <b>sessão</b> a partir da idade da reclamação e é o que desbloqueia o comprador;
/// este encerra a <b>linha do job</b>, que é o que a página técnica mostra e o que a fila
/// enxerga. Sem ele, a sessão morre com motivo e a carga continua afirmando "Processando" —
/// dois registros do mesmo evento discordando. Os dois usam o mesmo relógio
/// (<see cref="ComparacaoSessao.LimiteDeFaseSemProgresso"/>) e o mesmo texto
/// (<see cref="SessaoJobs.MotivoDeAbandono"/>) exatamente para não poderem divergir.
/// </para>
///
/// <para>
/// <b>Por idade, e não "tudo que está Processando no startup".</b> A varredura de startup
/// seria mais rápida e está errada: o modelo admite mais de um worker (é o que os hints
/// <c>UPDLOCK, READPAST</c> das filas sustentam), e ali um processo subindo mataria o job que
/// outro está executando naquele instante. A idade é o único sinal que distingue "parou" de
/// "está demorando" sem heartbeat. O preço é a janela: até o limite, uma fila órfã continua
/// parecendo viva. Encurtar isso de verdade pede lease com renovação, não um número menor.
/// </para>
///
/// <para>
/// <c>DataInicioProcessamento</c> nulo fica de fora sozinho, porque em SQL a comparação com
/// <c>NULL</c> não é verdadeira — e é o desfecho correto, pelo mesmo motivo documentado em
/// <see cref="SessaoJobs.FaseAbandonada"/>: job ainda <c>Pendente</c> não é abandono, é fila
/// esperando worker.
/// </para>
/// </summary>
internal sealed class OrfaosWorker(
    IServiceProvider services,
    ILogger<OrfaosWorker> logger) : BackgroundService
{
    /// <summary>
    /// Cadência folgada de propósito: o que esta varredura persegue já está parado há horas,
    /// então chegar dez minutos mais tarde não muda nada para quem espera — e um
    /// <c>UPDATE</c> em quatro tabelas a cada volta de 5 segundos seria carga constante no
    /// banco para não fazer nada em 99,9% das voltas.
    /// </summary>
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "OrfaosWorker iniciado. Varredura a cada {Intervalo}min, limite de {Limite}h sem progresso.",
            Intervalo.TotalMinutes, ComparacaoSessao.LimiteDeFaseSemProgresso.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await VarrerAsync(stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado na varredura de jobs órfãos.");
            }

            try
            {
                await Task.Delay(Intervalo, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }
    }

    private async Task VarrerAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        var agora = DateTimeOffset.UtcNow;
        var corte = agora - ComparacaoSessao.LimiteDeFaseSemProgresso;

        var cargas = await db.CargasStage
            .Where(c => c.Status == CargaStageStatus.Processando && c.DataInicioProcessamento < corte)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CargaStageStatus.Falha)
                .SetProperty(c => c.DataConclusao, agora)
                .SetProperty(c => c.MensagemErro, SessaoJobs.MotivoDeAbandono(SessaoJobs.Fases.Importacao)), ct);

        var treinos = await db.TreinoJobs
            .Where(t => t.Status == TreinoStatus.Processando && t.DataInicioProcessamento < corte)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TreinoStatus.Falha)
                .SetProperty(t => t.DataConclusao, agora)
                .SetProperty(t => t.MensagemErro, SessaoJobs.MotivoDeAbandono(SessaoJobs.Fases.Treino)), ct);

        var comparacoes = await db.ComparacoesPbs
            .Where(c => c.Status == ComparacaoPbsStatus.Processando && c.DataInicioProcessamento < corte)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ComparacaoPbsStatus.Falha)
                .SetProperty(c => c.DataConclusao, agora)
                .SetProperty(c => c.MensagemErro, SessaoJobs.MotivoDeAbandono(SessaoJobs.Fases.Comparacao)), ct);

        // Simulação não pertence a sessão nenhuma (é fluxo técnico, F8), então a mensagem não
        // fala de reenviar dados: quem a dispara é o operador, na própria tela.
        var simulacoes = await db.SimulacoesCompra
            .Where(x => x.Status == SimulacaoStatus.Processando && x.DataInicioProcessamento < corte)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, SimulacaoStatus.Falha)
                .SetProperty(x => x.DataConclusao, agora)
                .SetProperty(x => x.MensagemErro, SimulacaoInterrompida), ct);

        // Carga de mercado (IQVIA, F16) também não pertence a sessão: o remédio é
        // reenviar o XLSX na própria tela de dados de mercado.
        var mercado = await db.MercadoCargas
            .Where(x => x.Status == MercadoCargaStatus.Processando && x.DataInicioProcessamento < corte)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, MercadoCargaStatus.Falha)
                .SetProperty(x => x.DataConclusao, agora)
                .SetProperty(x => x.MensagemErro, MercadoInterrompido), ct);

        var total = cargas + treinos + comparacoes + simulacoes + mercado;
        if (total > 0)
        {
            logger.LogWarning(
                "Jobs órfãos encerrados: {Cargas} carga(s), {Treinos} treino(s), {Comparacoes} comparação(ões), " +
                "{Simulacoes} simulação(ões), {Mercado} carga(s) de mercado. Reclamados antes de {Corte:u} e sem conclusão.",
                cargas, treinos, comparacoes, simulacoes, mercado, corte);
        }
    }

    private const string SimulacaoInterrompida =
        "O processamento desta simulação foi interrompido antes de terminar e não deu mais sinal de progresso. " +
        "Rode a simulação novamente; se acontecer de novo, procure o suporte.";

    private const string MercadoInterrompido =
        "A importação deste arquivo foi interrompida antes de terminar e não deu mais sinal de progresso. " +
        "Envie o arquivo novamente; se acontecer de novo, procure o suporte.";
}
