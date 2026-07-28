using System.Data;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker;

/// <summary>
/// Loop de polling. Pega a próxima carga em status `Pendente` usando
/// `WITH (UPDLOCK, READPAST)` (competing-consumers em SQL Server puro),
/// marca como `Processando`, delega para o `CargaProcessor`, e atualiza
/// o status final (`Concluida` ou `Falha`).
/// </summary>
internal sealed class ImportWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<ImportWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ImportWorker iniciado. Poll interval: {Interval}s.", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await TryProcessNextAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado no loop do ImportWorker. Aguardando antes de retentar.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<CargaProcessor>();

        var claimed = await ClaimNextAsync(db, ct);
        if (claimed is null)
        {
            return false;
        }

        logger.LogInformation(
            "Processando carga {Id} ({Arquivo}) da rede {RedeId}.",
            claimed.Id, claimed.NomeArquivoOriginal, claimed.RedeId);

        try
        {
            // Rede inexistente ou inativa falha a carga com mensagem clara, em vez
            // de estourar violação de FK no meio do SqlBulkCopy.
            var rede = await db.Redes.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == claimed.RedeId, ct)
                ?? throw new InvalidOperationException(
                    $"Rede {claimed.RedeId} não existe. Cadastre a rede antes de importar.");

            if (!rede.Ativo)
            {
                throw new InvalidOperationException(
                    $"Rede {rede.Id} ('{rede.Nome}') está inativa e não aceita importação.");
            }

            var totalRows = await processor.ProcessAsync(claimed, rede, ct);

            await db.CargasStage
                .Where(c => c.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, CargaStageStatus.Concluida)
                    .SetProperty(c => c.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(c => c.LinhasImportadas, totalRows), ct);

            logger.LogInformation("Carga {Id} concluída. Linhas importadas: {Linhas}.", claimed.Id, totalRows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Carga {Id} falhou.", claimed.Id);

            await db.CargasStage
                .Where(c => c.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, CargaStageStatus.Falha)
                    .SetProperty(c => c.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(c => c.MensagemErro, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message), ct);
        }

        return true;
    }

    /// <summary>
    /// Pega a próxima carga em `Pendente` (ordem cronológica), em uma única
    /// round-trip atomicamente: UPDATE com OUTPUT + hints UPDLOCK/READPAST.
    /// Concorrência: múltiplos workers podem rodar — `READPAST` faz cada um
    /// pular linhas já locked por outros.
    /// </summary>
    private async Task<CargaStage?> ClaimNextAsync(EngineDbContext db, CancellationToken ct)
    {
        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        const string sql = """
            ;WITH cte AS (
                SELECT TOP (1) *
                FROM dbo.CargasStage WITH (UPDLOCK, READPAST)
                WHERE Status = 'Pendente'
                ORDER BY DataAgendamento
            )
            UPDATE cte
                SET Status = 'Processando',
                    DataInicioProcessamento = SYSDATETIMEOFFSET()
                OUTPUT INSERTED.Id, INSERTED.BlobKey, INSERTED.NomeArquivoOriginal, INSERTED.RedeId;
            """;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct)) return null;

        return new CargaStage
        {
            Id = reader.GetGuid(0),
            BlobKey = reader.GetString(1),
            NomeArquivoOriginal = reader.GetString(2),
            RedeId = reader.GetInt32(3),
            Status = CargaStageStatus.Processando,
            DataAgendamento = default,
        };
    }
}
