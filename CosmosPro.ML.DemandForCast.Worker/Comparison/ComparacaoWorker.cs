using System.Data;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker.Comparison;

/// <summary>
/// Loop de polling dos jobs de comparação contra o ERP (engine.ComparacoesPbs),
/// mesmo padrão competing-consumers das demais filas (UPDLOCK/READPAST) — ver
/// <see cref="Training.TreinoWorker"/>. Cross-rede de propósito: um Worker serve
/// todos os inquilinos e pega a próxima pendente de qualquer um.
/// </summary>
internal sealed class ComparacaoWorker(
    IServiceProvider services,
    IConfiguration config,
    ILogger<ComparacaoWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ComparacaoWorker iniciado. Poll interval: {Interval}s.", PollInterval.TotalSeconds);

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
                logger.LogError(ex, "Erro inesperado no loop do ComparacaoWorker.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<ComparacaoProcessor>();

        var claimed = await ClaimNextAsync(ct);
        if (claimed is null) return false;

        logger.LogInformation(
            "Comparando job {Id} (rede={Rede} · treino={Treino} · tipo={Tipo} · {Inicio}→{Fim}).",
            claimed.Id, claimed.RedeId, claimed.TreinoJobId, claimed.TipoCalculo,
            claimed.JanelaInicio, claimed.JanelaFim);

        try
        {
            var outcome = await processor.ProcessAsync(claimed, ct);

            await db.ComparacoesPbs
                .Where(c => c.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ComparacaoPbsStatus.Concluido)
                    .SetProperty(c => c.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(c => c.ResultadoJson, outcome.ResultadoJson), ct);

            logger.LogInformation("Comparação {Id} concluída.", claimed.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Comparação {Id} falhou.", claimed.Id);
            await db.ComparacoesPbs
                .Where(c => c.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ComparacaoPbsStatus.Falha)
                    .SetProperty(c => c.DataConclusao, DateTimeOffset.UtcNow)
                    .SetProperty(c => c.MensagemErro, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message), ct);
        }
        return true;
    }

    private async Task<ComparacaoPbs?> ClaimNextAsync(CancellationToken ct)
    {
        var connStr = config.GetConnectionString("engine")
            ?? throw new InvalidOperationException("Connection string 'engine' não encontrada.");

        // O claim é a ÚNICA leitura da linha — tudo que o ComparacaoProcessor lê do job
        // precisa estar no OUTPUT. Omitir uma coluna aqui não quebra nada visivelmente:
        // o processor recebe o default do tipo e roda contra rede/janela/método errados
        // (foi o defeito encontrado no TreinoWorker, onde RedeId e TreinoAte faltavam).
        const string sql = """
            ;WITH cte AS (
                SELECT TOP (1) *
                FROM dbo.ComparacoesPbs WITH (UPDLOCK, READPAST)
                WHERE Status = 'Pendente'
                ORDER BY DataAgendamento
            )
            UPDATE cte
                SET Status = 'Processando',
                    DataInicioProcessamento = SYSDATETIMEOFFSET()
                OUTPUT INSERTED.Id, INSERTED.RedeId, INSERTED.TreinoJobId,
                       INSERTED.JanelaInicio, INSERTED.JanelaFim, INSERTED.TipoCalculo;
            """;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ComparacaoPbs
        {
            Id = reader.GetGuid(0),
            RedeId = reader.GetInt32(1),
            TreinoJobId = reader.GetGuid(2),
            JanelaInicio = reader.GetFieldValue<DateOnly>(3),
            JanelaFim = reader.GetFieldValue<DateOnly>(4),
            TipoCalculo = reader.GetByte(5),
            Status = ComparacaoPbsStatus.Processando,
        };
    }
}
