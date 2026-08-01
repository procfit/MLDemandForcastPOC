# F11 — Usuários, redes e PowerUser: plano de implementação

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe arquivos `.md` de planejamento.
> Gravado a pedido explícito do usuário em 2026-07-28.

> **Para quem for executar:** as etapas usam checkbox (`- [ ]`) para acompanhamento.
> **Depende de:** [F10 — isolamento por rede](2026-07-28-f10-isolamento-por-rede.md) aplicado.

**Objetivo:** login por usuário, cada usuário amarrado a uma rede, um papel `PowerUser`
global que administra redes e usuários numa área que só ele vê. Elimina o `redeId`
falsificável que F10 deixou na superfície.

**Arquitetura:** ASP.NET Core Identity com stores EF no banco `engine`, cookie auth
hospedado na **Web**. A `apiservice` continua sem endpoint externo — a fronteira de
autorização é a Web, único processo que o navegador alcança. O `RedeId` efetivo nunca
vem de rota, query ou form para usuário de rede: vem do claim, resolvido server-side.

**Stack:** ASP.NET Core Identity + `Microsoft.AspNetCore.Identity.EntityFrameworkCore`,
cookie auth, Blazor SSR, Radzen, xUnit v3 + Playwright.

## Global Constraints

- Sem auto-cadastro. Usuário só existe se um `PowerUser` criar.
- Dois papéis: `PowerUser` (global, `RedeId` nulo) e `UsuarioRede` (escopado, `RedeId` obrigatório).
- Senha do `PowerUser` inicial **nunca** no repo — parâmetro Aspire secreto.
- Esconder item de menu é cosmético. O controle real é `[Authorize]` na página e no endpoint.
- Versão do pacote Identity a confirmar via Context7 na execução, alinhada ao EF Core já em uso.

---

## Task 1: Testes que falham primeiro

**Files:**
- Test: `tests/CosmosPro.ML.DemandForCast.Web.E2ETests/AuthorizationE2ETests.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/TopologiaTests.cs`

- [ ] **Step 1: cenários de autorização**

```csharp
[Fact]
public async Task Anonimo_e_redirecionado_para_login_ao_abrir_admin()
{
    await using var app = await AppFixture.StartAsync();
    var page = await app.NewPageAsync();

    await page.GotoAsync($"{app.WebBaseUrl}/admin/redes");

    page.Url.Should().Contain("/login", "área administrativa não pode ser anônima");
}

[Fact]
public async Task Usuario_de_rede_recebe_negacao_ao_abrir_admin()
{
    await using var app = await AppFixture.StartAsync();
    await app.SeedUsuarioRedeAsync("op@redea.test", "Senha!123", redeId: 1);
    var page = await app.NewPageAsync();
    await app.LoginAsync(page, "op@redea.test", "Senha!123");

    await page.GotoAsync($"{app.WebBaseUrl}/admin/redes");

    (await page.TextContentAsync("body"))!.Should().Contain("Acesso negado");
}
```

- [ ] **Step 2: invariante de topologia**

```csharp
[Fact]
public void ApiService_nao_pode_ter_endpoint_externo()
{
    var apiservice = AppHostModel.Resources.Single(r => r.Name == "apiservice");

    apiservice.Annotations.OfType<EndpointAnnotation>()
        .Should().NotContain(e => e.IsExternal,
            "a autorização mora na Web; expor a API externamente contorna o cookie");
}
```

- [ ] **Step 3: rodar e ver falhar** — os dois primeiros nem compilam (não há `/login`,
      `/admin/redes`, nem helpers no fixture). Isso conta como falha e é o ponto de partida.

---

## Task 2: Identity no `EngineDbContext`

**Files:**
- Create: `Engine/Entities/Usuario.cs`
- Modify: `Engine/EngineDbContext.cs`
- Modify: `Engine/CosmosPro.ML.DemandForCast.Engine.csproj`

**Interfaces:**
- Produz: `Usuario : IdentityUser<Guid>` com `int? RedeId`; constantes `Papeis.PowerUser` / `Papeis.UsuarioRede`.

- [ ] **Step 1: entidade e papéis**

```csharp
using Microsoft.AspNetCore.Identity;

namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Usuário do sistema. RedeId nulo ⇒ PowerUser (acesso global). RedeId
/// preenchido ⇒ usuário operacional, restrito à sua rede.
/// A coerência entre papel e RedeId é validada na aplicação: papéis vivem em
/// tabela de junção do Identity, fora do alcance de um CHECK constraint.
/// </summary>
public sealed class Usuario : IdentityUser<Guid>
{
    public int? RedeId { get; set; }
    public required string NomeCompleto { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTimeOffset CriadoEm { get; set; }
}

public static class Papeis
{
    public const string PowerUser = "PowerUser";
    public const string UsuarioRede = "UsuarioRede";
}
```

- [ ] **Step 2: trocar a base do contexto**

```csharp
public sealed class EngineDbContext(DbContextOptions<EngineDbContext> options)
    : IdentityDbContext<Usuario, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Rede> Redes => Set<Rede>();
    public DbSet<CargaStage> CargasStage => Set<CargaStage>();
    public DbSet<TreinoJob> TreinoJobs => Set<TreinoJob>();
    public DbSet<SimulacaoCompra> SimulacoesCompra => Set<SimulacaoCompra>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);   // OBRIGATÓRIO: sem isto o Identity não é configurado

        modelBuilder.Entity<Usuario>(b =>
        {
            b.Property(x => x.NomeCompleto).IsRequired().HasMaxLength(160);
            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.RedeId).HasDatabaseName("IX_Usuarios_RedeId");
        });

        // ... blocos existentes de Rede, CargaStage, TreinoJob, SimulacaoCompra ...
    }
}
```

O `base.OnModelCreating` hoje **não** é chamado. Esquecer isso faz as 7 tabelas do
Identity ficarem sem configuração.

- [ ] **Step 3:** `dotnet ef migrations add AddIdentity`. Nada de `IDesignTimeDbContextFactory`.

---

## Task 3: `IRedeContext` — o fim do `redeId` no request

**Files:**
- Create: `Web/Services/IRedeContext.cs`
- Create: `Web/Services/RedeContext.cs`
- Modify: `Web/Services/ImportsApiClient.cs`, `StageApiClient.cs`, `TrainingApiClient.cs`, `PurchasingApiClient.cs`
- Modify: `Web/Components/Pages/Imports.razor`, `Dados.razor`, `Treinamento.razor`, `SugestaoCompra.razor`
- Test: `tests/CosmosPro.ML.DemandForCast.Web.Tests/RedeContextTests.cs`

**Interfaces:**
- Produz: `IRedeContext` com `RedeIdAtual`, `EhPowerUser`, `PodeAcessar(int)`, `SelecionarRede(int)`.

- [ ] **Step 1: a abstração**

```csharp
namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Resolve em qual rede a operação atua. Para UsuarioRede vem do claim e é
/// imutável na sessão; para PowerUser vem de seleção explícita, que é legítima
/// porque ele é autorizado em todas. Nenhum caminho lê rede de rota/query.
/// </summary>
public interface IRedeContext
{
    int RedeIdAtual { get; }
    bool EhPowerUser { get; }
    bool PodeAcessar(int redeId);
    void SelecionarRede(int redeId);
}
```

`RedeIdAtual` **lança** se não houver rede resolvida, em vez de cair num default
silencioso — um default aqui seria exatamente o vazamento cross-tenant que F10 e F11
existem para impedir.

- [ ] **Step 2:** os 4 API clients injetam `IRedeContext` e preenchem o `redeId` a partir dele.
- [ ] **Step 3:** as 4 páginas param de receber/propagar `redeId`; o seletor do `MainLayout` fica visível só para `PowerUser`.
- [ ] **Step 4: testes unitários** — usuário de rede sempre devolve o claim; `PodeAcessar` de outra rede é falso; `RedeIdAtual` lança quando não resolvido; PowerUser acessa qualquer uma.

---

## Task 4: Cookie auth na Web + referência ao banco

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.AppHost/AppHost.cs`
- Modify: `Web/Program.cs`
- Create: `Web/Components/Pages/Login.razor`, `Web/Components/Pages/Logout.razor`

- [ ] **Step 1: AppHost**

```csharp
var powerUserEmail = builder.AddParameter("poweruser-email", secret: false, value: "admin@local");
var powerUserPassword = builder.AddParameter("poweruser-password", secret: true);

builder.AddProject<Projects.CosmosPro_ML_DemandForCast_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(engineDb)          // novo: Identity vive aqui
    .WithReference(apiService)
    .WithEnvironment("PowerUser__Email", powerUserEmail)
    .WithEnvironment("PowerUser__Password", powerUserPassword)
    // ...
```

A Web hoje **não** tem referência ao `engineDb` — só a `apiService`. Sem isso o Identity
não tem onde persistir.

- [ ] **Step 2: `Web/Program.cs`** — `AddDbContext<EngineDbContext>`,
      `AddIdentity<Usuario, IdentityRole<Guid>>` com EF stores, `AddAuthentication` /
      `AddAuthorization`, `UseAuthentication` / `UseAuthorization`, e
      `AddCascadingAuthenticationState` para o `AuthorizeView` funcionar em SSR.

- [ ] **Step 3: `Login.razor` / `Logout.razor`** — form de email/senha via `SignInManager`,
      sem link de cadastro.

---

## Task 5: Bootstrap do PowerUser

**Files:**
- Create: `Web/Services/IdentityBootstrapper.cs`

- [ ] **Step 1:** `IHostedService` idempotente que no startup cria os papéis
      `PowerUser` e `UsuarioRede` se ausentes, e o usuário do `PowerUser__Email` se
      ausente, com a senha do parâmetro. **Não** faz nada se o usuário já existe (não
      reescreve senha alterada).

- [ ] **Step 2:** falhar ruidosamente se `PowerUser__Password` estiver vazio. Senha default
      silenciosa em app que vai receber dado de duas redes reais não é aceitável.

```powershell
dotnet user-secrets --project CosmosPro.ML.DemandForCast.AppHost set Parameters:poweruser-password '<senha>'
```

Há precedente no repo de credencial fixa de debug para o MinIO (commit `c484403`). Para
senha de administrador a recomendação é não repetir o padrão.

---

## Task 6: Área administrativa

**Files:**
- Create: `Web/Components/Pages/Admin/Redes.razor`
- Create: `Web/Components/Pages/Admin/Usuarios.razor`
- Create: `Web/Components/Shared/AcessoNegado.razor`
- Modify: `Web/Components/Layout/NavMenu.razor`, `Web/Components/Routes.razor`

- [ ] **Step 1: `/admin/redes`** — `@attribute [Authorize(Roles = Papeis.PowerUser)]`.
      Grid Radzen com CRUD de `Rede` (Nome, Slug, CnpjRaiz, Ativo). Bloqueia exclusão de
      rede que já tenha `CargasStage` — desativa em vez de apagar, senão as FKs de F10 estouram.

- [ ] **Step 2: `/admin/usuarios`** — mesmo atributo. Criar usuário (email, nome, papel,
      rede), desativar, resetar senha. Valida a invariante: `UsuarioRede` exige rede;
      `PowerUser` força rede nula.

- [ ] **Step 3: `AcessoNegado.razor`** — mensagem em português, sem stack trace.

- [ ] **Step 4: `NavMenu.razor`** — seção "Administração" dentro de
      `<AuthorizeView Roles="@Papeis.PowerUser">`; resto da navegação dentro de
      `<AuthorizeView>` para o anônimo não ver menu de dados.

- [ ] **Step 5: `Routes.razor`** — `AuthorizeRouteView` com `NotAuthorized` apontando para
      `AcessoNegado`; `[Authorize]` nas páginas de dados.

---

## Task 7: Endpoints da API

**Files:**
- Modify: `ApiService/Program.cs` e endpoints

- [ ] **Step 1:** cada endpoint rejeita `redeId` inexistente ou de rede inativa, em vez de
      confiar cegamente. Custa uma consulta e transforma erro de wiring em 400 em vez de vazamento.
- [ ] **Step 2:** preencher `CargaStage.UsuarioId` com o id real (o campo já existe).
- [ ] **Step 3:** comentar no `Program.cs` o porquê da API não ter auth própria e a condição
      que sustenta isso (sem endpoint externo) — invariante não-óbvia, CLAUDE.md §3.

---

## Task 8: Verde

- [ ] **Step 1:** cenários da Task 1 em verde, mais login levando ao dashboard e PowerUser abrindo `/admin/redes`.
- [ ] **Step 2:** `Web.E2ETests` ganha `SeedUsuarioRedeAsync`, `SeedPowerUserAsync`, `LoginAsync`; cenários de import/treino passam a logar antes.
- [ ] **Step 3:** `dotnet test` verde nos 13 projetos.

---

## Task 9: Documentação

- [ ] **Step 1:** `README.md` §6 com F11 marcada.
- [ ] **Step 2:** `CLAUDE.md` §4 ganha as tabelas do Identity em `engine`.
- [ ] **Step 3:** `CLAUDE.md` §7 precisa de correção — o texto atual diz *"Posso adicionar
      autenticação? → POC, provavelmente não"*, e isso deixou de valer.

---

## O que este plano não faz

- **Não** coloca auth na `apiservice`. Se algum dia ela for publicada, o modelo cai — o teste da Task 1 falha se alguém adicionar o endpoint externo.
- **Não** tem recuperação de senha por email. PowerUser reseta manualmente.
- **Não** tem MFA.
- **Não** tem trilha de auditoria além do `UsuarioId` na carga.

## Ordem de execução

Task 1 (vermelho) → Task 2 (migration) → Task 3 (`IRedeContext`) → Task 4 (auth + AppHost)
→ Task 5 (bootstrap) → Task 6 (páginas) → Task 7 (API) → Task 8 (verde) → Task 9 (docs).
