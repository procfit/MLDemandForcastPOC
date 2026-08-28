using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CosmosPro.ML.DemandForCast.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minio;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Sobe o AppHost real (com SQL Server, MinIO, Worker) uma vez
/// por classe de teste via <c>IClassFixture</c>. Subir leva ~60-90s por causa
/// dos containers persistentes — não use por método (`IAsyncLifetime` direto).
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    /// <summary>
    /// Impede que este AppHost coexista com o do projeto E2E — ver
    /// <see cref="AppHostExclusiveLock"/>. Os containers são persistentes e
    /// compartilhados, então dois AppHosts simultâneos são um só ambiente sendo
    /// escrito por dois donos.
    /// </summary>
    private AppHostExclusiveLock? _exclusividade;

    public DistributedApplication App { get; private set; } = null!;
    public IImportsApi ImportsApi { get; private set; } = null!;
    public IMercadoApi MercadoApi { get; private set; } = null!;
    public IRedesApi RedesApi { get; private set; } = null!;
    public IStageApi StageApi { get; private set; } = null!;
    public IComparacoesApi ComparacoesApi { get; private set; } = null!;
    public IQuestionariosApi QuestionariosApi { get; private set; } = null!;
    public IComparisonApi ComparisonApi { get; private set; } = null!;
    public ITrainingApi TrainingApi { get; private set; } = null!;
    public IPurchasingApi PurchasingApi { get; private set; } = null!;
    public IExtratorApi ExtratorApi { get; private set; } = null!;

    /// <summary>Rede semeada pela migration AddRedes — usada pelos testes que não criam rede própria.</summary>
    public const int RedeDemoId = 1;

    public async ValueTask InitializeAsync()
    {
        _exclusividade = await AppHostExclusiveLock.AcquireAsync();

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

        // `dbgate-password` é parâmetro secreto sem valor no AppHost — sem injetar aqui, o
        // AppHost não sobe e nenhum teste roda. Nada nesta suíte usa o DbGate; ele existe
        // no modelo porque tem de existir no compose publicado.
        builder.Configuration["Parameters:dbgate-password"] = "TesteDbGate!2026";

        // Aqui existia um remendo (`OverrideSqlProjectWithBuiltDacpac`): o
        // `AddSqlProject` do CommunityToolkit descobria o caminho do .dacpac avaliando o
        // .sqlproj via Microsoft.Build em runtime, e sob `dotnet test` isso falhava com
        // "Microsoft.Common.props not found". Com o schema aplicado pelo projeto
        // `db-migrator`, que carrega o .dacpac copiado para o próprio bin, não há mais
        // avaliação de MSBuild em runtime e o remendo deixou de existir.

        App = await builder.BuildAsync();

        // `StartAsync` sem token era o único await sem teto de toda a subida — e, num run de
        // CI, foi ele quem consumiu 30 minutos de log em branco antes de alguém cancelar à
        // mão. A espera por saúde, logo abaixo, já tinha teto de 5 minutos e tira uma foto
        // dos recursos ao estourar; esta não tinha nada, então o modo de falhar era silêncio
        // até o teto do passo no workflow. Seis minutos porque as imagens já vêm pré-baixadas
        // no CI (passo "Pré-baixar imagens de infraestrutura") e localmente sobem em ~90s.
        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            await App.StartAsync(startCts.Token);
        }
        catch (OperationCanceledException ex)
        {
            var snapshot = await CaptureResourceSnapshotAsync();
            throw new TimeoutException(
                $"O AppHost de teste não terminou de subir em 6 min.\n\nEstado dos recursos:\n{snapshot}",
                ex);
        }

        var httpClient = App.CreateHttpClient("apiservice", endpointName: "https");
        httpClient.Timeout = TimeSpan.FromMinutes(2);
        ImportsApi = RestService.For<IImportsApi>(httpClient);
        MercadoApi = RestService.For<IMercadoApi>(httpClient);
        RedesApi = RestService.For<IRedesApi>(httpClient);
        StageApi = RestService.For<IStageApi>(httpClient);
        ComparacoesApi = RestService.For<IComparacoesApi>(httpClient);
        QuestionariosApi = RestService.For<IQuestionariosApi>(httpClient);
        ComparisonApi = RestService.For<IComparisonApi>(httpClient);
        TrainingApi = RestService.For<ITrainingApi>(httpClient);
        PurchasingApi = RestService.For<IPurchasingApi>(httpClient);
        ExtratorApi = RestService.For<IExtratorApi>(httpClient);

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
        var failed = new[] { "db-migrator", "apiservice", "worker" };
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

    /// <summary>
    /// Connection string do banco Stage tal como o Worker a recebe. Testes que
    /// exercitam um loader diretamente (em vez de passar pela API) precisam dela.
    /// </summary>
    public async Task<string> GetStageConnectionStringAsync(CancellationToken ct = default)
        => await App.GetConnectionStringAsync("Stage", ct)
           ?? throw new InvalidOperationException("Recurso 'Stage' sem connection string.");

    /// <summary>
    /// Connection string do banco <c>engine</c>. Necessária para os testes que precisam plantar
    /// um estado que a API não sabe produzir — sessão apontando para um job abandonado, por
    /// exemplo — e depois observar o Worker reagir a ele.
    /// </summary>
    public async Task<string> GetEngineConnectionStringAsync(CancellationToken ct = default)
        => await App.GetConnectionStringAsync("engine", ct)
           ?? throw new InvalidOperationException("Recurso 'engine' sem connection string.");

    /// <summary>
    /// Cliente MinIO direto do teste, para semear/limpar o bucket <c>extrator</c> — a
    /// apiservice só lê (publicação é manual, fora do processo). A connection string do
    /// recurso vem no formato <c>Endpoint=http://host:port;AccessKey=..;SecretKey=..</c>
    /// (CommunityToolkit.Aspire.Hosting.Minio); parseado aqui em vez de reusar
    /// <c>AddMinioClient</c> porque este é o processo de teste, não a apiservice.
    /// </summary>
    public async Task<IMinioClient> GetMinioClientAsync(CancellationToken ct = default)
    {
        var cs = await App.GetConnectionStringAsync("minio", ct)
                  ?? throw new InvalidOperationException("Recurso 'minio' sem connection string.");

        var partes = cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);

        var endpoint = new Uri(partes["Endpoint"]);
        return new MinioClient()
            .WithEndpoint(endpoint.Host, endpoint.Port)
            .WithCredentials(partes["AccessKey"], partes["SecretKey"])
            .Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (App is not null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }

        // Só depois do AppHost realmente parado: soltar antes deixaria o próximo
        // processo subir enquanto apiservice/worker daqui ainda escrevem.
        if (_exclusividade is not null)
        {
            await _exclusividade.DisposeAsync();
        }
    }
}
