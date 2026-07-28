using System.Security.Claims;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// O <see cref="IRedeContext"/> é o que impede um usuário de alcançar dado de outro
/// cliente. Os casos abaixo são a garantia disso — em especial o de que ele
/// <b>lança</b> em vez de assumir uma rede quando não há escopo resolvido.
/// </summary>
public sealed class RedeContextTests
{
    private const int RedeDoUsuario = 42;
    private const int OutraRede = 99;

    [Fact]
    public async Task Usuario_de_rede_recebe_a_rede_do_proprio_cadastro()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, RedeDoUsuario));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: false);

        (await ctx.GetRedeIdAtualAsync()).Should().Be(RedeDoUsuario);
        (await ctx.EhPowerUserAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Usuario_de_rede_nao_pode_acessar_outra_rede()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, RedeDoUsuario));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: false);

        (await ctx.PodeAcessarAsync(RedeDoUsuario)).Should().BeTrue();
        (await ctx.PodeAcessarAsync(OutraRede)).Should().BeFalse();
    }

    [Fact]
    public async Task Selecionar_rede_e_ignorado_para_usuario_de_rede()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, RedeDoUsuario));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: false);
        await ctx.SelecionarRedeAsync(OutraRede);

        (await ctx.GetRedeIdAtualAsync()).Should().Be(RedeDoUsuario,
            "o escopo de um usuário operacional vem do cadastro, não de escolha na tela");
    }

    [Fact]
    public async Task Usuario_de_rede_sem_vinculo_lanca_em_vez_de_assumir_rede()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Users.Add(NovoUsuario(userId, redeId: null));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: false);

        var act = async () => await ctx.GetRedeIdAtualAsync();
        await act.Should().ThrowAsync<InvalidOperationException>(
            "default silencioso aqui seria vazamento de dado entre clientes");
    }

    [Fact]
    public async Task PowerUser_acessa_qualquer_rede_e_pode_trocar()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, redeId: null));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: true);

        (await ctx.EhPowerUserAsync()).Should().BeTrue();
        (await ctx.PodeAcessarAsync(OutraRede)).Should().BeTrue();

        // Sem seleção explícita cai na primeira rede ativa.
        (await ctx.GetRedeIdAtualAsync()).Should().Be(RedeDoUsuario);

        await ctx.SelecionarRedeAsync(OutraRede);
        (await ctx.GetRedeIdAtualAsync()).Should().Be(OutraRede);
    }

    [Fact]
    public async Task PowerUser_nao_pode_selecionar_rede_inativa()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = false });
            db.Users.Add(NovoUsuario(userId, redeId: null));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: true);

        var act = async () => await ctx.SelecionarRedeAsync(OutraRede);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Anonimo_lanca()
    {
        await using var sp = Construir(seed: _ => Task.CompletedTask);
        var ctx = new RedeContext(new AuthProviderFalso(new ClaimsPrincipal(new ClaimsIdentity())),
                                 sp.GetRequiredService<IServiceScopeFactory>());

        var act = async () => await ctx.EhPowerUserAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- infraestrutura dos testes ----

    private static Usuario NovoUsuario(Guid id, int? redeId) => new()
    {
        Id = id,
        UserName = $"u{id:N}@test",
        Email = $"u{id:N}@test",
        NomeCompleto = "Teste",
        RedeId = redeId,
        Ativo = true,
        CriadoEm = DateTimeOffset.UnixEpoch,
    };

    private static ServiceProvider Construir(Func<EngineDbContext, Task> seed)
    {
        // InMemory: o objetivo é a lógica de escopo, não o SQL gerado.
        var nomeBanco = Guid.CreateVersion7().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<EngineDbContext>(o => o.UseInMemoryDatabase(nomeBanco));
        var sp = services.BuildServiceProvider();

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<EngineDbContext>();
        seed(db).GetAwaiter().GetResult();

        return sp;
    }

    private static RedeContext Criar(IServiceProvider sp, Guid userId, bool ehPowerUser)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (ehPowerUser)
        {
            claims.Add(new Claim(ClaimTypes.Role, Papeis.PowerUser));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
        return new RedeContext(new AuthProviderFalso(principal),
                               sp.GetRequiredService<IServiceScopeFactory>());
    }

    private sealed class AuthProviderFalso(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
