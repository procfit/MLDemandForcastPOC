# CosmosPro.ML.DemandForCast (POC)

POC de um **engine de previsão de demanda** para apoiar o processo de **sugestão de compra** no varejo farmacêutico. Construído sobre .NET 10 + .NET Aspire, com os dados em **SQL Server** (há um ClickHouse provisionado no código, mas **desativado no AppHost** — não roda nem no `F5` nem no compose publicado, e nenhum caminho de código o consulta — ver §3).

> Status: **bootstrap**. Solução Aspire em branco com os 4 projetos do template (AppHost, ApiService, ServiceDefaults, Web). Próximos passos abaixo.

> **Contexto:** este repositório é o protótipo de um TCC. A visão completa (objetivo acadêmico, algoritmos comparados, dados, próximos passos) está em **[Docs/tcc-design.md](Docs/tcc-design.md)**.

---

## 1. Objetivo

Prever demanda (em unidades) por **SKU × ponto-de-venda × dia/semana**, com horizonte e granularidade configuráveis, para alimentar uma política de sugestão de compra (lead time + lote econômico + nível de serviço).

Saídas esperadas do engine:

- Previsão pontual (`yhat`)
- Intervalos de confiança / quantis (para dimensionar *safety stock*)
- *Backtest* (MAE, MAPE, WAPE, RMSE) por hierarquia (rede → loja → categoria → SKU)
- Sinalização de séries problemáticas (itens intermitentes, *new items*, vendas anômalas)

---

## 2. Stack de ML — por que **ML.NET**, com ressalvas

Avaliação direta para este caso de uso:

| Aspecto | ML.NET hoje | Implicação no POC |
|---|---|---|
| Manutenção | Ativo — repo `dotnet/machinelearning` recebe releases, ML.NET 4.x atual, pacotes `Microsoft.ML.TimeSeries`, `Microsoft.ML.FastTree`, `Microsoft.ML.LightGbm`, `Microsoft.ML.AutoML` em uso. | Aposta segura no curto/médio prazo. |
| `ForecastBySsa` (SSA — o que o tutorial da MS Learn ensina) | Univariado, sem covariáveis (promoção, preço, feriado, estoque). Foi o *flagship* do tutorial original de 2019. | **Não usar como motor principal.** Bom só como baseline didático ou detector de padrão. |
| LightGBM / FastTree regressão | Maduros, performance competitiva. Receita "GBM sobre features engenheiradas" é a mesma que venceu o M5 Forecasting Competition (Walmart, Kaggle 2020). | **Esta é a base do POC.** |
| AutoML Forecasting (`mlContext.Auto().CreateForecastingExperiment`) | Disponível, mas com catálogo de modelos limitado vs. Python (Nixtla, Darts, sktime). | Útil para *quick wins* em séries curtas; não substitui o pipeline customizado. |
| Itens intermitentes (Croston, TSB, SBA) | **Não há suporte nativo.** | Risco real em farma (medicamentos de baixo giro). Precisa de implementação custom **ou** sidecar Python. |
| Forecast hierárquico com reconciliação (MinT, OLS) | Sem suporte nativo. | Pode ser implementado manualmente; para o POC, projetar a interface aceitando esse hook. |
| ONNX export | Suportado. | Permite servir o modelo fora do .NET, se necessário. |

**Conclusão sobre o tutorial que você viu** (`learn.microsoft.com/.../time-series-demand-forecasting`): a impressão de "antigo" é correta — ele ensina **SSA**, que é insuficiente para o nosso caso. Não é que ML.NET esteja morto; é que aquele tutorial específico mostra o caminho fraco. O caminho forte (LightGBM + features) **não tem tutorial oficial elegante**, mas é o que vamos seguir.

### Decisão arquitetural

1. **Engine principal** = ML.NET + LightGBM regressão sobre *feature store* com lags, rolling stats, calendário (incluindo feriados nacionais/estaduais e datas farma-relevantes), promoção, preço, ruptura, hierarquia de produto/loja.
2. **Abstração `IForecastEngine`** desde o dia 1 — se um segmento do catálogo (ex.: itens intermitentes) exigir Croston/TSB, plugamos um *sidecar* Python (FastAPI + `statsforecast`) orquestrado pelo AppHost Aspire, sem alterar o consumidor.
3. **SSA / `DetectAnomalyBySrCnn`** = fluxo paralelo de qualidade de dados (flag de vendas anômalas antes de entrar no treino), **não** motor de previsão.
4. **Sem cloud lock-in no POC.** Tudo roda local via Aspire. Modelos serializados em `.zip` (ML.NET) e/ou ONNX.

---

## 3. Arquitetura

A cada `F5` o AppHost sobe os recursos abaixo lado a lado:

```
┌───────────────────────────────────────────────────────────────────┐
│ CosmosPro.ML.DemandForCast.AppHost (Aspire 13.3.1)                │
│                                                                   │
│  sql (SQL Server 2022 container, persistent volume)               │
│   ├─ Stage        ← schema deployado via DACPAC (SQL Project)     │
│   └─ engine       ← schema gerenciado via EF Core migrations (F2) │
│                                                                   │
│  dbgate (UI web de inspeção, volume persistente)                  │
│   └─ conexão "sql" ← auto-wire (SqlServer.Extensions)             │
│                                                                   │
│  db-migrator (one-shot, console .NET)                             │
│   ├─ publica o DACPAC do projeto Database no banco "Stage"        │
│   └─ aplica as EF Core migrations no banco "engine"               │
│                                                                   │
│  apiservice  ← .WaitFor(Stage, engine)                            │
│              ← .WaitForCompletion(db-migrator)                    │
│                                                                   │
│  webfrontend ← .WaitFor(apiservice)                               │
│              ← .WaitForCompletion(db-migrator)                    │
│                                                                   │
│  [futuro] forecast-py — sidecar opcional para Croston/TSB         │
└───────────────────────────────────────────────────────────────────┘
```

Persistência: `WithLifetime(ContainerLifetime.Persistent)` + `WithDataVolume()` em SQL Server. Os containers sobrevivem ao encerramento do AppHost; os volumes nomeados sobrevivem à recriação dos containers. Reset completo de dados exige `docker volume rm` explícito.

### ClickHouse: provisionado, desativado, código preservado

O ClickHouse entrou em F1, quando a escolha do armazenamento analítico ainda estava aberta.
A implementação inteira acabou em SQL Server — importação → `Stage` → features → treino →
comparação → materialização. **Nenhum código do produto jamais consultou `vendas-olap`**: o
`apiservice` recebia a connection string e nunca a abriu, e o diretório `Scripts/` do runner
de schema está vazio, então ele sempre aplicou zero scripts.

Por isso os três recursos (`clickhouse`, `vendas-olap` e `vendas-olap-schema`) estão **comentados**
no [AppHost.cs](CosmosPro.ML.DemandForCast.AppHost/AppHost.cs), junto com o trecho de wiring do
`apiservice` que os referenciava — ele nem carrega no `F5` nem aparece no `docker-compose.yaml`
gerado pelo `aspire publish`. Antes disso o ClickHouse rodava condicionalmente em modo `run`
(`if (builder.ExecutionContext.IsRunMode)`); a decisão evoluiu de "fora só do deploy" para
"fora de tudo", porque custava um container e um gate de startup para nada.

**O código não foi removido, só desligado.** O projeto `CosmosPro.ML.DemandForCast.OlapSchema`,
o helper `ClickHouseDbGateExtensions.cs` e os pacotes ClickHouse continuam no repositório —
a necessidade analítica é esperada mais adiante. **Para reativar:** descomente o bloco de
recursos ClickHouse em `AppHost.cs` (seção "ClickHouse: desativado, código preservado") e o
trecho de wiring logo após a definição do `apiService` (`apiService.WithReference(vendasOlapDb)...`).
Os fixtures de teste sobem o AppHost real via `Aspire.Hosting.Testing`, então também deixaram de
ver o ClickHouse.

Projetos da solução (`MLDemandForCastPOC.slnx`):

| Projeto | Papel |
|---|---|
| [CosmosPro.ML.DemandForCast.AppHost](CosmosPro.ML.DemandForCast.AppHost/) | Orquestração Aspire. Declara recursos (DBs, serviços) e dependências. |
| [CosmosPro.ML.DemandForCast.ApiService](CosmosPro.ML.DemandForCast.ApiService/) | HTTP API que expõe treino, forecast e métricas. Hospeda o engine ML.NET. |
| [CosmosPro.ML.DemandForCast.Web](CosmosPro.ML.DemandForCast.Web/) | Blazor — UI de cockpit: disparar experimentos, inspecionar backtests, comparar modelos. |
| [CosmosPro.ML.DemandForCast.Database](CosmosPro.ML.DemandForCast.Database/) | SQL Server Project (`MSBuild.Sdk.SqlProj/4.2.0`). Fonte de verdade do schema declarativo do banco `Stage`; compila para um `.dacpac` que o `Migrator` publica. |
| [CosmosPro.ML.DemandForCast.Migrator](CosmosPro.ML.DemandForCast.Migrator/) | Console .NET one-shot que aplica o schema dos **dois** bancos: DACPAC no `Stage` (via `Microsoft.SqlServer.DacFx`) e depois EF Core migrations no `engine`. Recurso `db-migrator` no AppHost — roda no F5 e vira serviço do compose publicado. |
| [CosmosPro.ML.DemandForCast.OlapSchema](CosmosPro.ML.DemandForCast.OlapSchema/) | Console .NET one-shot. Aplicaria scripts SQL versionados (embedded em `Scripts/*.sql`) ao banco `vendas-olap` no ClickHouse, com controle de versão via tabela `__schema_migrations` — mas `Scripts/` está **vazio**, então nunca aplicou nenhum. **Dormente**: o recurso `vendas-olap-schema` que o executava está comentado em `AppHost.cs`, então hoje o projeto não roda em lugar nenhum (ver §ClickHouse acima). |
| [CosmosPro.ML.DemandForCast.ServiceDefaults](CosmosPro.ML.DemandForCast.ServiceDefaults/) | OpenTelemetry, health checks, resilience. |

### Por que duas trilhas de migração de schema

- **DACPAC (`MSBuild.Sdk.SqlProj`)** para o banco `Stage` — schema **declarativo**, ideal para representar a fonte transacional consumida pelo engine. O DacFx faz o diff e aplica ALTERs; ganhamos histórico de schema versionável, refactoring com detecção de rename, e scripts pre/post-deployment.
- **EF Core migrations** para o banco `engine` — schema **imperativo**, ideal para tabelas próprias do engine (`CargasStage`, `TreinoJobs`, `SimulacoesCompra`) e para o Identity.

**Quem aplica as duas:** o projeto [`CosmosPro.ML.DemandForCast.Migrator`](CosmosPro.ML.DemandForCast.Migrator/),
um console .NET one-shot que roda o DACPAC no `Stage` e, em seguida, `Database.MigrateAsync()`
no `engine` — recurso `db-migrator` no AppHost. Antes disso eram dois recursos de hosting
(`AddSqlProject` e `AddEFMigrations().RunDatabaseUpdateOnStart()`); os dois só existiam em
modo `run` e **sumiam do `docker-compose.yaml`** gerado, deixando o deploy com bancos vazios.
Um console comum é publicado como serviço de compose normal, então o mesmo mecanismo cobre
`F5` e deploy — dois mecanismos divergiriam.

**Um projeto para os dois bancos** porque num VPS o operador quer uma coisa para checar e um
log para ler, e não existe cenário em que se queira um banco migrado sem o outro. A ordem é
fixa (`Stage` → `engine`) e qualquer falha em qualquer das duas etapas encerra o processo com
**código de saída != 0**, dizendo no log qual banco e qual etapa falharam — é esse código que
sustenta o `condition: service_completed_successfully` do compose. Os dois passos são
idempotentes: o DACPAC é declarativo (segunda execução não gera delta) e o EF pula migrations
já aplicadas, então rodar o container duas vezes é seguro. Os dois também **criam o banco** se
ele não existir, que é o caso do primeiro `docker compose up`.

O `.dacpac` viaja **dentro da imagem**: o `.csproj` do Migrator referencia o `.sqlproj`
(`ReferenceOutputAssembly=false`, só pela ordem de build) e um target pergunta ao projeto
`Database` o caminho da saída (`GetTargetPath`), copiando-a como `Content` para o `bin` e para
o `publish`. Em produção não há `.sqlproj` para compilar.
- **Runner customizado (.NET console one-shot)** para `vendas-olap` no ClickHouse — ClickHouse não tem DACPAC equivalente. O projeto `CosmosPro.ML.DemandForCast.OlapSchema` carrega scripts `.sql` versionados (embedded em `Scripts/`), mantém tabela `__schema_migrations` no próprio ClickHouse, e skipa scripts já aplicados. Convenção de nomes: `NNN_descricao.sql` (versão = nome sem extensão). **O mecanismo está pronto e sem uso: `Scripts/` não tem nenhum arquivo, então cada execução aplicaria zero scripts.** Hoje nem chega a executar — o recurso (`olapSchema`/`vendas-olap-schema`) e o `WaitForCompletion(olapSchema)` no `apiservice` estão comentados em `AppHost.cs`, desativados junto com o resto do ClickHouse (ver §3). **Detalhes completos: [Docs/olap-schema-migrations.md](Docs/olap-schema-migrations.md)**.

### Projetos previstos (próximas fases)

| Projeto | Papel |
|---|---|
| `CosmosPro.ML.DemandForCast.Engine` | Class library. `IForecastEngine`, implementações (LightGBM, baseline naive, sidecar adapter). Treino, predição, persistência de modelo. Hospeda o `EngineDbContext` para EF Core migrations. |
| `CosmosPro.ML.DemandForCast.Features` | Class library. Feature engineering puro (lags, rolling, calendar, joins). Sem dependência de ML.NET para facilitar teste. |
| `CosmosPro.ML.DemandForCast.Data` | Class library. Acesso a SQL Server (`Microsoft.Data.SqlClient`) e ClickHouse (`ClickHouse.Client`). Repositórios e DTOs de vendas/estoque/promoção. |
| `CosmosPro.ML.DemandForCast.Contracts` | Class library. DTOs + interfaces compartilhadas entre API, Engine, Web. |
| `CosmosPro.ML.DemandForCast.Engine.Tests` | xUnit. Backtest reprodutível, *golden samples*, *property tests* em features. |

---

## 4. Dados

### Fontes
- **SQL Server** — transacional (mestres de produto/loja, vendas recentes, promoções vigentes, ruptura).
- **ClickHouse** — analítico (histórico denso de vendas, ideal para varrer milhões de séries SKU×loja). Previsto em F1, **nunca usado**: o pipeline inteiro ficou em SQL Server. Desativado no AppHost — não sobrevive nem como ambiente de experimentação local (§3).

### Granularidade alvo (a confirmar com o negócio)
- Diária por SKU × loja para o pipeline.
- Possibilidade de agregação semanal (operação de compra costuma ser semanal).

### Features iniciais previstas
- **Tempo:** dia da semana, dia do mês, mês, semana epidemiológica, feriado nacional/estadual, dias até/desde feriado.
- **Lags:** 1, 7, 14, 28 (e seus *rolling means* / *rolling std*).
- **Calendário farma:** campanhas (ex.: vacinação, antialérgico em transição climática), datas com efeito conhecido.
- **Comerciais:** preço, preço relativo à categoria, *flag* de promoção, *flag* de ruptura no passado.
- **Hierarquia:** categoria, subcategoria, fabricante, princípio ativo (quando aplicável).
- **Loja:** região, perfil, dias de operação.

### Qualidade de dados
- Detecção de anomalia via `DetectAnomalyBySrCnn` / `DetectIidChangePoint` em fluxo paralelo, antes do treino.
- Política para **rupturas** (não tratar zero de ruptura como zero de demanda).

---

## 5. Como rodar

### Pré-requisitos
- .NET 10 SDK
- Docker Desktop (para recursos do Aspire — SQL Server, MinIO)
- Aspire workload: `dotnet workload install aspire`

### Executar
```powershell
dotnet run --project .\CosmosPro.ML.DemandForCast.AppHost\
```

Aspire Dashboard abre automaticamente; webfrontend, apiservice e DbGate ficam acessíveis pelos endpoints listados.

### Inspecionar bancos com DbGate

DbGate aparece como recurso `dbgate` no dashboard, com a conexão **SQL Server (`sql`)** já pronta.
Roda com volume e `WithLifetime(Persistent)`, então favoritos e abas salvas sobrevivem entre F5s.

**O container é declarado à mão no `AppHost.cs`, e não pelo `.WithDbGate()`** do
`CommunityToolkit.Aspire.Hosting.SqlServer.Extensions`, que era o que estava lá antes. Aquele helper
só cria o recurso em **run mode**: `aspire publish` gerava um compose sem `dbgate` nenhum. É a mesma
armadilha do `AddSqlProject` — existe no F5, desaparece no destino. Declarado à mão, o recurso vale
nos dois modos, e o que se inspeciona localmente é o que roda no deploy. As env vars seguem o
contrato do container (`LABEL_/SERVER_/PORT_/USER_/PASSWORD_/ENGINE_` + `CONNECTIONS`), o mesmo que
[ClickHouseDbGateExtensions.cs](CosmosPro.ML.DemandForCast.AppHost/ClickHouseDbGateExtensions.cs)
reproduz para o dia em que o ClickHouse voltar (§3) — o helper continua no repositório, sem uso hoje.

**Login é obrigatório, e isso não é opcional em ambiente publicado.** Dois parâmetros:
`dbgate-login` (default `admin`) e `dbgate-password`, secreto e **sem `value:` no código** — com
rota pública e sem login, o DbGate dá leitura e escrita em `Stage` e `engine` a quem descobrir a
URL: o dado comercial de todas as redes, e a possibilidade de inserir um `PowerUser` direto nas
tabelas do Identity e entrar na aplicação pela porta da frente.

No F5 a senha tem default em
[appsettings.Development.json](CosmosPro.ML.DemandForCast.AppHost/appsettings.Development.json),
pelo mesmo mecanismo do `PowerUser`: o arquivo só é lido em Development, então o default não
acompanha o binário para fora da máquina do desenvolvedor. Ele existe porque o Aspire pede valor
para **todo** parâmetro sem valor ao rodar — não só para os que o modo `run` usa —, e um prompt por
F5 não comprava segurança nenhuma num DbGate que escuta em localhost. User-secrets continua
ganhando do arquivo, para usar senha própria localmente:

```powershell
dotnet user-secrets --project CosmosPro.ML.DemandForCast.AppHost set "Parameters:dbgate-password" "<senha>"
```

No deploy, `DBGATE_LOGIN` e `DBGATE_PASSWORD` ficam no Environment do Dokploy — o `.env` gerado por
`aspire publish` emite **todo** parâmetro vazio, inclusive os que têm default no código.

### Como o extrator se comporta quando algo demora ou falha

**O catálogo de sugestões carrega só os cabeçalhos.** Doze meses da Natus são 19.610
sugestões e a consulta custa **0,27 s** no servidor. As colunas *Linhas* e *Lojas* não
ficam mais no grid: elas são buscadas para a sugestão que o comprador **selecionar**
(0,01 s) e aparecem ao lado da janela derivada. O motivo é medido, não estético — pedi-las
em lote custava **30,25 s por lote de 500 ids**, porque `COUNT(DISTINCT FILIAL)` não é
coberto pelo índice de `SUGESTAO_COMPRA` numa tabela de 124 milhões de linhas. Doze meses
davam 40 lotes, cerca de **20 minutos**, e a conexão até o cliente caiu antes disso, aos
2min09, com erro de transporte. Use o campo de filtro para achar a sugestão por id ou
descrição; com 19 mil linhas, rolar a lista não é navegação.

**Toda espera é cancelável e mostra o relógio — e a contagem por sugestão é a exceção
deliberada.** Testar conexão, carregar sugestões e extrair passam pelo mesmo escopo: os
campos travam, o botão **Cancelar** responde, a barra anda (indeterminada quando não há
total conhecido) e o rodapé mostra o tempo decorrido. Uma operação lenta se distingue de
uma travada sem precisar do código-fonte. A contagem de itens da sugestão selecionada
**não** passa por esse escopo: é uma busca leve em segundo plano, refeita a cada seleção,
que nunca trava campo nenhum, nunca abre diálogo de erro e escreve o próprio desfecho
(sucesso ou falha) direto na linha de informação da janela derivada.

**A falha diz onde quebrou.** Os erros são tipados e carregam etapa, arquivo `.sql`,
número do erro SQL e duração. Exemplo real, com a porta errada — o `1433` responde e o
logon falha sem mencionar porta nenhuma:

```
Não foi possível conectar ao SQL Server. Confira servidor, porta e banco — uma porta
errada costuma responder e falhar no logon, sem dizer que o problema é a porta.
  etapa: catálogo de sugestões
  query: catalogo_sugestoes.sql
  sqlNumber: 4060
```

**O log da janela sobrevive à sessão.** A interface gráfica grava tudo o que aparece no
painel em `extrator-log-AAAA-MM-DD.txt`, ao lado do `.exe`, e o botão **Copiar log** leva
o painel para a área de transferência. A senha é redigida. No modo linha de comando não
há arquivo: a saída vai para o console (dados em `stdout`, falhas e progresso em
`stderr`), que o operador redireciona — o arquivo só recebe linha ali quando uma consulta
é retentada. Ao reportar um problema, é esse conteúdo que interessa: ele leva a etapa, a
query, o número do erro SQL e a duração.

**Escolha de lojas.** A sugestão do PBS pode cobrir uma centena de lojas, e a rede pode
ter autorizado exportar só parte delas. O botão **Escolher lojas…** abre a lista das lojas
que a sugestão cita, com nome e quantos itens caem em cada uma; nada vem marcado, e
**Extrair** recusa a extração — sem gravar nada — até que ao menos uma loja seja escolhida.
O recorte vale para todos os
arquivos do ZIP — inclusive `sugestoes_compra_itens.csv`, que leva demanda, estoque de
segurança e preço de compra por loja — e o `manifesto.json` declara quais lojas saíram e
quantas a sugestão tinha, para o resultado da comparação não ser confundido com um da rede
inteira. No modo linha de comando é `--stores 12,45,78`; ausente, exporta todas.

### Publicar o extrator no MinIO

O comprador baixa o extrator pela página da sessão (`/comparacoes/{id}`, estado
"Aguardando dados"), que faz stream do bucket MinIO `extrator` — o `.exe` **não** é
embutido no repositório nem no build da Web (self-contained dá ~118 MB, e isso no git a
cada versão é inviável). Isso significa que, **num ambiente novo, nada é baixável até
alguém publicar** — passo obrigatório a cada release do extrator, não só na primeira vez.

**O caminho normal são dois passos.** O CI produz o par pronto e o operador o publica pela
UI:

1. **Baixar o artefato `extrator`** da execução do Actions (job "Testes do extrator
   (Windows)" — é o único runner Windows do pipeline, e o extrator é WinForms). A UI do
   Actions entrega um `.zip` com `extrator.exe` e `manifesto.json`, este já com a versão do
   `<Version>` do csproj e o SHA-256 calculado.
2. **Publicar em `/admin/extrator`** (só `PowerUser`), enviando **o `.zip` como veio** — sem
   descompactar. A tela mostra a versão vigente antes e depois.

**Um pacote, não dois campos de arquivo, e isso é deliberado.** O erro que importa nesta
operação é misturar execuções: mandar o `.exe` de uma com o `manifesto.json` de outra
publicaria um checksum que o download não cumpre, e quem conferisse concluiria "executável
adulterado" quando o fato foi um arquivo errado arrastado na pressa. Um ZIP lacrado, vindo
de um download só, torna essa combinação impossível na origem.

O servidor ainda assim **recalcula** o SHA-256 do executável que vem dentro do pacote e o
confere contra o declarado — agora como rede contra ZIP corrompido ou montado à mão. Pacote
incoerente é recusado sem escrever nada, e a versão que já estava no ar continua intacta.
O `publicadoEm` gravado é o instante da publicação, não a hora do build que veio no
manifesto: um artefato de semanas atrás publicado hoje está disponível desde hoje.

O ZIP entra como um arquivo só, mas o bucket continua guardando **dois objetos**. É o
`/versao` que decide isso: ele lê um manifesto de ~200 bytes a cada render da página da
sessão, e não teria por que abrir um ZIP de ~118 MB para isso.

O checksum existe para o comprador conferir o arquivo que baixou, e é calculado **uma vez**
na publicação, não a cada download: rehashear ~118 MB por request sob várias sessões
simultâneas seria custo de CPU pago por quem baixa, para um arquivo que não muda entre
releases.

#### Publicar à mão (sem CI, ou em ambiente local)

**1. Gerar o `.exe`** (o porquê de cada flag em
[Docs/extracao-pbs-stage.md § Como publicar o extrator](Docs/extracao-pbs-stage.md#como-publicar-o-extrator)):

```powershell
dotnet publish CosmosPro.ML.DemandForCast.Extractor -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Saída em `CosmosPro.ML.DemandForCast.Extractor\bin\Release\net10.0-windows\win-x64\publish\CosmosPro.ML.DemandForCast.Extractor.exe`.

**2. Calcular o checksum SHA-256** do `.exe` gerado:

```powershell
Get-FileHash .\CosmosPro.ML.DemandForCast.Extractor.exe -Algorithm SHA256
```

(equivalente em Linux/macOS: `sha256sum` ou `shasum -a 256`).

**3. Escrever o `manifesto.json`** com a versão (mesma do `<Version>` no
`CosmosPro.ML.DemandForCast.Extractor.csproj`) e o hash do passo anterior:

```json
{
  "versao": "0.14.0",
  "sha256": "<hash em minúsculas do passo 2>",
  "publicadoEm": "2026-07-30T12:00:00Z"
}
```

A leitura é case-insensitive nas chaves (`versao`/`Versao` tanto faz) — de propósito,
para um manifesto escrito à mão às pressas não falhar silenciosamente por causa de
maiúscula/minúscula.

**4. Zipar os dois** — `extrator.exe` e `manifesto.json`, com esses nomes — e publicar em
`/admin/extrator`. A tela aceita subpasta dentro do ZIP (um pacote feito da pasta de
publicação serve), mas **não** aceita dois `.exe` no mesmo pacote: qual dos dois toda a
base vai rodar não é escolha para um desempate silencioso.

Os passos abaixo são a alternativa que fala direto com o MinIO — útil em desenvolvimento,
onde o console está a um clique no Aspire Dashboard. **No deploy ela não existe:** o MinIO
não tem endpoint publicado (só a Web atravessa o Traefik), então lá é a UI ou um shell na
VPS.

**Subir os dois arquivos para o bucket `extrator`**, como `extrator.exe` e
`manifesto.json` (nomes fixos — a apiservice só procura por esses dois). O jeito mais
simples é pelo **MinIO Console**: no Aspire Dashboard, abra o recurso `minio`, o endpoint
do console (login `minioadmin`/`minioadmin` em ambiente local — outras credenciais vêm
dos parâmetros `minio-access-key`/`minio-secret-key` do AppHost), crie o bucket
`extrator` se ainda não existir, e arraste os dois arquivos para dentro dele — **sempre
os dois juntos**: publicar só o `.exe` deixa `/api/extrator/versao` respondendo 404
("não publicado") mesmo com o download já funcionando, e publicar só o manifesto deixa a
versão/checksum aparecerem na tela para um download que ainda falha.

Alternativa via [`mc`](https://min.io/docs/minio/linux/reference/minio-mc.html) (útil para
automatizar em pipeline de release):

```sh
mc alias set cosmospro-local http://localhost:<porta-do-endpoint-minio> minioadmin minioadmin
mc mb --ignore-existing cosmospro-local/extrator
mc cp CosmosPro.ML.DemandForCast.Extractor.exe cosmospro-local/extrator/extrator.exe
mc cp manifesto.json cosmospro-local/extrator/manifesto.json
```

A porta do endpoint MinIO muda a cada `F5` (Aspire aloca portas dinamicamente em dev) —
confirme no Aspire Dashboard antes de rodar o `mc alias set`.

**Sem versão publicada é o estado normal de um ambiente recém-criado, não um bug:** a
página da sessão mostra "o extrator ainda não foi publicado" no lugar do botão desabilitado,
e `GET /api/extrator/versao`/`GET /api/extrator/download` respondem 404 com uma mensagem
igualmente clara — nunca um 404 cru ou um stack trace. Se em vez de 404 a resposta for 500,
o problema é outro: MinIO fora do ar ou inacessível, não falta de publicação — as duas
situações têm respostas diferentes de propósito, para quem estiver de plantão saber por
onde começar.

### Pipeline de CI e imagens de container

[`.github/workflows/ci-imagens.yml`](.github/workflows/ci-imagens.yml) roda a **push na
`main`** e sob demanda (`workflow_dispatch`). São três jobs, nessa dependência:

| Job | Runner | O que faz |
|---|---|---|
| `windows-tests` | `windows-latest` | Testes do extrator **e** o binário dele: publica `win-x64` self-contained, calcula o SHA-256, escreve o `manifesto.json` e sobe o par como artefato `extrator`. Ele é WinForms (`net10.0-windows`, `WinExe`) e **não compila em Linux** — nem ele nem o projeto de teste dele, e é por isso que a suíte é dividida por sistema operacional, não por capricho de paralelismo. O `.exe` não entra em imagem nenhuma: quem o publica é o operador, em `/admin/extrator` (ver "Publicar o extrator no MinIO"). |
| `linux-tests` | `ubuntu-latest` | Compila em **Debug** (mesma configuração dos testes, e é dela que sai o DACPAC copiado para o `bin` do `Migrator`), roda os nove projetos de teste puros e, depois, os dois que sobem o AppHost real com SQL Server e MinIO em container (ClickHouse desativado — §3). |
| `images` | `ubuntu-latest` | Só se os dois anteriores passarem: constrói e empurra a **imagem base do worker** (abaixo), `aspire do push` (constrói e empurra as quatro imagens, na tag imutável), um **smoke** que abre a imagem do worker e confere as dependências nativas do LightGBM, um passo de `docker tag`/`docker push` que acrescenta a tag móvel, e `aspire publish` (gera `docker-compose.yaml` + `.env`), publicados como artefato `aspire-compose` da execução. |

Os testes de integração e E2E ficam em **passos separados e sequenciais** do mesmo job de
propósito: eles se excluem mutuamente por um lock de arquivo entre processos
([`AppHostExclusiveLock`](tests/CosmosPro.ML.DemandForCast.Tests.Shared/AppHostExclusiveLock.cs)),
e rodá-los em sequência faz o segundo já encontrar o primeiro encerrado em vez de esperar
os 30 minutos de tolerância do lock.

**Onde as imagens aparecem.** No GHCR, sob o próprio repositório —
`ghcr.io/<organização>/<repositório>/…`, tudo **em minúsculas** (o GHCR rejeita maiúsculas
no push, e o nome do repositório tem algumas; o workflow converte). São quatro:
`apiservice`, `webfrontend`, `worker` e `db-migrator` (`vendas-olap-schema` saiu junto com o
ClickHouse — §3), mais a `worker-base` descrita logo abaixo, que é insumo de build e não
serviço. A infraestrutura (SQL Server, MinIO, dashboard) **não** é construída aqui: vem de
imagem pública, já referenciada no compose gerado.

#### A imagem base do worker, e por que ela existe

O `worker` é o único serviço que treina modelo, e o único que **não** sai da base default do
container publishing do SDK. O nativo do LightGBM (`lib_lightgbm.so`, do pacote `LightGBM`
que vem por `Microsoft.ML.LightGbm`) declara `libgomp.so.1` em `DT_NEEDED`, e
`mcr.microsoft.com/dotnet/runtime:10.0` não traz esse pacote — nem o `-chiseled-extra`, cujo
"extra" é ICU e tzdata. O resultado é uma imagem que compila, sobe, responde ao health check
e quebra **só** quando a primeira sessão chega à fase de treino, com
`Unable to load shared library 'lib_lightgbm'` na tela do comprador e a sessão em `Falha`
pedindo que ele reenvie o ZIP — que nunca resolve, porque o problema não é o ZIP.

Instalar o pacote na imagem do app não é possível: o container publishing do SDK monta
camadas sobre uma base, não executa `RUN`. Daí
[`worker-base.Dockerfile`](CosmosPro.ML.DemandForCast.Worker/worker-base.Dockerfile) — quatro
linhas sobre a mesma base do SDK, com `libgomp1` e uma asserção de `ldconfig` que falha o
build se o pacote deixar de entregar a biblioteca com esse nome. O CI a constrói e empurra
**antes** do `aspire do push`, porque o SDK vai puxá-la do registry ao montar a imagem do
worker (ele não consulta o daemon local). A referência mora em **um** lugar, o
`ContainerBaseImage` do
[`.csproj` do worker](CosmosPro.ML.DemandForCast.Worker/CosmosPro.ML.DemandForCast.Worker.csproj),
e o workflow pergunta qual é por `-getProperty` em vez de repeti-la.

A tag dela (`worker-base:net10.0-libgomp1`) **não** é imutável, ao contrário das quatro de
serviço: o CI a reconstrói a cada execução, então correção de segurança do sistema entra
sozinha. Isso é seguro porque a base é insumo de build — a imagem do worker carrega as
camadas dela, não uma referência a ela. `aspire do push` **local** precisa de
`docker login ghcr.io` para puxá-la.

**Por que nenhum teste pegava isso.** `Forecasting.Tests` e `Purchasing.Tests` treinam
LightGBM de verdade, em Linux, e passam — porque o runner do GitHub tem `libgomp1`
instalado. O que faltava não era cobertura de código: era alguém abrir o **artefato**. É o
que o passo de smoke faz, dentro da imagem publicada: confere os frameworks com
`dotnet --list-runtimes` e as dependências nativas com `ldd`.

A checagem de frameworks está ali porque a primeira versão deste smoke, que só rodava `ldd`,
**passou numa imagem que não iniciava**: fixar `ContainerBaseImage` sobrescreve a escolha do
SDK, e a base que eu apontei (`dotnet/runtime`) trazia o nativo mas não o framework do ASP.NET
Core, que este worker referencia via `ServiceDefaults`. O processo saía em dois segundos com
exit 150 (`No frameworks were found`), a cada start — imagem que sobe, container que morre,
fila que para, e dois dias de produção sem worker. Abrir o artefato pela metade dá o mesmo
conforto de abrir inteiro e não é a mesma coisa. Ele não barra o push (o `aspire do push` constrói e empurra na
mesma operação), mas impede que a imagem seja usada: a tag móvel não avança e o artefato de
compose não é publicado.

**Como as imagens são marcadas.** Duas tags por imagem, cada uma com um trabalho:

| Forma | Exemplo | Para quê |
|---|---|---|
| `sha-<7 primeiros do commit>` | `ghcr.io/procfit/mldemandforcastpoc/worker:sha-5b36f24` | **Imutável.** É a que vai para o `.env` do destino. Aponta para o commit exato em um passo, sem cruzar com o histórico do CI — durante um incidente a pergunta é sempre "qual commit está rodando?", e no destino (compose, Dokploy, `docker ps`) a tag é o único lugar onde ela aparece. |
| nome da branch, saneado | `ghcr.io/procfit/mldemandforcastpoc/worker:main` | **Móvel.** Segue a última execução verde daquela branch, para quem quer "o último de `main`" sem descobrir o sha. Vem do ref da execução, não de um `main` cravado, então um `workflow_dispatch` numa branch de feature publica `feat-extrator-cli` e **não** mexe na tag que a produção segue. Nome de branch aceita `/` e tag de imagem não, daí o saneamento (`feat/extrator-cli` → `feat-extrator-cli`). |

Não há mais tag do tipo `aspire-deploy-<timestamp>` (o default do `aspire do push`, substituído
pela variável `IMAGE_TAG` que o AppHost lê no callback `WithImagePushOptions`): ela dizia
*quando* alguém empurrou, mudava a cada execução e carregava o nome da ferramenta de build para
dentro da identidade — imutável — do artefato. **A tag imutável não precisa mais ser copiada do
log**: é `sha-` + os 7 primeiros caracteres do commit que se quer deployar.

Rodando `aspire do push` à mão, sem `IMAGE_TAG` no ambiente, a tag é `local` — de propósito, para
uma imagem de máquina de desenvolvimento se identificar como tal. Não é o sha do commit porque
localmente não há como saber se a árvore está limpa, e `sha-abc1234` numa imagem com alterações
não commitadas mente exatamente na pergunta que a tag existe para responder.

O passo que acrescenta a tag móvel é `docker tag` + `docker push` comum — o Aspire empurra uma
tag por imagem. Ele **não** tem a lista de serviços escrita à mão: pergunta ao daemon local
quais imagens casam com o prefixo do repositório mais a tag imutável, e falha se não achar
nenhuma. Um quinto recurso acrescentado ao AppHost entra nessa lista sozinho, em vez de ficar
sem a tag móvel até alguém notar no destino.

**O que o operador precisa preencher no `.env`.** O arquivo é enviado **em branco**, do
jeito que o `aspire publish` gera — nenhuma credencial trafega pelo pipeline:

- `APISERVICE_IMAGE`, `WEBFRONTEND_IMAGE`, `WORKER_IMAGE`, `DB_MIGRATOR_IMAGE` — as
  referências completas, com a **tag imutável** (`…/worker:sha-5b36f24`). Saem **vazias**:
  configurar o registry no AppHost afeta o `aspire do push`, não o `aspire publish`. Use a
  imutável, não a móvel: com o nome da branch o compose puxaria silenciosamente outra coisa
  no próximo `docker compose pull`, e o que roda no destino deixaria de ser identificável.
- `APISERVICE_PORT`, `WEBFRONTEND_PORT` — as portas publicadas no host.
- `SQL_PASSWORD`, `MINIO_ACCESS_KEY`, `MINIO_SECRET_KEY` — as
  credenciais da infraestrutura no destino. Não são as do ambiente local: `minioadmin`
  existe para o `F5` subir em qualquer máquina, e repeti-lo fora dali é entregar o object
  storage.
- `POWERUSER_EMAIL`, `POWERUSER_PASSWORD` — o administrador global semeado no primeiro
  start da Web. Sem eles a Web **falha no startup de propósito**.

#### O schema dos dois bancos, no compose (buraco fechado)

Houve um período em que `docker compose up` subia com os **dois bancos vazios**: os recursos
`stage-schema` (`AddSqlProject`) e `engine-migrations` (`AddEFMigrations`) eram one-shot que o
Aspire só sabia executar em modo `run` e **não apareciam no `docker-compose.yaml` gerado** —
zero ocorrências, sem aviso do `aspire publish`. Sem tabela de Identity não há usuário, sem
usuário não há login: não era degradação parcial, era aplicação inutilizável.

Os dois foram substituídos pelo **`db-migrator`** — um console .NET comum, e por isso um
serviço de compose comum. O YAML gerado expressa exatamente o padrão de init container:

```yaml
apiservice:
  depends_on:
    db-migrator:
      condition: "service_completed_successfully"
```

`apiservice`, `webfrontend` e `worker` têm os três esse `depends_on`, então nenhum deles abre
uma conexão antes de o schema estar aplicado. O migrador cria os bancos se não existirem,
aplica o DACPAC no `Stage`, aplica as EF Core migrations no `engine` e **sai com código != 0**
se qualquer etapa falhar — sem isso o gate do compose não teria efeito e os serviços subiriam
contra um banco pela metade. Não há mais passo manual entre o `docker compose up` e o primeiro
acesso; o único pré-requisito continua sendo preencher o `.env` (abaixo).

#### Reinício, pull e jobs órfãos (buraco fechado depois de uma outage de dois dias)

Produção ficou **dois dias sem `worker`**. Ele subiu num deploy, o processo saiu em algum
momento, e a partir daí nada o levantou: o compose publicado não trazia `restart:`, e o default
do Docker é `no`. A limpeza de Docker do Dokploy (`enableDockerCleanup`) removeu o container
parado — junto com o log — e a imagem que ficou sem uso. A fila parou, e a tela do comprador
seguiu dizendo "importando" todo esse tempo.

O que torna essa falha diferente das outras é **quem** deveria relatá-la: o relógio que encerra
uma fase abandonada (`SessaoJobs.FaseAbandonada`, 2h) roda dentro do worker. O processo que
executa é o mesmo que denuncia, então a morte dele é a única falha que o sistema não tinha como
contar. Três mudanças fecham isso, todas nascendo do modelo em vez de edição à mão no YAML:

| Mudança | Onde | Por quê |
|---|---|---|
| `restart: unless-stopped` em todos os serviços, **menos** o `db-migrator` | `ConfigureComposeFile` no [AppHost.cs](CosmosPro.ML.DemandForCast.AppHost/AppHost.cs) | Processo que morre volta sozinho. O migrador fica de fora porque é one-shot e precisa terminar com exit 0 para o `service_completed_successfully` liberar — com política de reinício ele republicaria o DACPAC em loop. |
| `pull_policy: always` nos quatro serviços com imagem deste repositório | idem | Eles usam a tag móvel `:main` e o Compose aceita "já existe local": sem isso, deploy depois de CI verde sobe o binário antigo **e reporta sucesso**. Já aconteceu; a correção vivia como edição à mão no YAML do Dokploy, que a próxima regeneração apagaria em silêncio. |
| [`OrfaosWorker`](CosmosPro.ML.DemandForCast.Worker/OrfaosWorker.cs) encerra job em `Processando` além do limite | Worker | As quatro filas reclamam com `WHERE Status = 'Pendente'` e sem lease: linha reclamada por processo morto não é reclamada nem encerrada por ninguém. Por **idade**, não numa varredura de startup — o modelo admite mais de um worker, e ali um processo subindo mataria o job que outro está executando. |

O `OrfaosWorker` **complementa** o `FaseAbandonada`, não o substitui: aquele encerra a *sessão*
(é o que desbloqueia o comprador), este encerra a *linha do job* (é o que a página técnica
mostra e a fila enxerga). Os dois compartilham o relógio e o texto de propósito, para dois
registros do mesmo evento não poderem discordar.

**O compose da produção é `raw` guardado no Dokploy.** A descrição dele diz "regenerar e
recolar", e é isso: `aspire publish`, e o YAML gerado substitui o de lá. Os domínios do Traefik
(`webfrontend`, `dbgate`) são configuração do Dokploy, fora do YAML, e sobrevivem à recolagem.

---

## 6. Roadmap do POC

- [x] **F0 — Fundação**: README, CLAUDE.md, decisão arquitetural.
- [x] **F1 — Orquestração**: AppHost sobe SQL Server (persistente) + ClickHouse (persistente) + SQL Project DACPAC a cada F5. Bancos `vendas`, `engine`, `vendas-olap` declarados. Schema bootstrap funcional. **ClickHouse desativado depois** (código preservado, resources comentados em `AppHost.cs`) — ver §3.
- [ ] **F2 — Dados & schema**:
  - [x] **F2.1** — Schema do banco `Stage` (renomeado de `vendas`) no SQL Project: `Lojas`, `Produtos`, `Vendas`, `EstoquesDiarios`, `Compras`, `Promocoes`, `MercadoIqvia` (todos sob `dbo`, plural, com FKs/IXs/CKs; aplicado via DACPAC ~28s, validado via MCP). MinIO adicionado ao AppHost para armazenar ZIPs de import.
  - [ ] **F2.2** — Templates de planilha + UI de importação no Blazor.
  - [ ] **F2.3** — Dataset sintético farma para semear.
  - [x] **F2.4** — Projeto `Engine` (class library) com `EngineDbContext` + entidade `CargaStage` + EF Core migrations aplicadas via `Aspire.Hosting.EntityFrameworkCore.AddEFMigrations` (prerelease 13.3.4-preview). Tabela `engine.CargasStage` validada via MCP.
  - [x] **F2.5** — API endpoint `POST /api/imports/upload` (valida ZIP estrutural + headers das CSVs, upload MinIO bucket `imports/`, INSERT `CargasStage` com `Status=Pendente`, retorna 202 + Id). GET `/api/imports/{id}` retorna o estado da carga. Validado end-to-end via curl: happy path 202 + erros estruturais 400.
  - [x] **F2.6** — Blazor UI: upload de ZIP via `<InputFile>` + lista de cargas com polling 3s. Endpoint `GET /api/imports?take=N` adicionado para alimentar a listagem. **Rotas mudaram na F14:** as duas telas (então `/importar` e `/jobs`) foram fundidas numa página só, hoje em `/tecnico/importar`.
  - [x] **F2.7** — Worker (`CosmosPro.ML.DemandForCast.Worker`, BackgroundService). Claim com `;WITH cte AS (... WITH (UPDLOCK, READPAST) ...) UPDATE cte SET Status='Processando' OUTPUT INSERTED.*`. Download ZIP do MinIO + extract → DataTable tipada via `TableSchemas` → `DELETE FROM` (ordem reversa de FK) + `SqlBulkCopy` (ordem de FK) tudo em transação única. UPDATE final via EF Core `ExecuteUpdateAsync`. Validado end-to-end: ZIP com 18 linhas → Status=Concluida + LinhasImportadas=18 + tabelas Stage com contagem exata.
- [x] **F3 — Testes** (vai antes do resto pra evitar regressão silenciosa):
  - [x] **F3.1** — `tests/Tests.Shared` com Bogus fakers (um arquivo por entidade — `LojaFaker`, `ProdutoFaker`, `VendaFaker`, `EstoqueDiarioFaker`, `CompraFaker`, `PromocaoFaker`, `MercadoIqviaFaker`) + `CsvZipBuilder` (gera ZIP em memória com 7 CSVs).
  - [x] **F3.2** — Unit tests por projeto: `Engine.Tests` (4), `OlapSchema.Tests` (5), `ApiService.Tests` (4), `Worker.Tests` (16), `Web.Tests` (9) — xUnit v3 + FluentAssertions + NSubstitute. 38/38 verdes.
  - [x] **F3.3** — `ApiService.IntegrationTests` — Aspire.Hosting.Testing 13.3.4 + Refit 10.1.6 no Act. 3 cenários (upload happy path 202 + listagem, ZIP incompleto 400, GET inexistente 404). Workaround obrigatório no fixture para `SqlProjectResource` (remover anotação `IProjectMetadata` + chamar `WithDacpac(absolutePath)` apontando para o DACPAC buildado), porque o evaluation MSBuild falha sob `dotnet test`. 3/3 verdes.
  - [x] **F3.4** — `Web.E2ETests` — Aspire.Hosting.Testing + Playwright 1.59 no Act. Cenário: subir ZIP gerado por fakers na tela de importação, ver a notificação verde e confirmar a linha no grid da mesma página. Localiza linha por filename (não por GUID prefix — UUIDv7 compartilha 8 chars iniciais entre cargas próximas no tempo). Depois da F11 o cenário faz login antes; depois da F14 ele navega para `/tecnico/importar`, já que `/` passou a ser o painel de comparações. Hoje o projeto tem cinco arquivos de cenário: `ImportsE2ETests`, `AuthorizationE2ETests`, `ComparacaoE2ETests`, `ComparacoesE2ETests` e `SessaoResultadoE2ETests`.
- [x] **F4 — Dataset sintético farma** (era F2.3): novo projeto `CosmosPro.ML.DemandForCast.SyntheticData` (class library) com gerador procedural. Regras de domínio:
  - **ABC**: power-law (alpha=1.2) sobre baseline por rank; top 20% SKUs respondem por ~80% do volume.
  - **Sazonalidade semanal**: sáb ×1.5, dom ×0.6, dias úteis ×1.0.
  - **Sazonalidade anual**: senoidal com pico no inverno (julho ~ dia 200), amplitude ±15%.
  - **Promoções**: ~5% SKUs com janela 7-14 dias, multiplier 2-3×, desconto 10-30%.
  - **Ruptura**: probabilidade base 3% dia-SKU-loja; cauda rupturada 2.5× mais que top sellers.
  - **IQVIA**: agrega por (Mês × PrincipioAtivo × UF), 5k-50k unidades + share 5-25%.
  - **Ruído Poisson** sobre lambda baseline × fatores; aproximação Normal pra lambda > 30.
  - **Determinismo**: mesmo seed → mesmo dataset (validado por teste).
  - **Endpoint** `POST /api/imports/synthetic` no ApiService gera ZIP em memória → MinIO → CargaStage Pendente (passa pelo Worker como upload normal). Botão "Gerar dados sintéticos" na UI Radzen abre dialog com params (lojas, SKUs, datas, seed).
  - **Testes**: 7 unit tests em `SyntheticData.Tests` (ZIP estrutura, headers, determinismo, ABC concentração, fração de promoções, stats).
- [x] **F5 — Features**: projeto `CosmosPro.ML.DemandForCast.Features` (class library pura, sem deps externas). Decisões: **granularidade diária**, **horizonte/lead time 7 dias**, **ruptura mascarada do treino**.
  - `DailyObservation` (input, série densa diária por SKU×loja) → `FeatureVector` (output: features + target + `IsValidTarget`).
  - **Anti-leakage rígido**: nenhuma feature de histórico usa dados mais recentes que D − LeadTime (7d). Validado por teste (pico dentro do lead time não vaza para lag/rolling/max).
  - **Lags** 7/14/21/28; **rolling** (mean 7/28, std 28, max 28) com janela terminando em D−7.
  - **Calendário** do dia-alvo: dia-da-semana, dia-do-mês, mês, fim-de-semana, **feriado nacional BR** (`BrazilianHolidays`, fixos + móveis via Computus/Páscoa).
  - **Promoção/preço** planejados de D (conhecidos): flag promo, dias-desde-última-promo, preço, preço relativo à média.
  - **Hierarquia** categórica: SKU, categoria, princípio ativo, classe ABC, loja, UF, região, perfil.
  - **Densifica gaps** (dias sem venda → qtd 0) para lags corretos; exige histórico ≥ max(maiorLag, LeadTime+rollingLongo−1) = 34 dias.
  - **Masking de ruptura**: dia com estoque 0 → `IsValidTarget=false` (CLAUDE.md §6), excluído do treino mas mantido como contexto histórico.
  - **Testes**: 10 unit tests em `Features.Tests` (correção de lag, anti-leakage, rolling, masking, densificação, calendário/feriado, agrupamento por SKU×loja, validação de config).
  - **Fonte dos dados**: F5 opera sobre observações em memória (puro/determinístico). O loader Stage→observações (densificação a partir de Vendas×EstoquesDiarios×Promocoes) fica acoplado em F6.
- [ ] **F6 — Engine de previsão v1**: previsores de **demanda** (`IForecastEngine`) + walk-forward + persistência. **Nota:** eMax/eSeg saíram daqui — são política de reposição (estoque máx/segurança), não previsão; foram para **F8**. **Atualização F13:** o comparativo do TCC não é mais contra uma reimplementação nossa da regra clássica — é contra o baseline real do ERP (PBS), que já grava sua própria eMax/eSeg e sua própria previsão de demanda.
  - [x] **F6.1** — Projeto `CosmosPro.ML.DemandForCast.Forecasting` (lib pura, ref. Features). `IForecastEngine`/`IForecastModel`; baselines **naïve sazonal** (previsão = Lag7) e **média móvel** (RollMean7/28); métricas **MAE/MAPE/WAPE/RMSE** (`ForecastMetrics`); **backtest walk-forward** (`WalkForwardBacktest`, origem rolante + janela de treino expansível) com métricas globais e por hierarquia (categoria, ABC, loja, UF); ruptura excluída de treino/avaliação. 13 unit tests (inclui anti-leakage: treino nunca alcança a janela de teste).
  - [x] **F6.2** — Engine **LightGBM** (`Microsoft.ML` + `Microsoft.ML.LightGbm` 4.0.2) sobre `FeatureVector`. **Modelo global** (SKU e demais categóricas via one-hot + `Concatenate` → `Regression.Trainers.LightGbm(options)`), `MLContext(seed)` reprodutível. Classes de dados tipadas (`LightGbmInput`/`Output`). **Persistência .zip** (`Model.Save`/`Load`); `PredictionEngine` para inferência sequencial (descartado por fold no backtest). 4 unit tests (treina/prevê, supera média global, roundtrip save/load preserva previsões).
  - [x] **F6.3** — Integração end-to-end. Loader `StageObservationLoader` cruza Vendas × EstoquesDiarios × Promoções + masters de Lojas/Produtos; deriva **classe ABC** a partir do volume cumulativo de vendas (Pareto); injeta dias de **ruptura** (estoque ≤ 0) como observações com `EmRuptura=true` (evita o anti-pattern de "venda 0 = demanda 0" do CLAUDE.md §6). Limita aos top **MaxSkus** SKUs por volume — parâmetro do treino. Novo entity `TreinoJob` + migration EF; `TreinoWorker` (BackgroundService no Worker) faz polling com mesmo padrão `UPDLOCK/READPAST` das cargas; `TreinoProcessor` carrega → features → walk-forward de 3 engines (naïve, média móvel, LightGBM) → treina LightGBM final em todas as features válidas → salva `.zip` no MinIO (bucket `models`) → grava `ResultadoJson` (métricas globais + por hierarquia). Endpoints `POST /api/training/run`, `GET /api/training`, `GET /api/training/{id}`. UI: botão "Treinar" ativo + input Top SKUs + tabela de execuções (polling 3s) + comparação walk-forward (engine, WAPE, MAE, RMSE, MAPE, n) com badge "MELHOR" e troféu no líder. **Validado end-to-end com dataset sintético**: 25 SKUs · 165k features · 3 folds × 14 dias · LightGBM 29.4% WAPE vs naïve 60.7% (metade do erro).
- [x] **F7 — Backtest (dashboard)**: drill-down por hierarquia em `/treinamento`. Card "Drill-down por hierarquia" com 4 abas (Categoria, ClasseAbc, Loja, UF). Cada aba pivota o `PorDimensao` do `ResultadoJson` em uma tabela `chave × WAPE-de-cada-engine`, com coluna **Vencedor** (badge no engine de menor WAPE) e flag **Regressão?** quando o LightGBM perde para algum baseline naquela chave (CLAUDE.md §6: "médias globais escondem regressões locais"). Cabeçalho de cada aba mostra contagem de regressões com `RadzenAlert` de aviso. Linhas ordenadas por `n` desc (chaves mais relevantes primeiro). Validado no browser: 25 SKUs · LightGBM venceu em todas as 3 categorias (Prescrição 36,8% / OTC 22,3% / Controlado 43,3% WAPE — vs 60-77% do naïve), 0 regressões no dataset sintético.
- [x] **F8 — Sugestão de compra**: novo projeto `CosmosPro.ML.DemandForCast.Purchasing` (class library) com `IPurchasingPolicy` no estilo clássico **(s, S)**, simulador `PurchasingSimulator` (replay determinístico dia-a-dia) e KPIs comparáveis (nível de serviço unidades/dias, venda perdida, cobertura, giro, pedidos, custo total). Política `ForecastRopPolicy` (soma do forecast LightGBM no LT+ciclo + safety pelo desvio dos resíduos). Adapter `LightGbmForecaster` indexa features pré-construídas por (Sku, Loja, Data). Nova entity `SimulacaoCompra` + migration `AddSimulacoesCompra` (FK lógica para TreinoJob). `SimulacaoWorker`/`SimulacaoProcessor` no Worker baixam o modelo do MinIO, fazem o replay e gravam `ResultadoJson`. Endpoints `POST /api/purchasing/simulate`, `GET /api/purchasing`, `GET /api/purchasing/{id}`. UI `/sugestao-compra` com select de TreinoJob, parâmetros (janela, LT, ciclo, fator z), tabela de execuções (polling 3s), KPIs do replay + drill-down por hierarquia (Categoria, ClasseAbc, Loja, UF) com vencedor por custo total. **Nota (F13):** originalmente havia uma segunda política, `EMaxESegPolicy` (média + desvio histórico × LT), como "regra clássica" do TCC — foi **removida**: o ERP real (PBS) grava sua própria eMax/eSeg e sua própria previsão de demanda, então o baseline do comparativo passou a ser esse dado real, não uma cópia nossa. `/sugestao-compra` virou ferramenta secundária (replay de uma política só); o comparativo do TCC contra o ERP é tratado em outro fluxo (ver F13 abaixo).
- [ ] **F9 — Decisão go/no-go**: avaliar qualidade do baseline LightGBM e dos ganhos da política forecast-based contra o **ERP real** (F13) em dado real. Decidir se entra sidecar Python para intermitentes. **Bloqueado hoje pelo mesmo horizonte que trava a camada B da F13** — a decisão de compra de ciclo completo não é comparável com previsão de 7 dias.

### Redirecionamento multi-rede (F10–F14)

O POC deixa de ser banco de provas single-user e passa a ser instrumento de coleta que usuários de **redes distintas** operam sozinhos, com comparativo contra o **ERP real** em vez de contra reimplementação nossa. Planos detalhados em [Docs/superpowers/plans/](Docs/superpowers/plans/).

- [x] **F10 — Isolamento por rede**: `RedeId` como primeira coluna da PK em `Lojas`/`Produtos` (códigos de ERP colidem entre redes) e nas tabelas de referência externa; tabelas-filhas com FK **composta** `(RedeId, Sku)`/`(RedeId, LojaId)`, que amarra cada linha ao inquilino sem FK redundante no caminho do `SqlBulkCopy`. Nova `dbo.Redes` no Stage como âncora referencial + `engine.Redes` como registro (FK entre bancos não existe no SQL Server, então o Worker projeta a rede no Stage a cada import). **`RedeId` nunca trafega no CSV** — o Worker injeta a partir da `CargaStage`, o que mantém o contrato CSV, o Extractor e os fakers intactos e impede que um cliente reivindique a rede de outro. `CargaProcessor` passa a fazer `DELETE ... WHERE RedeId` (antes apagava o Stage de todas as redes). Endpoint `/api/redes` + guarda `ValidateRedeAsync` em todo endpoint escopado. Teste de integração cobre o cenário que motivou a fase: import de duas redes preservando as duas.
- [x] **F11 — Usuários e PowerUser**: ASP.NET Core Identity (`Microsoft.AspNetCore.Identity.EntityFrameworkCore` 10.0.10) com stores EF no banco `engine`; `EngineDbContext` passa a ser `IdentityDbContext<Usuario, IdentityRole<Guid>, Guid>`. Cookie auth hospedado na **Web** — é o único processo que o navegador alcança, já que a `apiservice` não tem endpoint externo. Dois papéis: `PowerUser` (global, `RedeId` nulo) e `UsuarioRede` (escopado, `RedeId` obrigatório).
  - **`IRedeContext`** substitui o `redeId` de request da F10: resolve a rede a partir do usuário autenticado, e **lança** em vez de cair em default silencioso quando não há escopo — um default aqui seria vazamento entre clientes. Para `PowerUser` a rede é escolhida no seletor do cabeçalho (legítimo, ele é autorizado em todas); para usuário operacional vem do cadastro e é imutável na sessão. Os 4 API clients passaram a obter o `redeId` daí.
  - **Login** por form post para `SignInManager` (`/api/auth/login`), não de dentro do componente: o cookie precisa ser escrito na resposta HTTP. Bloqueio após 5 tentativas; usuário inativo é barrado antes da senha.
  - **Bootstrap** cria papéis e o PowerUser inicial, e **falha ruidosamente** se `poweruser-password` não estiver configurado — sem senha default numa aplicação que recebe dado comercial de terceiros. Idempotente: se o usuário existe, não mexe (senão a senha trocada voltaria ao valor do parâmetro a cada F5).
  - **UI:** `/admin/redes` e `/admin/usuarios` com `[Authorize(Roles = PowerUser)]`; menu administrativo dentro de `AuthorizeView` (cosmético — o controle real é o atributo na página); `[Authorize]` nas 5 páginas de dados; `AuthorizeRouteView` distingue anônimo (redireciona) de autenticado sem permissão (mostra "Acesso negado"). Redes e usuários são **desativados**, nunca excluídos, para preservar o histórico de cargas e jobs.
  - Acesso ao banco nas páginas via escopo por operação, não `DbContext` injetado: em Blazor Server um serviço scoped vive o circuito inteiro e dois cliques estouram *"A second operation was started on this context"*.
- [x] **F12 — Captura da Sugestão de Compra do PBS**: `SUGESTOES_COMPRAS` + `SUGESTOES_COMPRAS_RESULTADO` → Stage. Tabelas `dbo.SugestoesCompra` (cabeçalho, com `TipoCalculo` **1 = `Emax e Eseg`** / **2 = `Dias de Reposição`**, `DiasCurvaA..E`, `Efetividade`, `ConsideraPedidosPendentes`) e `dbo.SugestoesCompraItens` (`DemandaDia` — a **própria previsão do ERP** —, `EstoqueMaximo`, `EstoqueSeguranca`, `CompraSugerida`, `CompraAutorizada`, `PrecoCompra`, `FatorEmbalagem`, `Falteiro`, `Curva`), ambas com `RedeId` na PK; o cabeçalho referencia só `Redes`, e é a tabela de itens que leva as FKs **compostas** `(RedeId, Sku)` → `Produtos` e `(RedeId, LojaId)` → `Lojas`, além de `(RedeId, SugestaoId)` → o cabeçalho. Import do Worker e `ImportSchemas` cobrem as duas CSVs novas (opcionais no ZIP, para o fluxo sintético continuar valendo). O lado do extrator — catálogo de sugestões, escolha de **uma** sugestão, query de diagnóstico `EMPRESA <> FILIAL` e `manifesto.json` no ZIP — saiu nas Tasks 1–4 da **F14**, que substituíram as Tasks 3–4 do plano desta fase. Mapeamento PBS → Stage documentado em [Docs/extracao-pbs-stage.md](Docs/extracao-pbs-stage.md).
- [x] **F13 — Comparação ML vs PBS**: o baseline do TCC passa a ser o **ERP real**, não cópia nossa. `EMaxESegPolicy` foi **apagada** (a `/sugestao-compra` ficou com uma política só, ver nota na F8). Três camadas, medindo coisas diferentes e nunca somadas. Protocolo completo, com as duas regras e as limitações, em [Docs/04 — Avaliação e métricas](Docs/04-avaliacao-metricas.md#comparativo-erp).
  - **Camada A — previsão × previsão (`ForecastVsErpComparer`, resultado de manchete):** `SugestoesCompraItens.DemandaDia` contra a previsão do LightGBM, ambas julgadas pela mesma venda real, em unidades/dia. Métricas `ForecastMetrics` reusadas do backtest + `WinRate` par a par, globais e por `Categoria`/`ClasseAbc`/`Loja`/`UF`/**`CurvaErp`**. Rotula `UnidadeMetrica.ErroPorParNaJanela` — promediar a janela encolhe a variância, então este MAE **não** é comparável com o do walk-forward de F7.
  - **Camada B — decisão × decisão (`DecisionComparer`):** troca só a demanda dentro da aritmética do próprio ERP (mesmo saldo, pendentes, `DiasEstoque`, `FatorEmbalagem`). Portão de validade: recalcula `CompraSugerida` a partir do `DemandaDia` gravado antes de comparar qualquer coisa. **Não compara nada hoje** — a cobertura do PBS é de 15–30 dias e o horizonte do pipeline é 7, então os itens caem em `ForaDoHorizonteMl` e o resultado sai com `Utilidade = ForaDoHorizonteMl`. Zero comparações não é empate, e a UI é obrigada a ler esse campo antes de qualquer número.
  - **Camada C — intervenção humana (`HumanOverrideReport`):** distância entre `CompraSugerida` e `CompraAutorizada`, em base não ponderada e ponderada por valor, com vetos/adições/ajustes separados e recorte por curva. **Descritiva** — mede que o comprador discordou, não quem estava certo.
  - **Duas regras verificadas em código:** *população* (só entram os pares `SugestaoId`/`LojaId`/`Sku` que o ERP avaliou; cada motivo de exclusão tem contador próprio) e *informação* (o ML só usa dado estritamente anterior a `SugestoesCompra.DataHora`), esta última aplicada em três pontos — `TreinoJob.TreinoAte` + filtro em toda consulta datada do `StageObservationLoader`, `FeatureConfig.PrecoCongeladoAPartirDe`, e validação por item nos dois comparadores.
  - **Ruptura:** default `ExcluirPar` (descarta o par inteiro). `ExcluirDia` é só sensibilidade na camada A e **recusado** na B — o ML se reprojeta sobre os dias sobreviventes e o ERP, que entrega um escalar único, não.
  - **Infra:** entity `ComparacaoPbs` + migration, `ComparacaoWorker`/`ComparacaoProcessor` (3 contratos anti-vazamento aplicados), `StageSugestaoLoader`, endpoints `POST /api/comparison/run`, `GET /api/comparison`, `GET /api/comparison/{id}`, e a tela técnica `/comparacao` (abas por `TipoCalculo`, portão de reconciliação antes dos números, contadores de exclusão em linhas distintas, ressalva treino/serviço no topo dos resultados).
  - **Ressalvas que viajam no `ResultadoJson`:** `ComparacaoOutput.RessalvaPadraoTreinoServe` documenta os dois desvios treino/serviço (preço congelado; classe ABC e orçamento de SKUs recalculados com o corte da sugestão). Ambos só podem prejudicar o braço de ML, nunca inflá-lo.
  - **Pergunta aberta antes de qualquer conclusão:** se o `DemandaDia` do PBS já é corrigido por ruptura. Verificar contra o campo `Falteiro` — se o ERP usa venda bruta e a nossa verdade exclui dias de ruptura, a comparação pende na direção que agrada à hipótese.
- [x] **F14 — Sessões de comparação**: fluxo guiado de ponta a ponta para o comprador da rede. Ele cria uma **sessão** no painel `/`, baixa o extrator, escolhe **uma** sugestão de compra no PBS, sobe o ZIP, e a sessão anda sozinha — importar → treinar → comparar → concluir — até uma tela (`/comparacoes/{id}`) que diz, em linguagem de negócio, quanto capital cada método deixaria parado na prateleira. Plano em [Docs/superpowers/plans/2026-07-28-f14-sessoes-de-comparacao.md](Docs/superpowers/plans/2026-07-28-f14-sessoes-de-comparacao.md), spec em [Docs/superpowers/specs/2026-07-28-sessoes-de-comparacao-design.md](Docs/superpowers/specs/2026-07-28-sessoes-de-comparacao-design.md). As **Tasks 3–4 do plano da F12** foram substituídas por esta fase — a UX do extrator passou a ser "uma sugestão, janelas derivadas".
  - **Extrator escolhe a sugestão, e o ZIP declara qual foi.** `Queries/catalogo_sugestoes.sql` lista as sugestões disponíveis no PBS; `ExtractionWindow.Derive(dataSugestao, diasCobertura, hoje)` deriva o período do ZIP — **12 meses antes** da sugestão (o pipeline de features exige 34 dias, mas 34 dias não pegam sazonalidade) e **T + dias de cobertura depois** dela, que é o único período capaz de revelar quem acertou. Sugestão cuja cobertura ainda não terminou — ou que não declara cobertura nenhuma — é recusada no próprio extrator (`Viavel = false`), com a data-limite na mensagem. **Cobertura acima do horizonte do ML não é mais recusa, e sim ressalva** (`ExtractionWindow.Ressalva`, semáforo âmbar, aviso no log e na confirmação da extração): a recusa partia de "a comparação sairia vazia", o que não é verdade — a camada A pontua `min(cobertura, lead time)` dias e produz número inteiro; só a camada B cai, e ela já tem contador e texto de comprador próprios. Como o ciclo de reposição corrente do PBS é de 15 a 30 dias, aquela recusa impedia uma rede real de exportar **qualquer** sugestão. O ZIP leva `manifesto.json` (`ZipManifest`: `SugestaoId`, `SugestaoDescricao`, `SugestaoDataHora`, `SugestaoTipoCalculo`, `JanelaInicio`/`JanelaFim`, `VersaoExtractor`, `SkusSemCadastro`) — a web **não pergunta nada disso ao usuário**, porque só o extrator tem acesso ao PBS. `SkusSemCadastro` conta os SKUs citados pela sugestão que entraram em `produtos.csv` como placeholder (sem cadastro em `PRODUTOS`), obrigatórios para o `SqlBulkCopy` não estourar FK.
  - **Duas tabelas novas em `engine`** — `ComparacaoSessoes` e `ComparacaoSessaoItens` (migrations `AddComparacaoSessoes`, `AddMaterializacaoDaSessao`, `AddQualificadoresDoItemDaSessao`). Máquina de estados de 7 fases em `ComparacaoSessao.PodeTransicionar` (`AguardandoDados`, `ProcessandoDados`, `Treinando`, `Comparando`, `Concluida`, `Inviavel`, `Falha`); só `Inviavel`/`Falha` voltam a `AguardandoDados`, e só por reenvio do usuário. Contrato das duas tabelas e as regras que as sustentam em [CLAUDE.md §4](CLAUDE.md).
  - **`SessaoWorker`**: um `BackgroundService` novo com o mesmo claim `UPDLOCK/READPAST` das outras filas, que a cada volta observa o job da fase corrente e, quando ele conclui, cria o da fase seguinte. `ImportWorker`, `TreinoWorker` e `ComparacaoWorker` seguem **sem saber que sessões existem** — a ordem das fases mora num único lugar. A decisão de avanço é pura (`SessaoAvancador.ProximoEstado`) e os dois parâmetros derivados dos jobs também (`SessaoJobs.Treino`/`Comparacao`), porque errar neles não dá erro: produz um treino que roda liso e uma comparação recusada três fases depois. Corte de treino = **o próprio dia da sugestão** (o loader aplica `Data < @treinoAte`); orçamento de SKUs = **nenhum** (`MaxSkus = null`): o treino carrega o catálogo inteiro da rede. Houve um `Math.Clamp(skusDistintos, 80, 1000)` aqui, e o teto vinha do limite de 2100 parâmetros por comando do SQL Server, que o `Sku IN (@s0…@sN)` do loader gastava um por SKU. Era limite de implementação, não de modelagem, e custou caro: na primeira sugestão real (Retiro) o teto descartou **54% dos itens** para `ItensForaOrcamentoSkus` e treinou o modelo só na fatia densa do catálogo — skew de treino/serviço que aparece como um modelo incapaz de prever perto de zero. `EscopoDeSkus` trocou o `IN` por join com tabela temporária, que não tem teto; o parâmetro continua existindo (nulo = todos) para experimento e teste.
  - **Nenhuma fila tem lease ou heartbeat**, então um worker que morre deixaria a sessão girando para sempre. `SessaoJobs.FaseAbandonada` encerra a sessão depois de `ComparacaoSessao.LimiteDeFaseSemProgresso` (2h) sem sinal de progresso, com mensagem de comprador em vez de spinner eterno — e o **mesmo** relógio solta o bloqueio de sessão concorrente, para quem libera e quem mata não discordarem.
  - **Inviabilidade ≠ falha.** `ManifestoLeitor` recusa envio sem `manifesto.json`, com manifesto danificado, que termina **antes** do dia da sugestão (sem gabarito) ou que começa **no** dia dela ou depois (sem histórico); `SessaoJobs` recusa sessão sem declaração e ZIP com mais de uma sugestão no mesmo dia+método. Todos viram `Inviavel` com texto de comprador em `MotivoInviabilidade` — nenhum lança exceção, porque `Falha` mandaria o comprador "tentar de novo" um arquivo que nunca vai servir.
  - **Resultado materializado, nunca recalculado.** `SessaoResultadoMaterializador` grava, na última volta em que a sessão ainda está em `Comparando`, uma linha por item em `ComparacaoSessaoItens` (`SqlBulkCopy`) e os agregados da manchete em `ComparacaoSessao.ResultadoJson` — **`DELETE` + bulk + `UPDATE Status='Concluida'` na mesma transação**, com o `WHERE Status = <fase reclamada>` que impede materialização em dobro. O montador (`SessaoResultadoMontador`) é puro. Ver a regra e o motivo em CLAUDE.md §4.
  - **Tela do comprador** (`Sessao.razor`, polling de 3s que para em estado terminal): `ManchetesComparacao` com duas colunas — "Pelo PBS" (o que de fato foi comprado, sobre a população inteira) e "Como teria sido pelo ML" (só sobre os itens em que **os dois** braços existem, com a frase "compare com X, não com o número da coluna ao lado"). `OndeOMlFoiPior` fica **fora das abas**, acima delas: a notícia ruim não pode depender de um clique. Abas "Itens comparados" (`TabelaItensComparacao`, paginada e ordenada no servidor por whitelist — `OrdemItensSessao`) e "Área técnica" (`AreaTecnicaComparacao`, `GET /api/comparacoes/{id}/analise`: previsão × previsão por curva e por loja, com numerador e denominador visíveis em vez de um WAPE que parece falar da população inteira).
  - **Uma sessão em voo por rede**, bloqueada no envio de dados e não na criação (`ComparacoesEndpoints.SessaoConcorrenteAsync`) — ver CLAUDE.md §4. Endpoints: `POST /api/comparacoes`, `GET /api/comparacoes`, `GET /api/comparacoes/{id}`, `POST /api/comparacoes/{id}/dados`, `GET /api/comparacoes/{id}/itens`, `GET /api/comparacoes/{id}/analise`.
  - **Download do extrator via MinIO**: `GET /api/extrator/download` (stream do bucket `extrator`, nunca materializa o `.exe` inteiro em memória) e `GET /api/extrator/versao` (versão + checksum SHA-256, lidos do `manifesto.json` publicado ao lado do executável — calculado uma vez na publicação, não a cada download). 404 claro e distinto de falha de infraestrutura quando nada foi publicado ainda. Botão "Baixar extrator" na página da sessão, só em `AguardandoDados`. Sem `redeId` em nenhum dos dois — o executável não é dado de inquilino. Passo a passo de publicação em [§ Publicar o extrator no MinIO](#publicar-o-extrator-no-minio) acima.
  - **Limitações declaradas, na tela e aqui** (todas encontradas durante a implementação, nenhuma resolvida):
    - **A compra do braço de ML não é calculável hoje.** O pipeline prevê 7 dias (`DecisionOptions.HorizonteMaximoMl`) e a cobertura do PBS é de 15 a 30, então a camada B cai em `ForaDoHorizonteMl` e a tela mostra a coluna do ERP com uma explicação em português de por que a coluna de ML não existe (`SessaoResultado.MotivoMlIndisponivel`). As colunas do braço de ML são **anuláveis de propósito**: nulo é "não foi possível calcular", nunca "o ML disse zero". Estender exige previsão multi-horizonte — o mesmo bloqueio da F9.
    - **As figuras em R$ excluem itens sem `PrecoCompra`**, que entram nos agregados com unidades e zero em reais. `SessaoResultado.ItensSemPrecoCompra` viaja ao lado do número e a manchete o exibe ("este valor em reais está incompleto"), para o total não ser lido como completo.
    - **Linhas cuja cobertura passa do fim do histórico importado** têm venda subcontada e sobra inflada. Marcadas **por linha** em `ComparacaoSessaoItem.JanelaAlemDoHistorico` (e contadas em `ItensComJanelaAlemDoHistorico`), porque não são comparáveis com as demais — o dado que permite descobrir isso vive no Stage, que o próximo import apaga.
    - **O portão de reconciliação nunca rodou contra uma sugestão real do PBS.** Taxa de acordo baixa significa "não modelamos a aritmética do ERP", **não** "o ML venceu".
    - **Duas perguntas abertas**, ambas dependentes de informação fora do código: se o `DemandaDia` do PBS já é corrigido por ruptura (verificável contra o campo `Falteiro` — se o ERP usa venda bruta e a nossa verdade exclui dias de ruptura, o viés pende para o lado que agrada à hipótese, que é a pior direção possível) e qual dos dois métodos de cálculo a rede de fato opera.
    - **Corrida residual:** o bloqueio de sessão concorrente é check-then-act, então dois envios **simultâneos** a duas sessões da mesma rede ainda podem se intercalar. O caso sequencial está fechado; fechar o simultâneo exige índice único filtrado (migration) ou `sp_getapplock`.
  - **A materialização não conclui mais a sessão** — desde a F15 ela grava `AguardandoQuestionario`, e a última transição passou a ser feita pelo endpoint de envio do questionário. O resto do parágrafo acima continua valendo palavra por palavra: mesma transação, mesmo `WHERE Status = <fase reclamada>`.
  - **Reorganização da navegação**: o painel de **Comparações** virou a raiz (`/`) — antes a home era a tela de importação. `NavMenu` passou a ter três blocos: "Comparações" (`/`), "Administração" (`/admin/redes`, `/admin/usuarios`, só `PowerUser`) e um grupo **"Técnico"** recolhido com as etapas de engenharia — `/tecnico/importar`, `/dados`, `/treinamento`, `/sugestao-compra`, `/comparacao`. As telas antigas `/importar` e `/jobs` foram **fundidas** em `/tecnico/importar` (upload + "Cargas recentes" na mesma página). Esconder item de menu continua sendo cosmético: o controle real é o `[Authorize]` da página.
- [ ] **F15 — Questionário do comprador**: a avaliação subjetiva de cada comparação levada até o fim, que é o dado de campo do TCC. **O questionário é a última fase da sessão, não um anexo dela**: `Comparando → AguardandoQuestionario → Concluida`, e `Concluida` passou a significar "o comprador respondeu".
  - **A única transição da máquina de estados que não sai do Worker.** `AguardandoQuestionario` está fora do allowlist do `SessaoWorker.ClaimNextAsync` (`WHERE Status IN ('ProcessandoDados','Treinando','Comparando')`), então nenhuma fila a reclama e o relógio de `LimiteDeFaseSemProgresso` não a alcança — o que é o comportamento certo, porque o que falta é um humano, e 2h matariam uma sessão esperando o comprador voltar do almoço. Quem grava `Concluida` é `POST /api/comparacoes/{id}/questionario/enviar`. Pelo mesmo motivo ela também **não** conta como sessão viva no bloqueio por rede: o resultado já está materializado, e um ZIP novo não o alcança mais.
  - **`PodeExcluir` ganhou uma segunda recusa, por motivo diferente da primeira.** As três fases de worker recusam para proteger o *job*; `Concluida` recusa para proteger o *dado* — resposta de pesquisa não evapora por clique. A equivalência "concluída ⟺ respondida" é o que a migration garante ao reclassificar as sessões que já estavam concluídas para `AguardandoQuestionario`; sem aquele `UPDATE` elas ficariam inexcluíveis para sempre sem nunca ter sido avaliadas.
  - **Duas tabelas novas em `engine`** (migration `AddQuestionarios`): `Questionarios` (cabeçalho, único por `SessaoId`) e `QuestionarioRespostas` (PK `(QuestionarioId, PerguntaCodigo)`, sem `RedeId` — escopo transitivo pela FK). O cabeçalho **não tem coluna de situação**: a autoridade é o status da sessão, e um `Status` aqui repetiria a mesma verdade em dois lugares que podem divergir. Contrato em [CLAUDE.md §4](CLAUDE.md).
  - **O instrumento é o Apêndice A da dissertação** (MGSI, ISCTE-IUL), `Versao = 2`: **Parte A** com A1 (função), A2 (anos na área, ordinal) e A3 (a organização já usa sistema de previsão); **Parte B** com sete afirmações (B1–B7) em escala de Likert 1–5. Texto **literal, em português europeu** — "artefacto", "stocks", "ruturas" —, deliberadamente não traduzido para o pt-BR do resto da aplicação: reescrever enunciado invalida a comparação com o que foi submetido. A apresentação e o termo de consentimento aparecem **fora do wizard**, acima do primeiro passo, porque é o que o participante concorda antes de responder.
    - **A4 do documento ("Qual o ERP utilizado pela sua organização?") foi deliberadamente omitida**, e a decisão está registrada em comentário onde ela caberia, para não ser "restaurada" como se fosse esquecimento. O extrator lê exclusivamente o PBS — o mapeamento inteiro do Stage é PBS → Stage —, então a resposta é constante por construção e perguntá-la gastaria o tempo do participante para produzir uma coluna de um único valor. A análise preenche A4 a partir da própria importação. Se um segundo ERP entrar, o lugar de gravar isso é o cadastro da rede ou o manifesto do ZIP, não uma pergunta.
    - **Pendência de consentimento, não de código:** o texto afirma que "não será recolhida qualquer informação que permita identificar os participantes", mas `Questionarios.UsuarioId` grava quem respondeu, e a sessão amarra a resposta a uma rede. Ou o `UsuarioId` sai, ou o texto muda — decisão de quem conduz a pesquisa. Hoje a coluna existe e é preenchida.
  - **Catálogo em código** (`Engine/Questionarios/QuestionarioCatalogo.cs`), com `Versao` incrementada à mão — o instrumento é fixo e revisado por quem conduz a pesquisa, e CRUD de perguntas seria abstração que o requisito não pede. O preço é pago em `QuestionarioRespostas`, que grava **o texto exibido** da pergunta e da opção junto com a resposta: o catálogo muda com deploy, e sem o retrato um ajuste de redação reescreveria retroativamente o que foi perguntado. `OpcaoValor` guarda a posição na escala quando a pergunta é ordinal, para tabular sem parsear português; nulo ali é "não é ordinal", nunca "grau zero".
  - **Rascunho e envio são coisas diferentes.** `PUT .../questionario` grava parcial (é o que permite fechar o navegador no meio sem perder o preenchido) e substitui o conjunto inteiro — a tela manda o estado completo do wizard, então pergunta ausente do corpo significa "sem resposta", não "mantenha a antiga". `POST .../enviar` exige completude, sela `EnviadoEm`, copia `ItensComDecisaoMl`/`TotalDeItens` e move a sessão, **tudo na mesma transação**, com o `WHERE Status = 'AguardandoQuestionario'` no `UPDATE` final como guarda contra dois envios simultâneos. Depois de selado é imutável.
  - **Os dois contadores existem por causa da limitação de horizonte.** Uma resposta dada sobre uma tela em que a coluna do ML está vazia não é comparável com uma dada sobre a tela cheia, e sem gravar isso no envio as duas populações ficam misturadas e **irrecuperáveis** — o Stage é apagado no import seguinte e a sessão pode ser excluída antes da análise.
  - **Tela**: `/comparacoes/{id}/questionario` com `RadzenSteps` (uma seção = um passo), `RadzenRadioButtonList` vertical e campo livre condicional nas opções marcadas. A guarda de completude vive nos **dois** caminhos de navegação — o botão "Próximo" e o `CanChange`, que cobre o clique direto no cabeçalho do passo. Chamada acima das abas em `Sessao.razor` (a pendência não pode ficar atrás de um clique numa aba) e resumo por linha no painel `/`.
  - **Duas armadilhas encontradas na implementação**, ambas invisíveis ao compilador e registradas em comentário no código:
    - **`[FromQuery] Guid usuarioId = default` derruba a aplicação inteira.** O compilador não grava constante de `Guid` em metadata, então o default chega à reflexão como `null` e o `RequestDelegateFactory` monta `Expression.Constant(null, typeof(Guid))`. Como a tabela de rotas é construída **inteira** na primeira requisição, um handler inválido derruba *todos* os endpoints — inclusive `/health`, que passa a dar 500, o `apiservice` nunca fica saudável e o AppHost não sobe. Se algum chamador puder não ter usuário, use `Guid?`; nunca `= default`.
    - **Transação EF sob estratégia de retry.** `AddSqlServerDbContext` (Aspire) liga `EnableRetryOnFailure`, e o EF **recusa** transação iniciada pelo usuário nesse modo. As outras duas transações do repositório escapam por abrirem `SqlConnection` própria; aqui o trabalho é de entidade, então a escrita vai dentro de `CreateExecutionStrategy().ExecuteAsync`, com `ChangeTracker.Clear()` na entrada — sem ele, uma retentativa herda as entidades da tentativa que rolou atrás e morre no índice único, virando permanente uma falha transitória.
  - **Falta**: resolver a pendência de consentimento acima e o export tabulado para a análise (`GET /api/questionarios/export`, XLSX via `ClosedXML`, restrito a `PowerUser`).

---

## 7. Referências consultadas

- Documentação atual ML.NET (`/dotnet/machinelearning`) via Context7 — confirma SSA, FastTree, LightGBM e AutoML mantidos.
- Repositório `dotnet/machinelearning` (ativo).
- M5 Forecasting Competition (Walmart / Kaggle) — referência da abordagem GBM + features para retail.
