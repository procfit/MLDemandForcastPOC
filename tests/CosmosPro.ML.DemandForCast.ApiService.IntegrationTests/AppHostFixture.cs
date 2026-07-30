using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Sobe o AppHost real (com SQL Server, ClickHouse, MinIO, Worker) uma vez
/// por classe de teste via <c>IClassFixture</c>. Subir leva ~60-90s por causa
/// dos containers persistentes — não use por método (`IAsyncLifetime` direto).
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = null!;
    public IImportsApi ImportsApi { get; private set; } = null!;
    public IRedesApi RedesApi { get; private set; } = null!;
    public IStageApi StageApi { get; private set; } = null!;
    public IComparacoesApi ComparacoesApi { get; private set; } = null!;

    /// <summary>Rede semeada pela migration AddRedes — usada pelos testes que não criam rede própria.</summary>
    public const int RedeDemoId = 1;

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.CosmosPro_ML_DemandForCast_AppHost>();

        builder.Services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));

        // 'poweruser-password' é parâmetro secreto sem valor no AppHost (vem de
        // user-secrets em desenvolvimento). Sem injetar aqui, a Web não sobe.
        // E-mail próprio, diferente do admin de debug (`admin@local`): o banco engine é
        // persistente e compartilhado, e reusar o e-mail fazia teste e inner loop
        // disputarem a senha do mesmo usuário.
        builder.Configuration["Parameters:poweruser-email"] = "integracao@teste.local";
        builder.Configuration["Parameters:poweruser-password"] = "TesteIntegracao!2026";

        // CommunityToolkit.Aspire.Hosting.SqlDatabaseProjects descobre o
        // caminho do .dacpac avaliando o .sqlproj via Microsoft.Build em runtime.
        // Sob `dotnet test`, MSBuild não está corretamente resolvido e a carga
        // falha com "Microsoft.Common.props not found". Como o build do .sqlproj
        // já gera o .dacpac no `bin\Debug\net10.0` da pasta do projeto, atalhamos
        // o resource para apontar direto pro arquivo via `WithDacpac` (que
        // adiciona uma DacpacMetadataAnnotation e bypassa o MSBuild evaluation).
        OverrideSqlProjectWithBuiltDacpac(builder, "stage-schema");

        App = await builder.BuildAsync();
        await App.StartAsync();

        var httpClient = App.CreateHttpClient("apiservice", endpointName: "https");
        httpClient.Timeout = TimeSpan.FromMinutes(2);
        ImportsApi = RestService.For<IImportsApi>(httpClient);
        RedesApi = RestService.For<IRedesApi>(httpClient);
        StageApi = RestService.For<IStageApi>(httpClient);
        ComparacoesApi = RestService.For<IComparacoesApi>(httpClient);

        using var healthyCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await App.ResourceNotifications.WaitForResourceHealthyAsync("apiservice", healthyCts.Token);
        }
        catch (Exception ex)
        {
            var snapshot = await CaptureResourceSnapshotAsync();
            var failedLogs = await CaptureFailedResourceLogsAsync();
            throw new InvalidOperationException(
                $"apiservice não ficou saudável.\n\nEstado dos recursos:\n{snapshot}\n\nLogs:\n{failedLogs}",
                ex);
        }
    }

    private static void OverrideSqlProjectWithBuiltDacpac(IDistributedApplicationTestingBuilder builder, string resourceName)
    {
        var resource = builder.Resources.OfType<SqlProjectResource>().Single(r => r.Name == resourceName);

        // bin do test = ...\tests\<TestProj>\bin\Debug\net10.0\
        // dacpac     = ...\CosmosPro.ML.DemandForCast.Database\bin\Debug\net10.0\CosmosPro.ML.DemandForCast.Database.dacpac
        var testBin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", "..", ".."));
        var dacpacPath = Path.Combine(
            repoRoot,
            "CosmosPro.ML.DemandForCast.Database",
            "bin", "Debug", "net10.0",
            "CosmosPro.ML.DemandForCast.Database.dacpac");

        if (!File.Exists(dacpacPath))
        {
            throw new FileNotFoundException(
                $"DACPAC não encontrado em '{dacpacPath}'. Garanta `dotnet build` do projeto Database antes de rodar testes.",
                dacpacPath);
        }

        // O SqlProjectResource criado por AddSqlProject<TProject> prioriza
        // IProjectMetadata (que faz ele resolver o .sqlproj via MSBuild). Removemos
        // essa anotação para forçar uso da DacpacMetadataAnnotation que WithDacpac
        // adiciona em seguida.
        var projectMetadataAnnotations = resource.Annotations
            .Where(a => a.GetType().GetInterfaces().Any(i => i.Name == "IProjectMetadata"))
            .ToList();
        foreach (var anno in projectMetadataAnnotations)
        {
            resource.Annotations.Remove(anno);
        }

        builder.CreateResourceBuilder(resource).WithDacpac(dacpacPath);
    }

    private async Task<string> CaptureResourceSnapshotAsync()
    {
        var states = new Dictionary<string, string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var evt in App.ResourceNotifications.WatchAsync(cts.Token))
            {
                states[evt.Resource.Name] = $"{evt.Snapshot.State?.Text ?? "?"} (health: {evt.Snapshot.HealthStatus?.ToString() ?? "?"})";
            }
        }
        catch (OperationCanceledException) { }

        return string.Join("\n", states.Select(kv => $"  - {kv.Key}: {kv.Value}"));
    }

    private async Task<string> CaptureFailedResourceLogsAsync()
    {
        var loggerService = App.Services.GetRequiredService<ResourceLoggerService>();
        var failed = new[] { "stage-schema", "apiservice", "worker", "engine-migrations" };
        var output = new List<string>();

        foreach (var name in failed)
        {
            output.Add($"--- {name} ---");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await foreach (var batch in loggerService.WatchAsync(name).WithCancellation(cts.Token))
                {
                    foreach (var line in batch)
                    {
                        output.Add($"  {line.Content}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                output.Add($"  (erro lendo logs: {ex.Message})");
            }
        }

        return string.Join("\n", output);
    }

    /// <summary>
    /// Espera o Worker terminar de processar a carga. Faz polling no GET por id
    /// porque não há sinal push — é o mesmo mecanismo que a UI usa.
    /// Devolve a carga em estado terminal (Concluida ou Falha); o teste decide
    /// se o estado é o esperado.
    /// </summary>
    public async Task<CargaStageView> WaitForCargaAsync(
        Guid id, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var limite = timeout ?? TimeSpan.FromMinutes(3);
        var deadline = DateTimeOffset.UtcNow + limite;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await ImportsApi.GetAsync(id, ct);
            if (resp.Content is { } carga && carga.Status is "Concluida" or "Falha")
            {
                return carga;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException(
            $"Carga {id} não atingiu estado terminal em {limite.TotalSeconds:F0}s. " +
            "Verifique os logs do worker.");
    }

    public async ValueTask DisposeAsync()
    {
        if (App is not null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
