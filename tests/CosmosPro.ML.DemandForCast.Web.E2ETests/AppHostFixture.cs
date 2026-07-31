using System.Data;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CosmosPro.ML.DemandForCast.Tests.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// Sobe o AppHost real (containers persistentes + apiservice + webfrontend +
/// worker) e fornece um <c>IBrowser</c> Playwright para os testes E2E. O Aspire
/// não roteia automaticamente para o webfrontend via service discovery em
/// browser — usamos o endpoint HTTPS publicado pelo Aspire diretamente.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    /// <summary>
    /// Impede que este AppHost coexista com o do projeto de integração — ver
    /// <see cref="AppHostExclusiveLock"/>. Sem isto, `dotnet test` na solução sobe os
    /// dois em paralelo sobre os mesmos containers persistentes, e a latência extra
    /// atrasa o carregamento das páginas Blazor além dos tempos de espera do Playwright.
    /// </summary>
    private AppHostExclusiveLock? _exclusividade;

    public DistributedApplication App { get; private set; } = null!;
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    public string WebfrontendUrl { get; private set; } = null!;

    /// <summary>
    /// Credenciais do PowerUser semeado — usadas pelo helper de login.
    /// <para>
    /// E-mail próprio, deliberadamente diferente do admin de debug (`admin@local`):
    /// o banco <c>engine</c> é persistente e compartilhado, e usar o mesmo e-mail fazia
    /// o teste e o inner loop disputarem a senha do mesmo usuário — quem criasse
    /// primeiro ganhava, e o outro falhava no login sem pista da causa.
    /// </para>
    /// </summary>
    public const string PowerUserEmail = "e2e@teste.local";
    public const string PowerUserSenha = "TesteE2E!2026";

    public async ValueTask InitializeAsync()
    {
        _exclusividade = await AppHostExclusiveLock.AcquireAsync();

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.CosmosPro_ML_DemandForCast_AppHost>();

        builder.Services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));

        // O AppHost declara 'poweruser-password' como parâmetro secreto sem valor.
        // Fora dos testes ele vem de user-secrets; aqui precisa ser injetado, senão
        // o IdentityBootstrapper falha de propósito no startup da Web.
        builder.Configuration["Parameters:poweruser-email"] = PowerUserEmail;
        builder.Configuration["Parameters:poweruser-password"] = PowerUserSenha;

        OverrideSqlProjectWithBuiltDacpac(builder, "stage-schema");

        App = await builder.BuildAsync();
        await App.StartAsync();

        using var healthyCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await App.ResourceNotifications.WaitForResourceHealthyAsync("webfrontend", healthyCts.Token);

        WebfrontendUrl = App.GetEndpoint("webfrontend", "https").ToString();

        // Garante navegadores baixados (idempotente — pula se já existem).
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"playwright install retornou {exitCode}");
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
    }

    /// <summary>
    /// Abre uma página já autenticada como PowerUser. Depois de F11 toda página de
    /// dados exige login, então os cenários E2E precisam passar por aqui primeiro.
    /// </summary>
    public async Task<IPage> NovaPaginaLogadaAsync(
        string? email = null, string? senha = null)
    {
        var page = await Browser.NewPageAsync(new BrowserNewPageOptions
        {
            IgnoreHTTPSErrors = true,
        });

        var baseUrl = WebfrontendUrl.TrimEnd('/');
        await page.GotoAsync($"{baseUrl}/login");

        await page.FillAsync("input[name='email']", email ?? PowerUserEmail);
        await page.FillAsync("input[name='senha']", senha ?? PowerUserSenha);
        await page.ClickAsync("button[type='submit']");

        // O POST redireciona para "/" em caso de sucesso e para /login?erro=N se falhar.
        await page.WaitForURLAsync(u => !u.Contains("/login"), new PageWaitForURLOptions
        {
            Timeout = 30_000,
        });

        return page;
    }

    /// <summary>
    /// Semeia uma execução de comparação já concluída, com <paramref name="resultadoJson"/>
    /// pronto, na rede que a página vai enxergar.
    ///
    /// <para>
    /// Escreve direto no banco <c>engine</c> porque o caminho legítimo — treinar um modelo e
    /// rodar o Worker — leva dezenas de minutos e depende de dado importado; o que estes
    /// testes exercitam é o <b>render</b> do bloco de resultados, não o cálculo dele.
    /// </para>
    ///
    /// <para>
    /// A rede é a mesma que <c>RedeContext</c> resolve para um PowerUser sem seleção (a
    /// primeira ativa por id); repetir a consulta aqui é o que mantém o dado semeado visível
    /// para a sessão do teste. As linhas anteriores com o mesmo <paramref name="treinoJobId"/>
    /// são removidas antes: o banco é persistente e reexecutar o teste acumularia execuções
    /// idênticas na grade.
    /// </para>
    /// </summary>
    public async Task<Guid> SemearComparacaoConcluidaAsync(
        Guid treinoJobId,
        DateOnly janelaInicio,
        DateOnly janelaFim,
        byte tipoCalculo,
        string resultadoJson,
        CancellationToken ct = default)
    {
        var connectionString = await App.GetConnectionStringAsync("engine", ct)
            ?? throw new InvalidOperationException("Recurso 'engine' sem connection string.");

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using (var limpeza = conn.CreateCommand())
        {
            limpeza.CommandText = "DELETE FROM dbo.ComparacoesPbs WHERE TreinoJobId = @treino;";
            limpeza.Parameters.AddWithValue("@treino", treinoJobId);
            await limpeza.ExecuteNonQueryAsync(ct);
        }

        int redeId;
        await using (var consulta = conn.CreateCommand())
        {
            consulta.CommandText = "SELECT TOP 1 Id FROM dbo.Redes WHERE Ativo = 1 ORDER BY Id;";
            redeId = (int?)await consulta.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Nenhuma rede ativa no banco engine.");
        }

        var id = Guid.CreateVersion7();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO dbo.ComparacoesPbs
                    (Id, RedeId, Status, DataAgendamento, DataInicioProcessamento, DataConclusao,
                     TreinoJobId, JanelaInicio, JanelaFim, TipoCalculo, ResultadoJson, MensagemErro)
                VALUES
                    (@id, @redeId, 'Concluido', @agendamento, @inicio, @conclusao,
                     @treino, @janelaInicio, @janelaFim, @tipoCalculo, @resultado, NULL);
                """;
            var agora = DateTimeOffset.UtcNow;
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@redeId", redeId);
            insert.Parameters.AddWithValue("@agendamento", agora);
            insert.Parameters.AddWithValue("@inicio", agora);
            insert.Parameters.AddWithValue("@conclusao", agora);
            insert.Parameters.AddWithValue("@treino", treinoJobId);
            insert.Parameters.Add("@janelaInicio", SqlDbType.Date).Value = janelaInicio.ToDateTime(TimeOnly.MinValue);
            insert.Parameters.Add("@janelaFim", SqlDbType.Date).Value = janelaFim.ToDateTime(TimeOnly.MinValue);
            insert.Parameters.AddWithValue("@tipoCalculo", tipoCalculo);
            insert.Parameters.AddWithValue("@resultado", resultadoJson);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    private static void OverrideSqlProjectWithBuiltDacpac(IDistributedApplicationTestingBuilder builder, string resourceName)
    {
        var resource = builder.Resources.OfType<SqlProjectResource>().Single(r => r.Name == resourceName);

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

        var projectMetadataAnnotations = resource.Annotations
            .Where(a => a.GetType().GetInterfaces().Any(i => i.Name == "IProjectMetadata"))
            .ToList();
        foreach (var anno in projectMetadataAnnotations)
        {
            resource.Annotations.Remove(anno);
        }

        builder.CreateResourceBuilder(resource).WithDacpac(dacpacPath);
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        Playwright?.Dispose();
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
