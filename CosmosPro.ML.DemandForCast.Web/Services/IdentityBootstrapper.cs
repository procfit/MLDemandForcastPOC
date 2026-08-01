using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Identity;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Cria os papéis e o PowerUser inicial no startup.
/// <para>
/// Fora de Development é idempotente: se o usuário existe, <b>não</b> mexe nele —
/// senão uma senha trocada pelo administrador voltaria ao valor do parâmetro a cada
/// reinício.
/// </para>
/// <para>
/// <b>Em Development a senha é reconciliada.</b> Motivo concreto: o banco
/// <c>engine</c> é persistente e compartilhado com os fixtures de teste, que também
/// semeiam um PowerUser. Sem reconciliar, o admin fica com a senha de quem o criou
/// primeiro e o login de debug falha com "E-mail ou senha inválidos" — sem nenhuma
/// pista de que a causa é essa.
/// </para>
/// </summary>
internal sealed class IdentityBootstrapper(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    IHostEnvironment ambiente,
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

        if (await userManager.FindByEmailAsync(email) is { } existente)
        {
            if (!ambiente.IsDevelopment())
            {
                logger.LogInformation("PowerUser {Email} já existe — bootstrap não alterou nada.", email);
                return;
            }

            await ReconciliarEmDesenvolvimentoAsync(existente, senha, userManager);
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

    /// <summary>
    /// Alinha senha, papel e situação do PowerUser ao que está configurado. Só roda em
    /// Development — em produção sobrescrever a senha do administrador a cada reinício
    /// seria um defeito, não uma conveniência.
    /// </summary>
    private async Task ReconciliarEmDesenvolvimentoAsync(
        Usuario existente, string senha, UserManager<Usuario> userManager)
    {
        if (!await userManager.CheckPasswordAsync(existente, senha))
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(existente);
            var reset = await userManager.ResetPasswordAsync(existente, token, senha);
            if (!reset.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Falha ao alinhar a senha do PowerUser em Development: {Descrever(reset)}");
            }
            logger.LogWarning(
                "Senha do PowerUser {Email} realinhada ao valor configurado (Development). " +
                "O usuário existia com outra senha — tipicamente criado por fixture de teste.",
                existente.Email);
        }

        // Usuário vindo de outra origem pode estar sem papel, inativo ou preso a uma
        // rede. Qualquer um dos três derruba o login ou a área administrativa.
        if (!await userManager.IsInRoleAsync(existente, Papeis.PowerUser))
        {
            var papel = await userManager.AddToRoleAsync(existente, Papeis.PowerUser);
            if (!papel.Succeeded)
            {
                throw new InvalidOperationException($"Falha ao atribuir papel: {Descrever(papel)}");
            }
            logger.LogWarning("PowerUser {Email} estava sem o papel — atribuído.", existente.Email);
        }

        if (!existente.Ativo || existente.RedeId is not null)
        {
            existente.Ativo = true;
            existente.RedeId = null;
            var upd = await userManager.UpdateAsync(existente);
            if (!upd.Succeeded)
            {
                throw new InvalidOperationException($"Falha ao reativar PowerUser: {Descrever(upd)}");
            }
            logger.LogWarning("PowerUser {Email} reativado / desvinculado de rede.", existente.Email);
        }
    }

    private static string Descrever(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => $"{e.Code}: {e.Description}"));
}
