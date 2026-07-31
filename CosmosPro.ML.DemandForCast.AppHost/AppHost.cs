var builder = DistributedApplication.CreateBuilder(args);

// --- Destino de publicação ---------------------------------------------------

// Alvo de deploy: Docker Compose. Com este recurso presente, todo resource do
// modelo é publicado como serviço de compose — `aspire publish` gera
// docker-compose.yaml + .env, e `aspire do push` constrói e empurra as imagens.
//
// Existe para o compose sair do MESMO modelo que roda no F5, em vez de um YAML
// paralelo mantido à mão: um arquivo separado desatualizaria na primeira vez que
// alguém acrescentasse um recurso aqui e esquecesse de espelhar lá.
builder.AddDockerComposeEnvironment("compose");

// Registry onde as imagens deste repositório são publicadas por `aspire do push`.
// Endpoint e repositório vêm de configuração (`REGISTRY_ENDPOINT` /
// `REGISTRY_REPOSITORY`) em vez de literais: o pipeline do GitHub Actions preenche
// com ghcr.io + o próprio repositório, e um valor cravado aqui obrigaria um fork a
// editar código para publicar no registry dele.
var registryEndpoint = builder.AddParameterFromConfiguration("registryEndpoint", "REGISTRY_ENDPOINT");
var registryRepository = builder.AddParameterFromConfiguration("registryRepository", "REGISTRY_REPOSITORY");

// ASPIRECOMPUTE003: `AddContainerRegistry` e `WithContainerRegistry` são
// experimentais no Aspire 13.4 e o compilador trata o uso como **erro**, não aviso.
// Suprimido porque são as únicas APIs que associam imagem a registry para o
// `aspire do push`. A supressão vale daqui até o fim do modelo, porque as quatro
// chamadas de `WithContainerRegistry` ficam espalhadas pelos recursos abaixo.
// Revisar a cada bump do Aspire — é quando elas devem sair de experimental (ou
// mudar de forma).
#pragma warning disable ASPIRECOMPUTE003
var registry = builder.AddContainerRegistry("ghcr", registryEndpoint, registryRepository);

// --- Parameters --------------------------------------------------------------

// MinIO de debug do POC: credenciais fixas e não-secretas de propósito, para o
// AppHost subir em qualquer máquina sem exigir user-secrets. Não reaproveitar
// fora do ambiente local.
var minioAccessKey = builder.AddParameter("minio-access-key", secret: false, value: "minioadmin");
var minioSecretKey = builder.AddParameter("minio-secret-key", secret: false, value: "minioadmin");

// --- Data stores (persistentes entre F5s) ------------------------------------

// DbGate (UI web de inspeção) é compartilhado entre SQL Server e ClickHouse.
// O volume/lifetime do container DbGate é configurado aqui (na primeira
// chamada `.WithDbGate`); a chamada subsequente no ClickHouse reusa o mesmo
// recurso DbGate (AddDbGate é idempotente).
var sqlServer = builder.AddSqlServer("sql")
                       .WithLifetime(ContainerLifetime.Persistent)
                       .WithDataVolume()
                       .WithDbGate(cfg => cfg.WithDataVolume().WithLifetime(ContainerLifetime.Persistent));

// Stage: staging area dos dados importados via UI (vendas, estoque, compras,
// promoções, mestres, IQVIA). O engine só lê deste banco; nunca escreve.
// Schema gerenciado declarativamente via SQL Server Project / DACPAC.
var stageDb = sqlServer.AddDatabase("Stage");

// engine: metadados próprios do engine (cargas, experimentos, runs, modelos,
// métricas). Schema será gerenciado via EF Core migrations quando o projeto
// Engine for criado.
var engineDb = sqlServer.AddDatabase("engine");

// `.WithDbGate()` aqui é um helper local (ver ClickHouseDbGateExtensions.cs)
// que cobre a lacuna do Aspire.Hosting.ClickHouse, ainda sem suporte nativo.
var clickhouse = builder.AddClickHouse("clickhouse")
                        .WithLifetime(ContainerLifetime.Persistent)
                        .WithDataVolume()
                        .WithDbGate();

// vendas-olap: histórico denso de vendas para varredura analítica.
// Nome do recurso Aspire usa hífen; nome do schema no ClickHouse é "vendas_olap".
var vendasOlapDb = clickhouse.AddDatabase("vendas-olap", "vendas_olap");

// MinIO: object storage S3-compatible para armazenar ZIPs de import (CSVs
// de vendas, estoque, etc.). Persistido em volume; credenciais fixas via
// ParameterResource (ver bloco de parameters acima).
var minio = builder.AddMinioContainer("minio", minioAccessKey, minioSecretKey)
                   .WithLifetime(ContainerLifetime.Persistent)
                   .WithDataVolume();

// --- Schema deployment -------------------------------------------------------

// SQL Server Project compilado em DACPAC e aplicado ao banco "Stage" a cada
// F5. `WithReference` já registra OnResourceReady internamente — adicionar
// `WaitFor` redundante prende o recurso em Waiting (descoberto em F1 debug).
var stageSchema = builder.AddSqlProject<Projects.CosmosPro_ML_DemandForCast_Database>("stage-schema")
                         .WithReference(stageDb);

// ClickHouse não tem DACPAC. O projeto OlapSchema aplica scripts versionados
// idempotentes ao banco vendas-olap (controle via tabela __schema_migrations).
var olapSchema = builder.AddProject<Projects.CosmosPro_ML_DemandForCast_OlapSchema>("vendas-olap-schema")
                        .WithReference(vendasOlapDb)
                        .WaitFor(vendasOlapDb)
                        .WithParentRelationship(vendasOlapDb.Resource)
                        .WithContainerRegistry(registry);

// --- Services ----------------------------------------------------------------

var apiService = builder.AddProject<Projects.CosmosPro_ML_DemandForCast_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(stageDb)
    .WithReference(engineDb)
    .WithReference(vendasOlapDb)
    .WithReference(minio)
    .WaitFor(stageDb)
    .WaitFor(engineDb)
    .WaitFor(vendasOlapDb)
    .WaitFor(minio)
    .WaitForCompletion(stageSchema)
    .WaitForCompletion(olapSchema)
    .WithContainerRegistry(registry);

// EF Core migrations para o banco "engine" — runner one-shot orquestrado pelo
// Aspire. Usa o DbContext registrado no apiservice (`AddSqlServerDbContext<EngineDbContext>`).
// Pacote: Aspire.Hosting.EntityFrameworkCore (prerelease 13.3.4-preview).
// `RunDatabaseUpdateOnStart` já faz o apiservice esperar implicitamente — não
// adicionar `WaitForCompletion` em cima (criaria ciclo).
apiService
    .AddEFMigrations("engine-migrations", "CosmosPro.ML.DemandForCast.Engine.EngineDbContext")
    .RunDatabaseUpdateOnStart();

// Identity (login, papéis, PowerUser) vive na Web e persiste no banco `engine` —
// por isso a referência a engineDb, que antes não existia aqui.
// Ambos sem `value:` de propósito — resolvem por configuração, e é isso que faz o
// default de debug existir só em Development (appsettings.Development.json) e permitir
// que os fixtures de teste injetem o próprio par e-mail/senha. Um `value:` literal aqui
// é ignorado silenciosamente na configuração: o fixture semeia um admin com o e-mail
// dele, o Web semeia outro, e o login do E2E falha com "E-mail ou senha inválidos".
// Em qualquer outro ambiente o valor não existe e o startup falha pedindo user-secrets.
var powerUserEmail = builder.AddParameter("poweruser-email", secret: false);
var powerUserPassword = builder.AddParameter("poweruser-password", secret: true);

builder.AddProject<Projects.CosmosPro_ML_DemandForCast_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(engineDb)
    .WithReference(apiService)
    .WithEnvironment("PowerUser__Email", powerUserEmail)
    .WithEnvironment("PowerUser__Password", powerUserPassword)
    .WaitFor(engineDb)
    .WaitFor(apiService)
    .WithContainerRegistry(registry);

// Worker que consome a fila engine.CargasStage e processa os ZIPs do MinIO
// para o banco Stage (BULK INSERT por tabela em transação única).
builder.AddProject<Projects.CosmosPro_ML_DemandForCast_Worker>("worker")
    .WithReference(stageDb)
    .WithReference(engineDb)
    .WithReference(minio)
    .WaitFor(stageDb)
    .WaitFor(engineDb)
    .WaitFor(minio)
    .WaitForCompletion(stageSchema)
    .WithContainerRegistry(registry);
#pragma warning restore ASPIRECOMPUTE003

builder.Build().Run();
