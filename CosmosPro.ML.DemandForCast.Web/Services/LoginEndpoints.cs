using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Sign-in/sign-out como endpoints de formulário em vez de chamadas dentro de um
/// componente: <see cref="SignInManager{TUser}"/> precisa escrever o cookie na
/// resposta, o que não funciona depois que o render interativo começou.
/// </summary>
internal static class LoginEndpoints
{
    public static IEndpointRouteBuilder MapLoginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", EntrarAsync).DisableAntiforgery();
        app.MapPost("/api/auth/logout", SairAsync).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> EntrarAsync(
        [FromForm] string email,
        [FromForm] string senha,
        SignInManager<Usuario> signInManager,
        UserManager<Usuario> userManager,
        ILogger<Program> logger)
    {
        var usuario = await userManager.FindByEmailAsync(email ?? string.Empty);

        // Usuário desativado é barrado antes da senha: preferimos desativar a excluir
        // para preservar a autoria das cargas que ele disparou.
        if (usuario is null || !usuario.Ativo)
        {
            return Results.Redirect("/login?erro=1");
        }

        var r = await signInManager.PasswordSignInAsync(
            usuario, senha ?? string.Empty, isPersistent: true, lockoutOnFailure: true);

        if (r.IsLockedOut)
        {
            logger.LogWarning("Login bloqueado por tentativas: {Email}", email);
            return Results.Redirect("/login?erro=2");
        }

        if (!r.Succeeded)
        {
            return Results.Redirect("/login?erro=1");
        }

        logger.LogInformation("Login de {Email}.", email);
        return Results.Redirect("/");
    }

    private static async Task<IResult> SairAsync(SignInManager<Usuario> signInManager)
    {
        await signInManager.SignOutAsync();
        return Results.Redirect("/login");
    }
}
