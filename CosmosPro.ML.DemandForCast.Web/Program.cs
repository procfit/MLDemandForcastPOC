using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Web;
using CosmosPro.ML.DemandForCast.Web.Components;
using CosmosPro.ML.DemandForCast.Web.Services;
using Microsoft.AspNetCore.Identity;
using Radzen;

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

// Faz o AuthenticationState fluir para os componentes (AuthorizeView/AuthorizeRouteView).
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

builder.Services.AddScoped<IRedeContext, RedeContext>();
builder.Services.AddHostedService<IdentityBootstrapper>();

// Radzen services: dialog, notification, tooltip, context-menu, theme.
builder.Services.AddRadzenComponents();

builder.Services.AddHttpClient<ImportsApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
    client.Timeout = TimeSpan.FromMinutes(10);
});

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
