using System.Globalization;
using System.Security.Claims;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Implementação do escopo de rede. Registrada como <b>scoped</b> — em Blazor Server
/// isso é <b>por circuito</b>.
/// <para>
/// A escolha do PowerUser <b>não</b> mora neste objeto: ela é um claim do cookie de
/// autenticação, gravado por <c>POST /api/auth/rede</c>. Um campo aqui morria junto com
/// o circuito, e trocar de rede exige justamente recarregar a página para as consultas
/// serem refeitas no novo escopo — a escolha era gravada e destruída na linha seguinte.
/// </para>
/// <para>
/// A rede do usuário operacional é lida do banco a cada resolução (com cache no
/// circuito) em vez de um claim fixo: se o PowerUser mudar a rede de alguém, não
/// queremos que o valor antigo continue valendo até o próximo login. Pela mesma razão o
/// claim de escolha é reconferido contra o banco antes de virar escopo.
/// </para>
/// </summary>
internal sealed class RedeContext(
    AuthenticationStateProvider authProvider,
    IHttpContextAccessor httpContextAccessor,
    IServiceScopeFactory scopeFactory) : IRedeContext
{
    /// <summary>
    /// Claim que carrega a rede escolhida pelo PowerUser dentro do cookie de
    /// autenticação. Nome curto porque todo claim engorda o cookie de cada requisição.
    /// </summary>
    internal const string ClaimRedeSelecionada = "rede_sel";

    private int? _redeDoPowerUser;
    private Resolucao? _resolucao;

    public async Task<int> GetRedeIdAtualAsync()
    {
        var resolucao = await ResolverAsync();

        if (!resolucao.EhPower)
        {
            return resolucao.RedeDoUsuario
                ?? throw new InvalidOperationException(
                    "Usuário sem rede associada. Um usuário operacional precisa estar " +
                    "vinculado a uma rede — peça ao administrador para corrigir o cadastro.");
        }

        if (_redeDoPowerUser is { } jaResolvida)
        {
            return jaResolvida;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        // O claim vive enquanto o cookie viver (8h); reconferir é o que impede uma rede
        // desativada nesse intervalo de continuar sendo o escopo ativo.
        if (resolucao.RedeNoClaim is { } escolhida
            && await PodeAtivarAsync(db, resolucao.Principal, escolhida))
        {
            _redeDoPowerUser = escolhida;
            return escolhida;
        }

        // PowerUser sem escolha válida: cai na primeira rede ativa, para a UI ter algo a
        // mostrar no primeiro acesso.
        var primeira = await db.Redes.AsNoTracking()
            .Where(r => r.Ativo)
            .OrderBy(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync();

        _redeDoPowerUser = primeira
            ?? throw new InvalidOperationException(
                "Não existe nenhuma rede ativa cadastrada. Cadastre uma em /admin/redes.");

        return _redeDoPowerUser.Value;
    }

    public async Task<Guid> GetUsuarioIdAtualAsync() =>
        (await ResolverAsync()).UsuarioId
        ?? throw new InvalidOperationException("Nenhum usuário autenticado no contexto.");

    public async Task<bool> EhPowerUserAsync() => (await ResolverAsync()).EhPower;

    public async Task<bool> PodeAcessarAsync(int redeId)
    {
        var resolucao = await ResolverAsync();
        return resolucao.EhPower || resolucao.RedeDoUsuario == redeId;
    }

    /// <summary>
    /// Se <paramref name="redeId"/> pode se tornar o escopo ativo de
    /// <paramref name="usuario"/>.
    /// <para>
    /// Autoridade <b>única</b> da regra: quem grava o claim (o endpoint de troca) e quem
    /// o lê de volta passam por aqui, para nenhum dos dois aceitar o que o outro recusa.
    /// Usuário sem o papel <c>PowerUser</c> nunca passa — o escopo dele vem do cadastro,
    /// e honrar uma escolha aqui seria a escalada entre inquilinos que F11 fecha.
    /// </para>
    /// </summary>
    internal static async Task<bool> PodeAtivarAsync(
        EngineDbContext db,
        ClaimsPrincipal usuario,
        int redeId,
        CancellationToken ct = default) =>
        usuario.IsInRole(Papeis.PowerUser)
        && await db.Redes.AsNoTracking().AnyAsync(r => r.Id == redeId && r.Ativo, ct);

    private async Task<Resolucao> ResolverAsync()
    {
        if (_resolucao is { } cache)
        {
            return cache;
        }

        // Duas fontes para o MESMO principal, e a ordem importa. Fora de um componente
        // Razor — num endpoint HTTP comum, como o download do ZIP da sessão — o
        // AuthenticationStateProvider do Blazor Server **lança**: ele exige o escopo de DI
        // de um componente. Ali o HttpContext existe e é a fonte. Em circuito interativo é o
        // inverso: o HttpContext não vale (a requisição que o criou já terminou) e o provider
        // é quem sabe. Custou um 500 em produção, num caminho que os testes de API e o E2E
        // de renderização não tocavam.
        var principal = httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } doRequest
            ? doRequest
            : (await authProvider.GetAuthenticationStateAsync()).User;

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("Nenhum usuário autenticado no contexto.");
        }

        var ehPower = principal.IsInRole(Papeis.PowerUser);

        Guid? usuarioId = null;
        var idTexto = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(idTexto, out var userIdParseado))
        {
            usuarioId = userIdParseado;
        }

        int? rede = null;
        if (!ehPower && usuarioId is { } userId)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
            rede = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.RedeId)
                .FirstOrDefaultAsync();
        }

        int? redeNoClaim = null;
        if (ehPower
            && int.TryParse(principal.FindFirstValue(ClaimRedeSelecionada),
                            CultureInfo.InvariantCulture, out var doClaim))
        {
            redeNoClaim = doClaim;
        }

        _resolucao = new Resolucao(ehPower, rede, usuarioId, redeNoClaim, principal);
        return _resolucao;
    }

    private sealed record Resolucao(
        bool EhPower,
        int? RedeDoUsuario,
        Guid? UsuarioId,
        int? RedeNoClaim,
        ClaimsPrincipal Principal);
}
