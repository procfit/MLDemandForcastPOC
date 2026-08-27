using System.Security.Claims;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
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

    /// <summary>
    /// O claim de escolha é a única coisa que separa o PowerUser da primeira rede ativa.
    /// Se um usuário operacional puder forjá-lo (cookie roubado, POST montado à mão), ele
    /// sai do próprio inquilino — é o vazamento que F11 existe para fechar.
    /// </summary>
    [Fact]
    public async Task Claim_de_escolha_e_ignorado_para_usuario_de_rede()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, RedeDoUsuario));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: false, redeNoClaim: OutraRede);

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
    public async Task PowerUser_sem_escolha_cai_na_primeira_rede_ativa()
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
        (await ctx.GetRedeIdAtualAsync()).Should().Be(RedeDoUsuario);
    }

    /// <summary>
    /// A regressão que motivou o claim: a escolha do PowerUser tinha de sobreviver ao
    /// recarregamento que a própria troca dispara, e um campo neste objeto (scoped = por
    /// circuito) morria junto com o circuito.
    /// </summary>
    [Fact]
    public async Task PowerUser_recebe_a_rede_gravada_no_claim_de_escolha()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, redeId: null));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: true, redeNoClaim: OutraRede);

        (await ctx.GetRedeIdAtualAsync()).Should().Be(OutraRede,
            "a escolha vive no cookie e vale para todo circuito criado depois dela");
    }

    /// <summary>
    /// O claim vive até 8h; a rede pode ser desativada nesse intervalo. Confiar nele sem
    /// reconferir deixaria o escopo ativo apontando para uma rede que a UI recusa.
    /// </summary>
    [Fact]
    public async Task Claim_apontando_para_rede_inativa_cai_na_primeira_ativa()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = false });
            db.Users.Add(NovoUsuario(userId, redeId: null));
            await db.SaveChangesAsync();
        });

        var ctx = Criar(sp, userId, ehPowerUser: true, redeNoClaim: OutraRede);

        (await ctx.GetRedeIdAtualAsync()).Should().Be(RedeDoUsuario);
    }

    [Fact]
    public async Task PodeAtivarAsync_recusa_quem_nao_e_PowerUser()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, RedeDoUsuario));
            await db.SaveChangesAsync();
        });

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<EngineDbContext>();

        (await RedeContext.PodeAtivarAsync(db, Principal(userId, ehPowerUser: false), OutraRede,
            TestContext.Current.CancellationToken))
            .Should().BeFalse("o endpoint de troca não pode ativar rede para usuário operacional");
    }

    [Fact]
    public async Task PodeAtivarAsync_recusa_rede_inativa_ou_inexistente()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = false });
            db.Users.Add(NovoUsuario(userId, redeId: null));
            await db.SaveChangesAsync();
        });

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<EngineDbContext>();
        var power = Principal(userId, ehPowerUser: true);
        var ct = TestContext.Current.CancellationToken;

        (await RedeContext.PodeAtivarAsync(db, power, RedeDoUsuario, ct)).Should().BeTrue();
        (await RedeContext.PodeAtivarAsync(db, power, OutraRede, ct)).Should().BeFalse("rede inativa");
        (await RedeContext.PodeAtivarAsync(db, power, 12345, ct)).Should().BeFalse("rede inexistente");
    }

    [Fact]
    public async Task Anonimo_lanca()
    {
        await using var sp = Construir(seed: _ => Task.CompletedTask);
        var ctx = new RedeContext(new AuthProviderFalso(new ClaimsPrincipal(new ClaimsIdentity())),
                                 new HttpContextAccessor(),
                                 sp.GetRequiredService<IServiceScopeFactory>());

        var act = async () => await ctx.EhPowerUserAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// <b>Regressão de um 500 em produção.</b> O download do ZIP da sessão é um endpoint HTTP
    /// comum, não um componente Razor, e ali o <see cref="AuthenticationStateProvider"/> do
    /// Blazor Server lança. O escopo tem de sair do <see cref="HttpContext"/> — sem deixar de
    /// ser derivado do <b>usuário autenticado</b>, que é a invariante de F11.
    /// </summary>
    [Fact]
    public async Task Fora_de_componente_razor_o_escopo_vem_do_http_context()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Users.Add(NovoUsuario(userId, RedeDoUsuario));
            await db.SaveChangesAsync();
        });

        var ctx = CriarForaDeComponente(sp, userId, ehPowerUser: false);

        (await ctx.GetRedeIdAtualAsync()).Should().Be(RedeDoUsuario);
        (await ctx.GetUsuarioIdAtualAsync()).Should().Be(userId);
    }

    /// <summary>
    /// O mesmo caminho para o <c>PowerUser</c>, que é quem de fato usa a tela: o escopo sai do
    /// claim de rede escolhida, e não da primeira rede ativa. Sem esta asserção, um PowerUser
    /// olhando a Rede Retiro baixaria o envio de outro inquilino.
    /// </summary>
    [Fact]
    public async Task Fora_de_componente_razor_o_poweruser_mantem_a_rede_escolhida()
    {
        var userId = Guid.CreateVersion7();
        await using var sp = Construir(seed: async db =>
        {
            db.Redes.Add(new Rede { Id = RedeDoUsuario, Nome = "A", Slug = "a", Ativo = true });
            db.Redes.Add(new Rede { Id = OutraRede, Nome = "B", Slug = "b", Ativo = true });
            db.Users.Add(NovoUsuario(userId, null));
            await db.SaveChangesAsync();
        });

        var ctx = CriarForaDeComponente(sp, userId, ehPowerUser: true, redeNoClaim: OutraRede);

        (await ctx.GetRedeIdAtualAsync()).Should().Be(OutraRede,
            "a rede escolhida na barra tem de valer no download, senão ele baixaria o envio de outra rede");
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

    private static RedeContext Criar(
        IServiceProvider sp, Guid userId, bool ehPowerUser, int? redeNoClaim = null) =>
        new(new AuthProviderFalso(Principal(userId, ehPowerUser, redeNoClaim)),
            new HttpContextAccessor(),
            sp.GetRequiredService<IServiceScopeFactory>());

    /// <summary>
    /// Fora de um componente Razor — num endpoint HTTP comum, como o download do ZIP da
    /// sessão — o principal tem de vir do <see cref="HttpContext"/>, porque o
    /// <see cref="AuthenticationStateProvider"/> do Blazor Server <b>lança</b> nesse caminho.
    /// O acessor recebe o usuário e o provider é o que estoura se for consultado.
    /// </summary>
    private static RedeContext CriarForaDeComponente(
        IServiceProvider sp, Guid userId, bool ehPowerUser, int? redeNoClaim = null)
    {
        var acessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, ehPowerUser, redeNoClaim) },
        };
        return new RedeContext(new AuthProviderQueLanca(), acessor, sp.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>
    /// Reproduz o comportamento real do <c>ServerAuthenticationStateProvider</c> fora do
    /// escopo de um componente: ele não devolve anônimo, ele <b>lança</b>. Um duplo que
    /// devolvesse principal vazio deixaria o teste passar com o bug de volta.
    /// </summary>
    private sealed class AuthProviderQueLanca : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException(
                "Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor component.");
    }

    private static ClaimsPrincipal Principal(Guid userId, bool ehPowerUser, int? redeNoClaim = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (ehPowerUser)
        {
            claims.Add(new Claim(ClaimTypes.Role, Papeis.PowerUser));
        }

        if (redeNoClaim is { } rede)
        {
            claims.Add(new Claim(RedeContext.ClaimRedeSelecionada, rede.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private sealed class AuthProviderFalso(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
