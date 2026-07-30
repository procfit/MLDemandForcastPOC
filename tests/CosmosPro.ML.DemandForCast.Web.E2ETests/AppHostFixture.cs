using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CosmosPro.ML.DemandForCast.Tests.Shared;
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
