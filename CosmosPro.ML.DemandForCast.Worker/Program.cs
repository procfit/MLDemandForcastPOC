using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Worker;
using CosmosPro.ML.DemandForCast.Worker.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Purchasing;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;
using CosmosPro.ML.DemandForCast.Worker.Training;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Engine DB (EF Core) — para ler/atualizar CargasStage.
builder.AddSqlServerDbContext<EngineDbContext>("engine");

// Stage DB (SqlConnection puro) — para DELETE + SqlBulkCopy.
// `Microsoft.Data.SqlClient` direto, sem EF, porque BULK INSERT é off-EF.
builder.AddSqlServerClient(connectionName: "Stage");

// MinIO client para baixar o ZIP da carga.
builder.AddMinioClient("minio");

builder.Services.AddSingleton<CargaProcessor>();
builder.Services.AddHostedService<ImportWorker>();

// Treino do engine de previsão: processador + loop de polling próprio (corre em
// paralelo ao ImportWorker, mesma fila-pattern sobre engine.TreinoJobs).
builder.Services.AddSingleton<TreinoProcessor>();
builder.Services.AddHostedService<TreinoWorker>();

// Simulação de compra (F8): mesma fila-pattern sobre engine.SimulacoesCompra.
// Replay das políticas eMax/eSeg vs ROP+forecast com KPIs comparativos.
builder.Services.AddScoped<SimulacaoProcessor>();
builder.Services.AddHostedService<SimulacaoWorker>();

// Comparação contra o ERP (F13): mesma fila-pattern sobre engine.ComparacoesPbs.
// Roda as três camadas (previsão, decisão, intervenção humana) sobre a população
// que o PBS avaliou.
builder.Services.AddScoped<ComparacaoProcessor>();
builder.Services.AddHostedService<ComparacaoWorker>();

// Orquestração das sessões de comparação (F14): polling sobre engine.ComparacaoSessoes.
// Não processa fase nenhuma — observa o job da fase corrente e cria o da seguinte, o que
// mantém os três workers acima sem saber que sessões existem. A exceção é o fim da última
// fase: ali ele materializa o resultado, porque o Stage que a comparação mediu não
// sobrevive ao próximo import.
builder.Services.AddScoped<SessaoResultadoMaterializador>();
builder.Services.AddHostedService<SessaoWorker>();

var host = builder.Build();
await host.RunAsync();
