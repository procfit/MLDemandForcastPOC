using System.Data;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker.Training;

/// <summary>
/// Loop de polling dos jobs de treino (engine.TreinoJobs), espelhando o
/// <see cref="ImportWorker"/>: claim com UPDLOCK/READPAST, marca Processando,
/// delega ao <see cref="TreinoProcessor"/>, grava resultado/erro.
/// </summary>
internal sealed class TreinoWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<TreinoWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TreinoWorker iniciado. Poll interval: {Interval}s.", PollInterval.TotalSeconds);

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
                logger.LogError(ex, "Erro inesperado no loop do TreinoWorker.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<TreinoProcessor>();

        var claimed = await ClaimNextAsync(ct);
        if (claimed is null) return false;

        logger.LogInformation(
            "Treinando job {Id} (maxSkus={MaxSkus}).",
            claimed.Id, claimed.MaxSkus?.ToString() ?? "todos");
        try
        {
            var outcome = await processor.ProcessAsync(claimed, ct);

            await db.TreinoJobs
                .Where(j => j.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, TreinoStatus.Concluido)
                    .SetProperty(j => j.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(j => j.ModeloBlobKey, outcome.ModeloBlobKey)
                    .SetProperty(j => j.ResultadoJson, outcome.ResultadoJson)
                    .SetProperty(j => j.FeaturesGeradas, outcome.Features), ct);

            logger.LogInformation("Treino {Id} concluído.", claimed.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Treino {Id} falhou.", claimed.Id);
            await db.TreinoJobs
                .Where(j => j.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, TreinoStatus.Falha)
                    .SetProperty(j => j.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(j => j.MensagemErro, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message), ct);
        }
        return true;
    }

    private async Task<TreinoJob?> ClaimNextAsync(CancellationToken ct)
    {
        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        const string sql = """
            ;WITH cte AS (
                SELECT TOP (1) *
                FROM dbo.TreinoJobs WITH (UPDLOCK, READPAST)
                WHERE Status = 'Pendente'
                ORDER BY DataAgendamento
            )
            UPDATE cte
                SET Status = 'Processando',
                    DataInicioProcessamento = SYSDATETIMEOFFSET()
                OUTPUT INSERTED.Id, INSERTED.MaxSkus, INSERTED.RedeId, INSERTED.TreinoAte;
            """;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new TreinoJob
        {
            Id = reader.GetGuid(0),
            MaxSkus = reader.IsDBNull(1) ? null : reader.GetInt32(1),
            // RedeId e TreinoAte não vinham no OUTPUT: o processor recebia RedeId 0 e
            // treinava sobre o Stage de rede nenhuma. Tudo que o TreinoProcessor lê do
            // job precisa estar aqui — o claim é a única leitura da linha.
            RedeId = reader.GetInt32(2),
            TreinoAte = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3),
            Status = TreinoStatus.Processando,
        };
    }
}
