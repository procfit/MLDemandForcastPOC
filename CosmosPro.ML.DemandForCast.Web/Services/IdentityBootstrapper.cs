using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Identity;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Cria os papéis e o PowerUser inicial no startup. Idempotente: se o usuário já
/// existe, <b>não</b> mexe nele — senão uma senha trocada pelo administrador voltaria
/// ao valor do parâmetro a cada F5.
/// </summary>
internal sealed class IdentityBootstrapper(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<IdentityBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var email = config["PowerUser:Email"];
        var senha = config["PowerUser:Password"];

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException(
                "PowerUser:Email não configurado. Defina o parâmetro 'poweruser-email' no AppHost.");
        }

        // Falha ruidosamente em vez de criar administrador com senha default: esta
        // aplicação vai receber dado comercial de redes reais.
        if (string.IsNullOrWhiteSpace(senha))
        {
            throw new InvalidOperationException(
                "PowerUser:Password não configurado. Registre com: dotnet user-secrets " +
                "--project CosmosPro.ML.DemandForCast.AppHost set Parameters:poweruser-password '<senha>'");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();

        foreach (var papel in Papeis.Todos)
        {
            if (!await roleManager.RoleExistsAsync(papel))
            {
                var r = await roleManager.CreateAsync(new IdentityRole<Guid>(papel));
                if (!r.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Falha ao criar papel '{papel}': {Descrever(r)}");
                }
                logger.LogInformation("Papel {Papel} criado.", papel);
            }
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            logger.LogInformation("PowerUser {Email} já existe — bootstrap não alterou nada.", email);
            return;
        }

        var poweruser = new Usuario
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            NomeCompleto = "Administrador",
            RedeId = null, // PowerUser é global
            Ativo = true,
            CriadoEm = DateTimeOffset.UtcNow,
        };

        var criado = await userManager.CreateAsync(poweruser, senha);
        if (!criado.Succeeded)
        {
            throw new InvalidOperationException($"Falha ao criar PowerUser: {Descrever(criado)}");
        }

        var papelAtribuido = await userManager.AddToRoleAsync(poweruser, Papeis.PowerUser);
        if (!papelAtribuido.Succeeded)
        {
            throw new InvalidOperationException(
                $"PowerUser criado mas sem papel: {Descrever(papelAtribuido)}");
        }

        logger.LogInformation("PowerUser {Email} criado.", email);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static string Descrever(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => $"{e.Code}: {e.Description}"));
}
