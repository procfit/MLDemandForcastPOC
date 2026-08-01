using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac;

namespace CosmosPro.ML.DemandForCast.Migrator;

/// <summary>
/// Publica o DACPAC do projeto <c>Database</c> no banco <c>Stage</c> usando DacFx.
/// Deploy declarativo: o DacFx compara o modelo do pacote com o banco alvo e emite
/// só o delta, então rodar duas vezes seguidas é seguro — a segunda não gera script.
/// </summary>
internal sealed class StageSchemaDeployer(ILogger<StageSchemaDeployer> logger)
{
    private const string DacpacFileName = "CosmosPro.ML.DemandForCast.Database.dacpac";

    public void Deploy(string connectionString, CancellationToken ct = default)
    {
        var dacpacPath = Path.Combine(AppContext.BaseDirectory, DacpacFileName);
        if (!File.Exists(dacpacPath))
        {
            throw new FileNotFoundException(
                $"DACPAC não encontrado em '{dacpacPath}'. O build do projeto Database deveria tê-lo copiado para junto do migrador (target IncluiDacpacDoStage).",
                dacpacPath);
        }

        var alvo = new SqlConnectionStringBuilder(connectionString);
        var nomeBanco = alvo.InitialCatalog;
        if (string.IsNullOrWhiteSpace(nomeBanco))
        {
            throw new InvalidOperationException(
                "A connection string do Stage não traz Initial Catalog; sem ele não há banco alvo para o deploy.");
        }

        // O DacFx abre a conexão antes de decidir se cria o banco alvo. Apontando para
        // `master` o primeiro deploy funciona mesmo com o `Stage` ainda inexistente —
        // o caso do primeiro `docker compose up`, em que ninguém criou os bancos.
        alvo.InitialCatalog = "master";

        logger.LogInformation("Stage: publicando '{Dacpac}' no banco '{Banco}' de {Servidor}.",
            DacpacFileName, nomeBanco, alvo.DataSource);

        using var pacote = DacPackage.Load(dacpacPath, DacSchemaModelStorageType.Memory);
        var servicos = new DacServices(alvo.ConnectionString);
        servicos.Message += (_, e) => logger.LogInformation("Stage/DacFx: {Mensagem}", e.Message);

        servicos.Deploy(pacote, nomeBanco, upgradeExisting: true, options: null, cancellationToken: ct);

        logger.LogInformation("Stage: schema publicado.");
    }
}
