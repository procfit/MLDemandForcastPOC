using System.Security.Claims;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Implementação do escopo de rede. Registrada como <b>scoped</b> — em Blazor Server
/// isso é por circuito, então a seleção do PowerUser sobrevive à navegação.
/// <para>
/// A rede do usuário é lida do banco a cada resolução (com cache no circuito) em vez
/// de um claim fixo: se o PowerUser mudar a rede de alguém, não queremos que o valor
/// antigo continue valendo até o próximo login.
/// </para>
/// </summary>
internal sealed class RedeContext(
    AuthenticationStateProvider authProvider,
    IServiceScopeFactory scopeFactory) : IRedeContext
{
    private int? _redeSelecionadaPowerUser;
    private (bool Resolvido, bool EhPower, int? RedeDoUsuario) _cache;

    public async Task<int> GetRedeIdAtualAsync()
    {
        var (ehPower, redeDoUsuario) = await ResolverAsync();

        if (!ehPower)
        {
            return redeDoUsuario
                ?? throw new InvalidOperationException(
                    "Usuário sem rede associada. Um usuário operacional precisa estar " +
                    "vinculado a uma rede — peça ao administrador para corrigir o cadastro.");
        }

        if (_redeSelecionadaPowerUser is { } escolhida)
        {
            return escolhida;
        }

        // PowerUser sem seleção: cai na primeira rede ativa, para a UI ter algo a
        // mostrar no primeiro acesso.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var primeira = await db.Redes.AsNoTracking()
            .Where(r => r.Ativo)
            .OrderBy(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync();

        _redeSelecionadaPowerUser = primeira
            ?? throw new InvalidOperationException(
                "Não existe nenhuma rede ativa cadastrada. Cadastre uma em /admin/redes.");

        return _redeSelecionadaPowerUser.Value;
    }

    public async Task<bool> EhPowerUserAsync() => (await ResolverAsync()).EhPower;

    public async Task<bool> PodeAcessarAsync(int redeId)
    {
        var (ehPower, redeDoUsuario) = await ResolverAsync();
        return ehPower || redeDoUsuario == redeId;
    }

    public async Task SelecionarRedeAsync(int redeId)
    {
        if (!await EhPowerUserAsync())
        {
            // Não lança: a UI não oferece o seletor para usuário de rede, e se algo
            // chamar por engano o escopo dele não deve mudar em silêncio nem quebrar.
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
        var existe = await db.Redes.AsNoTracking().AnyAsync(r => r.Id == redeId && r.Ativo);
        if (!existe)
        {
            throw new InvalidOperationException($"Rede {redeId} não existe ou está inativa.");
        }

        _redeSelecionadaPowerUser = redeId;
    }

    private async Task<(bool EhPower, int? RedeDoUsuario)> ResolverAsync()
    {
        if (_cache.Resolvido)
        {
            return (_cache.EhPower, _cache.RedeDoUsuario);
        }

        var state = await authProvider.GetAuthenticationStateAsync();
        var principal = state.User;

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("Nenhum usuário autenticado no contexto.");
        }

        var ehPower = principal.IsInRole(Papeis.PowerUser);

        int? rede = null;
        if (!ehPower)
        {
            var idTexto = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(idTexto, out var userId))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
                rede = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => u.RedeId)
                    .FirstOrDefaultAsync();
            }
        }

        _cache = (true, ehPower, rede);
        return (ehPower, rede);
    }
}
