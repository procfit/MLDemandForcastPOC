using CosmosPro.ML.DemandForCast.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CosmosPro.ML.DemandForCast.Migrator;

/// <summary>
/// Aplica as migrations do EF Core ao banco <c>engine</c>. Idempotente por natureza:
/// o EF consulta <c>__EFMigrationsHistory</c> e só aplica o que falta.
/// </summary>
internal sealed class EngineSchemaMigrator(EngineDbContext db, ILogger<EngineSchemaMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        // CanConnect antes de GetPendingMigrations porque a leitura do histórico exige
        // que o banco exista, e no primeiro start ele não existe — quem o cria é o
        // próprio MigrateAsync.
        if (await db.Database.CanConnectAsync(ct))
        {
            var pendentes = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pendentes.Count == 0)
            {
                logger.LogInformation("engine: schema já atualizado, nenhuma migration pendente.");
                return;
            }

            logger.LogInformation("engine: aplicando {Quantidade} migration(s) pendente(s): {Migrations}.",
                pendentes.Count, string.Join(", ", pendentes));
        }
        else
        {
            logger.LogInformation("engine: banco ainda não existe; será criado e migrado do zero.");
        }

        await db.Database.MigrateAsync(ct);
        logger.LogInformation("engine: migrations aplicadas.");
    }
}
