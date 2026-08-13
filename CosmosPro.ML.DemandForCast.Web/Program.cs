using System.Globalization;
using System.Security.Claims;

using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Web;
using CosmosPro.ML.DemandForCast.Web.Components;
using CosmosPro.ML.DemandForCast.Web.Services;
using Microsoft.AspNetCore.Identity;
using Radzen;

// Esta tela mostra dinheiro para um comprador brasileiro (N2/N0/P0 sem cultura
// explícita seguem a cultura ambiente do host). Um host de container com
// globalização invariante formataria R$ 1.234,50 como R$ 1,234.50 — erro de três
// ordens de grandeza para quem lê. Fixado uma vez aqui em vez de em cada call site.
var culturaPadrao = new CultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentCulture = culturaPadrao;
CultureInfo.DefaultThreadCurrentUICulture = culturaPadrao;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOutputCache();

// Identity vive na Web, não na ApiService: é aqui que o cookie do navegador chega.
// A ApiService continua sem endpoint externo — ver comentário no Program.cs dela.
builder.AddSqlServerDbContext<EngineDbContext>("engine");

builder.Services
    .AddIdentity<Usuario, IdentityRole<Guid>>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<EngineDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.LogoutPath = "/logout";
    o.AccessDeniedPath = "/acesso-negado";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});

// O SecurityStampValidator revalida o cookie a cada 30 min e, ao revalidar, recria o
// principal a partir do banco — o que descartaria a rede escolhida pelo PowerUser no meio
// da sessão, sem erro nenhum e sem que a barra parecesse errada até a próxima consulta.
// Este gancho carrega o claim de escolha para o principal novo.
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
{
    o.OnRefreshingPrincipal = contexto =>
    {
        var escolha = contexto.CurrentPrincipal?.FindFirst(RedeContext.ClaimRedeSelecionada);

        // O papel é reconferido aqui, e não só no leitor, para o gancho não depender de
        // ninguém mais barrar um principal que ele mesmo montou: se o usuário perdeu o
        // papel PowerUser entre uma revalidação e outra (ex.: rebaixado pelo admin), o
        // claim não atravessa. A checagem de posse evita duplicar o claim caso ele um dia
        // passe a vir de uma claims factory — dois claims do mesmo tipo fariam
        // FindFirstValue escolher entre eles por ordem arbitrária.
        if (escolha is not null
            && contexto.NewPrincipal?.Identity is ClaimsIdentity identidade
            && contexto.NewPrincipal.IsInRole(Papeis.PowerUser)
            && identidade.FindFirst(RedeContext.ClaimRedeSelecionada) is null)
        {
            identidade.AddClaim(new Claim(escolha.Type, escolha.Value));
        }

        return Task.CompletedTask;
    };
});

// Faz o AuthenticationState fluir para os componentes (AuthorizeView/AuthorizeRouteView).
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

builder.Services.AddScoped<IRedeContext, RedeContext>();
builder.Services.AddHostedService<IdentityBootstrapper>();

// Radzen services: dialog, notification, tooltip, context-menu, theme.
builder.Services.AddRadzenComponents();

// `RemoveAllResilienceHandlers` é marcada como experimental e o compilador trata
// EXTEXP0001 como **erro**, não aviso — mesmo caso de ASPIRECOMPUTE003 no AppHost.
// Suprimido de propósito: a alternativa seria empilhar um segundo
// `AddStandardResilienceHandler` com retry zerado sobre o primeiro, e os dois passariam a
// valer. Se a API sair numa versão futura, o build quebra aqui e não em silêncio.
#pragma warning disable EXTEXP0001

// Os três clientes que transferem arquivo grande (este, ComparacoesApiClient e
// ExtratorApiClient) removem o handler de resiliência que o ServiceDefaults liga em todo
// HttpClient. São dois defeitos, e o primeiro é silencioso:
//
// 1. O `AddStandardResilienceHandler` impõe timeout **de 10s por tentativa** (e 30s no
//    total), *dentro* do Timeout do HttpClient. O `TimeSpan.FromMinutes(10)` abaixo nunca
//    valeu nada: qualquer upload que passasse de 10 segundos era cortado. Não vimos em
//    desenvolvimento porque loopback entrega dezenas de MB antes disso; no primeiro upload
//    real pela internet, morreu.
// 2. O retry reenvia a requisição, e `StreamContent` é de uso único — a segunda tentativa
//    falha com "The stream was already consumed", *substituindo* a exceção da primeira. O
//    erro que chega à tela descreve o retry, não a causa. Retry automático em POST de
//    upload é errado de todo modo: não é idempotente.
//
// Os clientes que só fazem GET de dados continuam com resiliência — ali o retry ajuda e
// nenhum corpo precisa ser reenviado.
builder.Services.AddHttpClient<ImportsApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
    client.Timeout = TimeSpan.FromMinutes(10);
}).RemoveAllResilienceHandlers();

builder.Services.AddHttpClient<StageApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
});

builder.Services.AddHttpClient<TrainingApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
});

builder.Services.AddHttpClient<PurchasingApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
});

builder.Services.AddHttpClient<ComparisonApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
});

// Sem resiliência pelo mesmo motivo do ImportsApiClient (ver comentário acima): este
// cliente sobe o ZIP da sessão de comparação, que é o caminho central da aplicação.
builder.Services.AddHttpClient<ComparacoesApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
    client.Timeout = TimeSpan.FromMinutes(10);
}).RemoveAllResilienceHandlers();

builder.Services.AddHttpClient<QuestionariosApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
});

builder.Services.AddHttpClient<ExtratorApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
    // O .exe tem dezenas de MB; o download precisa do mesmo teto generoso do upload,
    // não do default de 100s do HttpClient.
    client.Timeout = TimeSpan.FromMinutes(10);
}).RemoveAllResilienceHandlers();

#pragma warning restore EXTEXP0001

const long MaxUploadBytes = 500L * 1024 * 1024;
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUploadBytes;
    o.ValueLengthLimit = int.MaxValue;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseOutputCache();
app.MapStaticAssets();

app.MapLoginEndpoints();
app.MapExtratorEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
