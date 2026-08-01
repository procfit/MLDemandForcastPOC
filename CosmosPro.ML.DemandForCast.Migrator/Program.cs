using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Migrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddSqlServerDbContext<EngineDbContext>("engine");

using var host = builder.Build();
await host.StartAsync();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var configuration = host.Services.GetRequiredService<IConfiguration>();

// Os dois bancos num processo só, nesta ordem, de propósito: num VPS o operador tem
// uma coisa para checar e um log para ler, e não existe cenário em que se queira um
// banco migrado sem o outro — a aplicação não sobe pela metade.
var etapa = "inicialização";
int exitCode = 0;
try
{
    logger.LogInformation("Migrador iniciado: Stage (DACPAC) e, em seguida, engine (EF Core migrations).");

    etapa = "Stage (DACPAC)";
    var stageConnectionString = configuration.GetConnectionString("Stage")
        ?? throw new InvalidOperationException(
            "Connection string 'Stage' ausente. O AppHost injeta via WithReference(stageDb).");
    new StageSchemaDeployer(host.Services.GetRequiredService<ILogger<StageSchemaDeployer>>())
        .Deploy(stageConnectionString);

    etapa = "engine (EF Core migrations)";
    await using (var scope = host.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        await new EngineSchemaMigrator(db, host.Services.GetRequiredService<ILogger<EngineSchemaMigrator>>())
            .MigrateAsync();
    }

    logger.LogInformation("Migrador concluído: Stage e engine no schema esperado.");
}
catch (Exception ex)
{
    // O código de saída é o contrato com o `condition: service_completed_successfully`
    // do compose. Se este processo terminar em 0 depois de falhar, apiservice, web e
    // worker sobem contra um banco pela metade e o erro reaparece adiante, deslocado.
    logger.LogError(ex, "Migração falhou na etapa {Etapa}. Nenhuma etapa seguinte foi executada.", etapa);
    exitCode = 1;
}
finally
{
    await host.StopAsync();
}

return exitCode;
