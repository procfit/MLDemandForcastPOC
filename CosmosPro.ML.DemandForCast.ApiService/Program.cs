using CosmosPro.ML.DemandForCast.ApiService.Comparacoes;
using CosmosPro.ML.DemandForCast.ApiService.Extrator;
using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.ApiService.Mercado;
using CosmosPro.ML.DemandForCast.ApiService.Purchasing;
using CosmosPro.ML.DemandForCast.ApiService.Questionarios;
using CosmosPro.ML.DemandForCast.ApiService.Redes;
using CosmosPro.ML.DemandForCast.ApiService.Stage;
using CosmosPro.ML.DemandForCast.ApiService.Training;
using CosmosPro.ML.DemandForCast.Engine;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Engine DB (EF Core via Aspire client integration). Connection name "engine"
// vem do `AddDatabase("engine")` no AppHost — Aspire injeta automaticamente.
builder.AddSqlServerDbContext<EngineDbContext>("engine");

// MinIO (object storage). Connection name "minio" vem do AppHost.
builder.AddMinioClient("minio");

// Stage DB (SqlConnection puro, para leitura paginada das tabelas importadas).
// Connection name "Stage" vem do AppHost. Engine usa EF; aqui só lemos via SQL.
builder.AddSqlServerClient(connectionName: "Stage");

// Limite de tamanho de upload (ZIPs de import). 500 MB cobre dataset histórico
// típico de farma de médio porte. Kestrel + form options precisam alinhar.
const long MaxUploadBytes = 500L * 1024 * 1024;
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUploadBytes;
    o.ValueLengthLimit = int.MaxValue;
});

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => "CosmosPro ML DemandForCast — API service.");

app.MapRedesEndpoints();
app.MapImportsEndpoints();
app.MapMercadoEndpoints();
app.MapStageEndpoints();
app.MapTrainingEndpoints();
app.MapPurchasingEndpoints();
app.MapComparacoesEndpoints();
app.MapQuestionariosEndpoints();
app.MapComparisonEndpoints();
app.MapExtratorEndpoints();

app.MapDefaultEndpoints();

app.Run();
