using System.Data;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker.Purchasing;

/// <summary>
/// Loop de polling dos jobs de simulação de compra (engine.SimulacoesCompra),
/// mesmo padrão dos demais workers (UPDLOCK/READPAST). Roda em paralelo aos
/// outros workers sem disputar fila.
/// </summary>
internal sealed class SimulacaoWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<SimulacaoWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SimulacaoWorker iniciado. Poll interval: {Interval}s.", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await TryProcessNextAsync(stoppingToken);
                if (!processed)
                    await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado no loop do SimulacaoWorker.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<SimulacaoProcessor>();

        var claimed = await ClaimNextAsync(ct);
        if (claimed is null) return false;

        logger.LogInformation("Simulando job {Id} (rede={Rede} · treino={Treino} · janela={Janela}d).",
            claimed.Id, claimed.RedeId, claimed.TreinoJobId, claimed.JanelaDias);
        try
        {
            var outcome = await processor.ProcessAsync(claimed, ct);

            await db.SimulacoesCompra
                .Where(s => s.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, SimulacaoStatus.Concluido)
                    .SetProperty(j => j.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(j => j.ResultadoJson, outcome.ResultadoJson)
                    .SetProperty(j => j.SeriesSimuladas, outcome.SeriesSimuladas), ct);

            logger.LogInformation("Simulação {Id} concluída.", claimed.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Simulação {Id} falhou.", claimed.Id);
            await db.SimulacoesCompra
                .Where(s => s.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, SimulacaoStatus.Falha)
                    .SetProperty(j => j.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(j => j.MensagemErro, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message), ct);
        }
        return true;
    }

    private async Task<SimulacaoCompra?> ClaimNextAsync(CancellationToken ct)
    {
        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        // O claim é a ÚNICA leitura da linha — tudo que o SimulacaoProcessor lê do job
        // precisa estar no OUTPUT. Omitir uma coluna não quebra nada visivelmente: o
        // processor recebe o default do tipo e roda contra rede/parâmetros errados. Foi o
        // defeito do TreinoWorker (RedeId e TreinoAte faltando) e, depois, o mesmo aqui —
        // com RedeId 0 o Stage não devolve observação nenhuma e toda simulação falhava.
        const string sql = """
            ;WITH cte AS (
                SELECT TOP (1) *
                FROM dbo.SimulacoesCompra WITH (UPDLOCK, READPAST)
                WHERE Status = 'Pendente'
                ORDER BY DataAgendamento
            )
            UPDATE cte
                SET Status = 'Processando',
                    DataInicioProcessamento = SYSDATETIMEOFFSET()
                OUTPUT INSERTED.Id, INSERTED.RedeId, INSERTED.TreinoJobId, INSERTED.JanelaDias,
                       INSERTED.LeadTimeDias, INSERTED.CicloDias, INSERTED.FatorServico;
            """;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new SimulacaoCompra
        {
            Id = reader.GetGuid(0),
            RedeId = reader.GetInt32(1),
            TreinoJobId = reader.GetGuid(2),
            JanelaDias = reader.GetInt32(3),
            LeadTimeDias = reader.GetInt32(4),
            CicloDias = reader.GetInt32(5),
            FatorServico = reader.GetDouble(6),
            Status = SimulacaoStatus.Processando,
        };
    }
}
