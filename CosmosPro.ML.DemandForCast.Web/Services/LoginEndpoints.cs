using System.Globalization;
using System.Security.Claims;

using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Sign-in/sign-out e troca de rede como endpoints de formulário em vez de chamadas
/// dentro de um componente: todos os três escrevem no cookie de autenticação, o que não
/// funciona depois que o render interativo começou.
/// </summary>
internal static class LoginEndpoints
{
    public static IEndpointRouteBuilder MapLoginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", EntrarAsync).DisableAntiforgery();
        app.MapPost("/api/auth/logout", SairAsync).DisableAntiforgery();
        app.MapPost("/api/auth/rede", TrocarRedeAsync).DisableAntiforgery();
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

    /// <summary>
    /// Grava a rede escolhida pelo PowerUser como claim no cookie e devolve o usuário à
    /// página de onde ele veio, agora com o escopo novo.
    ///
    /// <para>
    /// Precisa ser endpoint por dois motivos que se somam: reemitir o cookie exige
    /// escrever na resposta HTTP, e trocar de escopo exige recarregar a página para as
    /// consultas serem refeitas. Guardar a escolha num campo do <c>RedeContext</c>
    /// (scoped = por circuito) e recarregar em seguida destruía a escolha junto com o
    /// circuito que a guardava — era o bug.
    /// </para>
    ///
    /// <para>
    /// A escolha morre com o cookie: sair e entrar de novo devolve o PowerUser à primeira
    /// rede ativa. Guardá-la no cadastro do usuário faria uma sessão nova herdar em
    /// silêncio o escopo de outra, que é o oposto do que um operador espera ao reentrar.
    /// </para>
    /// </summary>
    private static async Task<IResult> TrocarRedeAsync(
        HttpContext http,
        [FromForm] int redeId,
        [FromForm] string? retorno,
        EngineDbContext db,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var destino = DestinoLocal(retorno);

        var atual = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!atual.Succeeded || atual.Principal?.Identity is not ClaimsIdentity identidade)
        {
            return Results.Redirect("/login");
        }

        // Recusa sem trocar o escopo e sem 500: usuário de rede não tem seletor (e um
        // POST forjado por ele não pode escapar do cadastro dele), e uma rede pode ter
        // sido desativada entre o render da barra e este POST.
        if (!await RedeContext.PodeAtivarAsync(db, atual.Principal, redeId, ct))
        {
            logger.LogWarning("Troca para a rede {RedeId} recusada para {Usuario}.",
                redeId, atual.Principal.Identity?.Name);
            return Results.Redirect(destino);
        }

        var claims = identidade.Claims
            .Where(c => c.Type != RedeContext.ClaimRedeSelecionada)
            .Append(new Claim(RedeContext.ClaimRedeSelecionada,
                              redeId.ToString(CultureInfo.InvariantCulture)));

        var renovada = new ClaimsIdentity(
            claims,
            identidade.AuthenticationType,
            identidade.NameClaimType,
            identidade.RoleClaimType);

        var propriedades = atual.Properties ?? new AuthenticationProperties();
        // Zerados para o handler recalcular a validade a partir do ExpireTimeSpan;
        // reaproveitar os do cookie anterior congelaria a expiração no instante do login.
        propriedades.IssuedUtc = null;
        propriedades.ExpiresUtc = null;

        await http.SignInAsync(
            IdentityConstants.ApplicationScheme, new ClaimsPrincipal(renovada), propriedades);

        logger.LogInformation("Escopo trocado para a rede {RedeId} por {Usuario}.",
            redeId, atual.Principal.Identity?.Name);

        return Results.Redirect(destino);
    }

    /// <summary>
    /// Só caminho local volta: <c>retorno</c> chega em campo de formulário, e aceitar
    /// valor absoluto transformaria o endpoint em redirecionador aberto.
    /// </summary>
    private static string DestinoLocal(string? retorno) =>
        !string.IsNullOrWhiteSpace(retorno)
        && retorno.StartsWith('/')
        && !retorno.StartsWith("//", StringComparison.Ordinal)
        && !retorno.StartsWith("/\\", StringComparison.Ordinal)
            ? retorno
            : "/";
}
