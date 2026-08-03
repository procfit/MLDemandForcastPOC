using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);

// --- Destino de publicação ---------------------------------------------------

// Alvo de deploy: Docker Compose. Com este recurso presente, todo resource do
// modelo é publicado como serviço de compose — `aspire publish` gera
// docker-compose.yaml + .env, e `aspire do push` constrói e empurra as imagens.
//
// Existe para o compose sair do MESMO modelo que roda no F5, em vez de um YAML
// paralelo mantido à mão: um arquivo separado desatualizaria na primeira vez que
// alguém acrescentasse um recurso aqui e esquecesse de espelhar lá.
// Dashboard desligado no compose publicado. Ele é o único serviço que publica porta
// no host (18888) e não tem autenticação nenhuma: numa VPS com IP público, qualquer um
// que achasse a porta leria logs, traces e as **variáveis de ambiente** de todos os
// serviços — o que inclui as connection strings e a senha do PowerUser. No `F5` o
// dashboard continua existindo, porque lá ele roda na sua máquina.
var compose = builder.AddDockerComposeEnvironment("compose")
                     .WithDashboard(enabled: false);

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
// ASPIREPIPELINES003: `WithImagePushOptions` (e o tipo de contexto que ela recebe) é
// experimental no Aspire 13.4 e também vira **erro** de compilação. Suprimido porque é a
// API documentada para trocar a tag que o `aspire do push` aplica; sem ela a tag é o
// default do Aspire (`aspire-deploy-<timestamp>`). Mesmo alcance da supressão acima e
// mesma recomendação: revisar a cada bump do Aspire.
#pragma warning disable ASPIRECOMPUTE003, ASPIREPIPELINES003
var registry = builder.AddContainerRegistry("ghcr", registryEndpoint, registryRepository);

// Tag das imagens empurradas por `aspire do push`, lida de `IMAGE_TAG`.
//
// O pipeline manda `sha-<7 primeiros do commit>`: durante um incidente a pergunta é sempre
// "qual commit está rodando?", e a tag é o único lugar onde ela aparece no destino — no
// compose, no Dokploy, no `docker ps`. O default do Aspire, `aspire-deploy-<timestamp>`,
// responde *quando* alguém empurrou (obrigando a cruzar com o histórico do CI para achar o
// commit), muda a cada execução (envelhecendo a referência do destino) e ainda carrega o
// nome da ferramenta de build para dentro da identidade do artefato — que é imutável e
// seguiria afirmando "Aspire" muito depois de uma eventual troca de ferramenta.
//
// O default local **não** é o sha do commit: numa máquina de desenvolvimento não há como
// saber se a árvore está limpa, e `sha-abc1234` numa imagem com alterações não commitadas
// mente exatamente na pergunta que a tag existe para responder. `local` se identifica como
// não vinda do CI e não colide com nada que o pipeline produza.
var imageTag = builder.Configuration["IMAGE_TAG"] is { Length: > 0 } tagConfigurada
    ? tagConfigurada
    : "local";

Action<ContainerImagePushOptionsCallbackContext> aplicarTagDeImagem =
    context => context.Options.RemoteImageTag = imageTag;

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
// recurso DbGate (AddDbGate é idempotente) — e só acontece em run mode, então
// no compose publicado o DbGate fica só com a conexão do SQL Server.
var sqlServer = builder.AddSqlServer("sql")
                       .WithLifetime(ContainerLifetime.Persistent)
                       .WithDataVolume()
                       .WithDbGate(cfg => cfg.WithDataVolume().WithLifetime(ContainerLifetime.Persistent));

// Stage: staging area dos dados importados via UI (vendas, estoque, compras,
// promoções, mestres, IQVIA). O engine só lê deste banco; nunca escreve.
// Schema gerenciado declarativamente via SQL Server Project / DACPAC.
var stageDb = sqlServer.AddDatabase("Stage");

// engine: metadados próprios do engine (cargas, experimentos, runs, modelos,
// métricas) e as tabelas do ASP.NET Core Identity. Schema gerenciado por EF Core
// migrations, aplicadas pelo `db-migrator` abaixo.
var engineDb = sqlServer.AddDatabase("engine");

// --- ClickHouse: desativado, código preservado -------------------------------

// ClickHouse foi provisionado em F1, quando a escolha do armazenamento analítico
// ainda estava aberta. A implementação inteira acabou em SQL Server: nenhum código
// do produto lê `vendas-olap` — o apiservice recebia a connection string e nunca a
// abriu — e `OlapSchema/Scripts/` está vazio, então o runner aplica zero scripts e
// encerra com sucesso a cada start.
//
// Desativado no modelo (nem no F5, nem no compose) porque custava um container e um
// gate de startup para nada. NÃO removido: o projeto `OlapSchema`, o helper
// `ClickHouseDbGateExtensions` e os pacotes continuam no repositório, porque a
// necessidade analítica é esperada mais adiante — quando vier, é só descomentar o
// bloco abaixo e a linha correspondente no wiring do apiservice.
//
// Ao reativar, lembre de `.WithContainerRegistry(registry)` no runner se ele também
// tiver de ir para o compose publicado.

// var clickhouse = builder.AddClickHouse("clickhouse")
//                         .WithLifetime(ContainerLifetime.Persistent)
//                         .WithDataVolume()
//                         .WithDbGate();   // helper local, ver ClickHouseDbGateExtensions.cs
//
// // Nome do recurso Aspire usa hífen; nome do schema no ClickHouse é "vendas_olap".
// var vendasOlapDb = clickhouse.AddDatabase("vendas-olap", "vendas_olap");
//
// // ClickHouse não tem DACPAC. O projeto OlapSchema aplica scripts versionados
// // idempotentes ao banco vendas-olap (controle via tabela __schema_migrations).
// var olapSchema = builder.AddProject<Projects.CosmosPro_ML_DemandForCast_OlapSchema>("vendas-olap-schema")
//                         .WithReference(vendasOlapDb)
//                         .WaitFor(vendasOlapDb)
//                         .WithParentRelationship(vendasOlapDb.Resource);

// MinIO: object storage S3-compatible para armazenar ZIPs de import (CSVs
// de vendas, estoque, etc.). Persistido em volume; credenciais fixas via
// ParameterResource (ver bloco de parameters acima).
var minio = builder.AddMinioContainer("minio", minioAccessKey, minioSecretKey)
                   .WithLifetime(ContainerLifetime.Persistent)
                   .WithDataVolume();

// --- Schema deployment -------------------------------------------------------

// Migrador único dos dois bancos: publica o DACPAC do projeto Database no `Stage`
// e, na sequência, aplica as migrations do EF Core no `engine`. Sai com código != 0
// se qualquer uma das duas etapas falhar — é isso que sustenta o
// `condition: service_completed_successfully` no compose gerado.
//
// Antes daqui existiam dois recursos: `stage-schema` (`AddSqlProject`) e
// `engine-migrations` (`AddEFMigrations` + `RunDatabaseUpdateOnStart`). Ambos são
// one-shot que o Aspire só sabe executar em modo `run`: **nenhum dos dois aparecia no
// docker-compose.yaml** do `aspire publish`, e o `docker compose up` subia com os dois
// bancos vazios — sem tabela de Identity, portanto sem login possível. Um console
// comum vira serviço de compose normal, então o mesmo mecanismo serve F5 e deploy;
// manter os dois caminhos lado a lado só garantiria que o testado e o deployado
// divergissem.
//
// Um projeto para os dois bancos, e não um por banco, porque num VPS o operador quer
// uma coisa para checar e um log para ler — e não existe cenário em que se queira um
// banco migrado sem o outro.
var dbMigrator = builder.AddProject<Projects.CosmosPro_ML_DemandForCast_Migrator>("db-migrator")
                        .WithReference(stageDb)
                        .WithReference(engineDb)
                        .WaitFor(stageDb)
                        .WaitFor(engineDb)
                        .WithContainerRegistry(registry)
                        .WithImagePushOptions(aplicarTagDeImagem);

// --- Compose: prontidão do SQL Server antes do migrador ----------------------

// `WaitFor(stageDb)` acima significa duas coisas diferentes nos dois mundos. No `F5` o
// Aspire espera o **health check** do SQL Server; traduzido para compose, o mesmo
// `WaitFor` degrada para `condition: service_started`, que afirma apenas que o processo do
// container começou. Um SQL Server frio leva de 30 a 60 segundos até aceitar login, e no
// primeiro deploy real o migrador chegou lá em cinco segundos e falhou — derrubando, pelo
// gate `service_completed_successfully`, a subida inteira.
//
// A correção vive no modelo, e não no YAML: `aspire publish` regenera o arquivo e apagaria
// uma edição à mão. `ConfigureComposeFile` roda depois de o Aspire gerar o modelo do
// compose e antes de escrevê-lo, então o `depends_on` que o `WaitFor` produziu já existe
// para ser corrigido aqui — um callback por recurso rodaria antes dessa geração e teria a
// correção sobrescrita em silêncio.
//
// Só o `db-migrator` muda de condição. Os outros três serviços já esperam a conclusão dele
// (`service_completed_successfully`), o que implica o banco alcançável; trocar a condição
// deles também não adiantaria nada.
compose.ConfigureComposeFile(composeFile =>
{
    // O teste é um login com `SELECT 1`, não uma porta aberta: o listener do SQL Server
    // responde antes de o engine aceitar autenticação, e é a autenticação que o migrador
    // precisa. A senha vem do ambiente do próprio container e `$$` é o escape da
    // interpolação do compose (ela consome um `$`), então o shell recebe
    // `$MSSQL_SA_PASSWORD` e a senha não aparece no YAML gerado.
    //
    // Dois caminhos de `sqlcmd` porque `2022-latest` é tag móvel: hoje a imagem traz
    // `/opt/mssql-tools/bin` (ODBC 17), as gerações mais novas trazem
    // `/opt/mssql-tools18/bin`, onde a criptografia é obrigatória e exige `-C`. Apostar num
    // caminho único deixaria o `sql` eternamente unhealthy no dia da troca — e um
    // healthcheck que nunca fica verde tranca a subida do mesmo jeito que o bug original.
    composeFile.Services[sqlServer.Resource.Name].Healthcheck = new Healthcheck
    {
        Test =
        [
            "CMD-SHELL",
            "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -Q 'SELECT 1' "
                + "|| /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -Q 'SELECT 1'",
        ],
        Interval = "10s",
        Timeout = "5s",
        Retries = 10,
        // Enquanto o `start_period` corre, sonda que falha não gasta tentativa nem marca o
        // container como unhealthy. 90s cobre o pior caso — primeiro start num host frio,
        // quando o SQL Server ainda cria os bancos de sistema —, e não o caso médio; o
        // container fica pronto assim que a sonda passar, então a folga não custa tempo de
        // subida, só evita um veredito prematuro.
        StartPeriod = "90s",
    };

    composeFile.Services[dbMigrator.Resource.Name].DependsOn[sqlServer.Resource.Name] =
        new ServiceDependency { Condition = "service_healthy" };
});

// --- Services ----------------------------------------------------------------

var apiService = builder.AddProject<Projects.CosmosPro_ML_DemandForCast_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(stageDb)
    .WithReference(engineDb)
    .WithReference(minio)
    .WaitFor(stageDb)
    .WaitFor(engineDb)
    .WaitFor(minio)
    .WaitForCompletion(dbMigrator)
    .WithContainerRegistry(registry)
    .WithImagePushOptions(aplicarTagDeImagem);

// Ao reativar o ClickHouse, este é o único ponto do modelo que amarra o apiservice
// a ele — descomente junto com o bloco lá em cima:
//
// apiService.WithReference(vendasOlapDb)
//           .WaitFor(vendasOlapDb)
//           .WaitForCompletion(olapSchema);

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
    // A Web semeia o PowerUser no `engine` no primeiro start, então precisa das
    // tabelas do Identity. Esperar só o apiservice bastaria em `run` (ele já espera o
    // migrador), mas no compose cada `depends_on` é declarado por serviço — sem esta
    // linha a Web subiria em paralelo ao migrador.
    .WaitForCompletion(dbMigrator)
    .WithContainerRegistry(registry)
    .WithImagePushOptions(aplicarTagDeImagem);

// Worker que consome a fila engine.CargasStage e processa os ZIPs do MinIO
// para o banco Stage (BULK INSERT por tabela em transação única).
builder.AddProject<Projects.CosmosPro_ML_DemandForCast_Worker>("worker")
    .WithReference(stageDb)
    .WithReference(engineDb)
    .WithReference(minio)
    .WaitFor(stageDb)
    .WaitFor(engineDb)
    .WaitFor(minio)
    .WaitForCompletion(dbMigrator)
    .WithContainerRegistry(registry)
    .WithImagePushOptions(aplicarTagDeImagem);
#pragma warning restore ASPIRECOMPUTE003, ASPIREPIPELINES003

builder.Build().Run();
