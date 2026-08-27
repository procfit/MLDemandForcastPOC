using System.Data;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

/// <summary>
/// Loop de polling da fila de cargas de mercado (IQVIA) — mesmo padrão
/// competing-consumers do <see cref="ImportWorker"/>, fila separada porque o dado tem
/// ciclo de vida próprio (sobrevive aos imports do Stage) e o processamento não
/// disputa com os imports de venda.
/// </summary>
internal sealed class MercadoWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<MercadoWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MercadoWorker iniciado. Poll interval: {Interval}s.", PollInterval.TotalSeconds);

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
                logger.LogError(ex, "Erro inesperado no loop do MercadoWorker. Aguardando antes de retentar.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<MercadoProcessor>();

        var claimed = await ClaimNextAsync(ct);
        if (claimed is null)
        {
            return false;
        }

        logger.LogInformation(
            "Processando carga de mercado {Id} ({Arquivo}) da rede {RedeId}.",
            claimed.Id, claimed.NomeArquivoOriginal, claimed.RedeId);

        try
        {
            var rede = await db.Redes.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == claimed.RedeId, ct)
                ?? throw new InvalidOperationException(
                    $"Rede {claimed.RedeId} não existe. Cadastre a rede antes de importar.");

            if (!rede.Ativo)
            {
                throw new InvalidOperationException(
                    $"Rede {rede.Id} ('{rede.Nome}') está inativa e não aceita importação.");
            }

            var (linhas, resumoJson) = await processor.ProcessAsync(claimed, ct);

            await db.MercadoCargas
                .Where(c => c.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, MercadoCargaStatus.Concluida)
                    .SetProperty(c => c.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(c => c.LinhasImportadas, linhas)
                    .SetProperty(c => c.ResumoJson, resumoJson), ct);

            logger.LogInformation("Carga de mercado {Id} concluída. Observações gravadas: {Linhas}.", claimed.Id, linhas);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Carga de mercado {Id} falhou.", claimed.Id);

            await db.MercadoCargas
                .Where(c => c.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, MercadoCargaStatus.Falha)
                    .SetProperty(c => c.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(c => c.MensagemErro, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message), ct);
        }

        return true;
    }

    private async Task<MercadoCarga?> ClaimNextAsync(CancellationToken ct)
    {
        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        const string sql = """
            ;WITH cte AS (
                SELECT TOP (1) *
                FROM dbo.MercadoCargas WITH (UPDLOCK, READPAST)
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

        return new MercadoCarga
        {
            Id = reader.GetGuid(0),
            BlobKey = reader.GetString(1),
            NomeArquivoOriginal = reader.GetString(2),
            RedeId = reader.GetInt32(3),
            Status = MercadoCargaStatus.Processando,
            DataAgendamento = default,
        };
    }
}
