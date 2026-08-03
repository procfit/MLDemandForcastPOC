using System.Data;

using Aspire.Hosting;
using Aspire.Hosting.Testing;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Tests.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Credenciais do usuário operacional (<see cref="Papeis.UsuarioRede"/>) semeado por
    /// <see cref="GarantirUsuarioRedeAsync"/> — usado pelos cenários que precisam de uma
    /// identidade sem o seletor de rede do PowerUser.
    /// <para>
    /// E-mail próprio, pela mesma razão de <see cref="PowerUserEmail"/>: o banco
    /// <c>engine</c> é persistente e compartilhado entre execuções, então reaproveitar um
    /// e-mail já usado por outro fixture (ou pelo admin de debug) faria os dois disputarem
    /// a senha do mesmo usuário.
    /// </para>
    /// </summary>
    public const string UsuarioRedeEmail = "e2e-usuario-rede@teste.local";
    public const string UsuarioRedeSenha = "TesteE2ERede!2026";

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

        App = await builder.BuildAsync();

        // Teto igual ao do fixture de integração, e pelo mesmo motivo: sem token, um AppHost
        // que não sobe deixa o passo do CI em silêncio até o teto do workflow, sem dizer qual
        // recurso ficou pelo caminho.
        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await App.StartAsync(startCts.Token);

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
    /// Garante uma rede ativa com o <paramref name="slug"/> informado e devolve o id dela.
    ///
    /// <para>
    /// Existe para o cenário de <b>troca de rede</b>, que precisa de dois inquilinos com dado
    /// distinguível. Os ids são atribuídos pela identidade da tabela, então nenhum deles
    /// concorre com a rede demo (id 1) pelo papel de "primeira rede ativa por id" — é ela que
    /// os outros semeadores daqui usam, e é para ela que o PowerUser cai sem escolha.
    /// </para>
    ///
    /// <para>
    /// Reativa em vez de recriar: o banco <c>engine</c> é persistente e o slug é único, então
    /// reexecutar o teste tem de reencontrar a mesma rede em lugar de estourar na constraint.
    /// </para>
    /// </summary>
    public async Task<int> GarantirRedeAtivaAsync(
        string slug, string nome, CancellationToken ct = default)
    {
        var connectionString = await App.GetConnectionStringAsync("engine", ct)
            ?? throw new InvalidOperationException("Recurso 'engine' sem connection string.");

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.Redes WHERE Slug = @slug)
                INSERT INTO dbo.Redes (Nome, Slug, Ativo, CriadoEm)
                VALUES (@nome, @slug, 1, @agora);
            ELSE
                UPDATE dbo.Redes SET Nome = @nome, Ativo = 1 WHERE Slug = @slug;

            SELECT Id FROM dbo.Redes WHERE Slug = @slug;
            """;
        cmd.Parameters.AddWithValue("@slug", slug);
        cmd.Parameters.AddWithValue("@nome", nome);
        cmd.Parameters.AddWithValue("@agora", DateTimeOffset.UtcNow);

        return (int?)await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException($"Rede '{slug}' não foi criada.");
    }

    /// <summary>
    /// Garante o usuário operacional <see cref="UsuarioRedeEmail"/>, vinculado a
    /// <paramref name="redeId"/> e no papel <see cref="Papeis.UsuarioRede"/>.
    ///
    /// <para>
    /// Passa por <c>UserManager</c>/<c>RoleManager</c> reais (contra a mesma connection
    /// string do recurso <c>engine</c>) em vez de <c>INSERT</c> direto: o hash de senha e
    /// as colunas normalizadas do Identity são detalhe de implementação do framework, e
    /// escrevê-las à mão aqui duplicaria — e poderia divergir de — o que
    /// <c>IdentityBootstrapper</c> já faz para o PowerUser.
    /// </para>
    ///
    /// <para>
    /// Idempotente e reconciliado a cada chamada — o banco é persistente, então reexecutar
    /// o teste precisa reencontrar o mesmo usuário em vez de esbarrar em e-mail duplicado, e
    /// precisa realinhar rede/senha/papel caso uma execução anterior tenha deixado o cadastro
    /// diferente do esperado.
    /// </para>
    /// </summary>
    public async Task GarantirUsuarioRedeAsync(int redeId, CancellationToken ct = default)
    {
        var connectionString = await App.GetConnectionStringAsync("engine", ct)
            ?? throw new InvalidOperationException("Recurso 'engine' sem connection string.");

        var services = new ServiceCollection();
        services.AddDbContext<EngineDbContext>(o => o.UseSqlServer(connectionString));
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddIdentityCore<Usuario>(o => o.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<EngineDbContext>();

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(Papeis.UsuarioRede))
        {
            await roleManager.CreateAsync(new IdentityRole<Guid>(Papeis.UsuarioRede));
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var usuario = await userManager.FindByEmailAsync(UsuarioRedeEmail);

        if (usuario is null)
        {
            usuario = new Usuario
            {
                Id = Guid.CreateVersion7(),
                UserName = UsuarioRedeEmail,
                Email = UsuarioRedeEmail,
                EmailConfirmed = true,
                NomeCompleto = "Usuario Operacional E2E",
                RedeId = redeId,
                Ativo = true,
                CriadoEm = DateTimeOffset.UtcNow,
            };

            var criado = await userManager.CreateAsync(usuario, UsuarioRedeSenha);
            if (!criado.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Falha ao criar usuário operacional E2E: {Descrever(criado)}");
            }
        }
        else
        {
            if (!await userManager.CheckPasswordAsync(usuario, UsuarioRedeSenha))
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(usuario);
                var reset = await userManager.ResetPasswordAsync(usuario, token, UsuarioRedeSenha);
                if (!reset.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Falha ao realinhar senha do usuário operacional E2E: {Descrever(reset)}");
                }
            }

            if (usuario.RedeId != redeId || !usuario.Ativo)
            {
                usuario.RedeId = redeId;
                usuario.Ativo = true;
                var atualizado = await userManager.UpdateAsync(usuario);
                if (!atualizado.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Falha ao realinhar cadastro do usuário operacional E2E: {Descrever(atualizado)}");
                }
            }
        }

        if (!await userManager.IsInRoleAsync(usuario, Papeis.UsuarioRede))
        {
            var papel = await userManager.AddToRoleAsync(usuario, Papeis.UsuarioRede);
            if (!papel.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Falha ao atribuir papel ao usuário operacional E2E: {Descrever(papel)}");
            }
        }

        if (await userManager.IsInRoleAsync(usuario, Papeis.PowerUser))
        {
            await userManager.RemoveFromRoleAsync(usuario, Papeis.PowerUser);
        }
    }

    private static string Descrever(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => $"{e.Code}: {e.Description}"));

    /// <summary>
    /// Semeia uma sessão de comparação <c>Concluida</c> numa rede <b>escolhida</b>, sem
    /// resultado nem detalhe por item.
    ///
    /// <para>
    /// Serve à listagem de <c>/</c>, que projeta apenas o cabeçalho de cada sessão (a
    /// projeção com <c>ResultadoJson</c> é a do detalhe). Para o cenário de troca de rede o que
    /// precisa existir é uma linha com nome reconhecível em cada inquilino — encher o
    /// resultado só somaria ruído. <c>Concluida</c> é terminal, então a linha não é reclamada
    /// por worker nenhum nem conta como sessão viva no bloqueio por rede.
    /// </para>
    /// </summary>
    public async Task<Guid> SemearSessaoNaRedeAsync(
        string nome, int redeId, CancellationToken ct = default)
    {
        var connectionString = await App.GetConnectionStringAsync("engine", ct)
            ?? throw new InvalidOperationException("Recurso 'engine' sem connection string.");

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using (var limpeza = conn.CreateCommand())
        {
            limpeza.CommandText = "DELETE FROM dbo.ComparacaoSessoes WHERE Nome = @nome;";
            limpeza.Parameters.AddWithValue("@nome", nome);
            await limpeza.ExecuteNonQueryAsync(ct);
        }

        var id = Guid.CreateVersion7();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO dbo.ComparacaoSessoes (Id, RedeId, Nome, Status, CriadoEm, AtualizadoEm)
                VALUES (@id, @redeId, @nome, 'Concluida', @agora, @agora);
                """;
            var agora = DateTimeOffset.UtcNow;
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@redeId", redeId);
            insert.Parameters.AddWithValue("@nome", nome);
            insert.Parameters.AddWithValue("@agora", agora);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return id;
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

    /// <summary>
    /// Semeia uma <b>sessão de comparação já concluída</b>, com os agregados da manchete e o
    /// detalhe por item materializados, na rede que a página vai enxergar.
    ///
    /// <para>
    /// Mesma razão de <see cref="SemearComparacaoConcluidaAsync"/>: o caminho legítimo passa
    /// por importar um ZIP, treinar um modelo e esperar o Worker — dezenas de minutos —, e o
    /// que estes testes exercitam é o <b>render</b> da manchete e da tabela. O
    /// <paramref name="resultadoJson"/> vem serializado dos tipos reais do Worker, para a tela
    /// ser lida contra o contrato de verdade.
    /// </para>
    ///
    /// <para>
    /// A sessão nasce em <c>Concluida</c>, que é terminal: ela não é reclamada pelo
    /// <c>SessaoWorker</c> nem conta como sessão viva no bloqueio por rede, então semeá-la não
    /// trava os outros cenários E2E da mesma rede. As linhas de execuções anteriores com o
    /// mesmo <paramref name="nome"/> são removidas antes — o banco é persistente e reexecutar
    /// o teste acumularia sessões idênticas na lista.
    /// </para>
    /// </summary>
    public async Task<Guid> SemearSessaoConcluidaAsync(
        string nome,
        long sugestaoId,
        DateTime sugestaoDataHora,
        byte tipoCalculo,
        int? skusSemCadastro,
        string resultadoJson,
        IReadOnlyList<ComparacaoSessaoItem> itens,
        CancellationToken ct = default)
    {
        var connectionString = await App.GetConnectionStringAsync("engine", ct)
            ?? throw new InvalidOperationException("Recurso 'engine' sem connection string.");

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Detalhe antes do pai: a FK é cascade no modelo, mas depender disso aqui deixaria a
        // limpeza calada se o comportamento mudar.
        await using (var limpezaItens = conn.CreateCommand())
        {
            limpezaItens.CommandText = """
                DELETE FROM dbo.ComparacaoSessaoItens
                WHERE SessaoId IN (SELECT Id FROM dbo.ComparacaoSessoes WHERE Nome = @nome);
                """;
            limpezaItens.Parameters.AddWithValue("@nome", nome);
            await limpezaItens.ExecuteNonQueryAsync(ct);
        }

        await using (var limpeza = conn.CreateCommand())
        {
            limpeza.CommandText = "DELETE FROM dbo.ComparacaoSessoes WHERE Nome = @nome;";
            limpeza.Parameters.AddWithValue("@nome", nome);
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
                INSERT INTO dbo.ComparacaoSessoes
                    (Id, RedeId, Nome, Status, CriadoEm, AtualizadoEm, SugestaoId, SugestaoDescricao,
                     SugestaoDataHora, SugestaoTipoCalculo, SkusSemCadastro, ResultadoJson)
                VALUES
                    (@id, @redeId, @nome, 'Concluida', @agora, @agora, @sugestaoId, @descricao,
                     @dataHora, @tipoCalculo, @skusSemCadastro, @resultado);
                """;
            var agora = DateTimeOffset.UtcNow;
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@redeId", redeId);
            insert.Parameters.AddWithValue("@nome", nome);
            insert.Parameters.AddWithValue("@agora", agora);
            insert.Parameters.AddWithValue("@sugestaoId", sugestaoId);
            insert.Parameters.AddWithValue("@descricao", "Sugestao semeada pelo E2E");
            insert.Parameters.Add("@dataHora", SqlDbType.DateTime2).Value = sugestaoDataHora;
            insert.Parameters.AddWithValue("@tipoCalculo", tipoCalculo);
            insert.Parameters.AddWithValue("@skusSemCadastro", (object?)skusSemCadastro ?? DBNull.Value);
            insert.Parameters.AddWithValue("@resultado", resultadoJson);
            await insert.ExecuteNonQueryAsync(ct);
        }

        foreach (var item in itens)
        {
            await using var insertItem = conn.CreateCommand();
            insertItem.CommandText = """
                INSERT INTO dbo.ComparacaoSessaoItens
                    (SessaoId, LojaId, Sku, NomeProduto, Curva, CompraSugeridaPbs, CompraSugeridaMl,
                     VendidoNaJanela, DemandaDiaPbs, DemandaDiaMl, DemandaDiaReal,
                     SobraPbsUnidades, SobraMlUnidades, SobraPbsValor, SobraMlValor, JanelaAlemDoHistorico)
                VALUES
                    (@sessaoId, @lojaId, @sku, @nomeProduto, @curva, @compraPbs, @compraMl,
                     @vendido, @demandaPbs, @demandaMl, @demandaReal,
                     @sobraPbsUn, @sobraMlUn, @sobraPbsVl, @sobraMlVl, @alemDoHistorico);
                """;
            insertItem.Parameters.AddWithValue("@sessaoId", id);
            insertItem.Parameters.AddWithValue("@lojaId", item.LojaId);
            insertItem.Parameters.AddWithValue("@sku", item.Sku);
            insertItem.Parameters.AddWithValue("@nomeProduto", (object?)item.NomeProduto ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@curva", (object?)item.Curva ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@compraPbs", item.CompraSugeridaPbs);
            insertItem.Parameters.AddWithValue("@compraMl", (object?)item.CompraSugeridaMl ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@vendido", item.VendidoNaJanela);
            insertItem.Parameters.AddWithValue("@demandaPbs", item.DemandaDiaPbs);
            insertItem.Parameters.AddWithValue("@demandaMl", (object?)item.DemandaDiaMl ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@demandaReal", (object?)item.DemandaDiaReal ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@sobraPbsUn", item.SobraPbsUnidades);
            insertItem.Parameters.AddWithValue("@sobraMlUn", (object?)item.SobraMlUnidades ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@sobraPbsVl", (object?)item.SobraPbsValor ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@sobraMlVl", (object?)item.SobraMlValor ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("@alemDoHistorico", item.JanelaAlemDoHistorico);
            await insertItem.ExecuteNonQueryAsync(ct);
        }

        return id;
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
