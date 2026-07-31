# CosmosPro.ML.DemandForCast (POC)

POC de um **engine de previsão de demanda** para apoiar o processo de **sugestão de compra** no varejo farmacêutico. Construído sobre .NET 10 + .NET Aspire, com fontes de dados em **SQL Server** e/ou **ClickHouse**.

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

A cada `F5` o AppHost sobe **todos** os recursos lado a lado:

```
┌───────────────────────────────────────────────────────────────────┐
│ CosmosPro.ML.DemandForCast.AppHost (Aspire 13.3.1)                │
│                                                                   │
│  sql (SQL Server 2022 container, persistent volume)               │
│   ├─ vendas       ← schema deployado via DACPAC (SQL Project)     │
│   └─ engine       ← schema gerenciado via EF Core migrations (F2) │
│                                                                   │
│  clickhouse (ClickHouse server container, persistent volume)      │
│   └─ vendas-olap  ← histórico denso para varredura analítica      │
│                                                                   │
│  dbgate (UI web de inspeção, volume persistente)                  │
│   ├─ conexão "sql"        ← auto-wire (SqlServer.Extensions)      │
│   └─ conexão "clickhouse" ← auto-wire (helper local, ver §abaixo) │
│                                                                   │
│  vendas-schema (one-shot)                                         │
│   └─ publica DACPAC do projeto Database no banco "vendas"         │
│                                                                   │
│  apiservice  ← .WaitFor(vendas, engine, vendas-olap)              │
│              ← .WaitForCompletion(vendas-schema)                  │
│                                                                   │
│  webfrontend ← .WaitFor(apiservice)                               │
│                                                                   │
│  [futuro] forecast-py — sidecar opcional para Croston/TSB         │
└───────────────────────────────────────────────────────────────────┘
```

Persistência: `WithLifetime(ContainerLifetime.Persistent)` + `WithDataVolume()` em SQL Server e ClickHouse. Os containers sobrevivem ao encerramento do AppHost; os volumes nomeados sobrevivem à recriação dos containers. Reset completo de dados exige `docker volume rm` explícito.

Projetos da solução (`MLDemandForCastPOC.slnx`):

| Projeto | Papel |
|---|---|
| [CosmosPro.ML.DemandForCast.AppHost](CosmosPro.ML.DemandForCast.AppHost/) | Orquestração Aspire. Declara recursos (DBs, serviços) e dependências. |
| [CosmosPro.ML.DemandForCast.ApiService](CosmosPro.ML.DemandForCast.ApiService/) | HTTP API que expõe treino, forecast e métricas. Hospeda o engine ML.NET. |
| [CosmosPro.ML.DemandForCast.Web](CosmosPro.ML.DemandForCast.Web/) | Blazor — UI de cockpit: disparar experimentos, inspecionar backtests, comparar modelos. |
| [CosmosPro.ML.DemandForCast.Database](CosmosPro.ML.DemandForCast.Database/) | SQL Server Project (`MSBuild.Sdk.SqlProj/4.2.0`). Schema declarativo do banco `vendas`, deployado via DACPAC a cada F5. |
| [CosmosPro.ML.DemandForCast.OlapSchema](CosmosPro.ML.DemandForCast.OlapSchema/) | Console .NET one-shot. Aplica scripts SQL versionados (embedded em `Scripts/*.sql`) ao banco `vendas-olap` no ClickHouse. Controle de versão via tabela `__schema_migrations`. |
| [CosmosPro.ML.DemandForCast.ServiceDefaults](CosmosPro.ML.DemandForCast.ServiceDefaults/) | OpenTelemetry, health checks, resilience. |

### Por que duas trilhas de migração de schema

- **DACPAC (`Microsoft.Build.Sql` / `MSBuild.Sdk.SqlProj`)** para o banco `vendas` — schema **declarativo**, ideal para representar a fonte transacional consumida pelo engine. SqlPackage faz o diff e aplica ALTERs; ganhamos histórico de schema versionável, refactoring com detecção de rename, e scripts pre/post-deployment. Aplicado pelo Aspire via `AddSqlProject<Projects.X>(...)` (pacote `CommunityToolkit.Aspire.Hosting.SqlDatabaseProjects`).
- **EF Core migrations** para o banco `engine` — schema **imperativo**, ideal para tabelas próprias do engine (`Experimento`, `BacktestRun`, `ModelArtifactRegistry`). Aplicado pelo Aspire via `AddEFMigrations(...)` + `RunDatabaseUpdateOnStart()` (pacote `Aspire.Hosting.EntityFrameworkCore`, anunciado no changelog 13.3 do Aspire mas ainda não publicado no NuGet — placeholder marcado no `AppHost.cs`).
- **Runner customizado (.NET console one-shot)** para `vendas-olap` no ClickHouse — ClickHouse não tem DACPAC equivalente. O projeto `CosmosPro.ML.DemandForCast.OlapSchema` carrega scripts `.sql` versionados (embedded em `Scripts/`), mantém tabela `__schema_migrations` no próprio ClickHouse, e skipa scripts já aplicados. Aplicado pelo Aspire como um `AddProject<...>(...)` regular com `WithReference(vendasOlapDb)` — apiservice usa `WaitForCompletion(olapSchema)`. Convenção de nomes: `NNN_descricao.sql` (versão = nome sem extensão). **Detalhes completos: [Docs/olap-schema-migrations.md](Docs/olap-schema-migrations.md)**.

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
- **ClickHouse** — analítico (histórico denso de vendas, ideal para varrer milhões de séries SKU×loja).

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
- Docker Desktop (para recursos do Aspire — SQL Server, ClickHouse)
- Aspire workload: `dotnet workload install aspire`

### Executar
```powershell
dotnet run --project .\CosmosPro.ML.DemandForCast.AppHost\
```

Aspire Dashboard abre automaticamente; webfrontend, apiservice e DbGate ficam acessíveis pelos endpoints listados.

### Inspecionar bancos com DbGate

DbGate aparece como recurso `dbgate` no dashboard. Abra o endpoint — **as duas conexões já vêm prontas**:

- **SQL Server (`sql`)** — auto-wirada pelo `WithDbGate()` do `CommunityToolkit.Aspire.Hosting.SqlServer.Extensions`.
- **ClickHouse (`clickhouse`)** — auto-wirada por um helper local em [ClickHouseDbGateExtensions.cs](CosmosPro.ML.DemandForCast.AppHost/ClickHouseDbGateExtensions.cs), que cobre a lacuna do `Aspire.Hosting.ClickHouse` (que ainda não traz `WithDbGate()` nativamente — candidato a PR upstream em `ClickHouse/ClickHouse.Aspire`).

O DbGate roda com `WithDataVolume() + WithLifetime(Persistent)`, então qualquer favorito/aba salvo na UI sobrevive entre F5s.

---

## 6. Roadmap do POC

- [x] **F0 — Fundação**: README, CLAUDE.md, decisão arquitetural.
- [x] **F1 — Orquestração**: AppHost sobe SQL Server (persistente) + ClickHouse (persistente) + SQL Project DACPAC a cada F5. Bancos `vendas`, `engine`, `vendas-olap` declarados. Schema bootstrap funcional.
- [ ] **F2 — Dados & schema**:
  - [x] **F2.1** — Schema do banco `Stage` (renomeado de `vendas`) no SQL Project: `Lojas`, `Produtos`, `Vendas`, `EstoquesDiarios`, `Compras`, `Promocoes`, `MercadoIqvia` (todos sob `dbo`, plural, com FKs/IXs/CKs; aplicado via DACPAC ~28s, validado via MCP). MinIO adicionado ao AppHost para armazenar ZIPs de import.
  - [ ] **F2.2** — Templates de planilha + UI de importação no Blazor.
  - [ ] **F2.3** — Dataset sintético farma para semear.
  - [x] **F2.4** — Projeto `Engine` (class library) com `EngineDbContext` + entidade `CargaStage` + EF Core migrations aplicadas via `Aspire.Hosting.EntityFrameworkCore.AddEFMigrations` (prerelease 13.3.4-preview). Tabela `engine.CargasStage` validada via MCP.
  - [x] **F2.5** — API endpoint `POST /api/imports/upload` (valida ZIP estrutural + headers das CSVs, upload MinIO bucket `imports/`, INSERT `CargasStage` com `Status=Pendente`, retorna 202 + Id). GET `/api/imports/{id}` retorna o estado da carga. Validado end-to-end via curl: happy path 202 + erros estruturais 400.
  - [x] **F2.6** — Blazor UI: `/importar` (form upload ZIP via `<InputFile>`) + `/jobs` (lista com polling 3s). Endpoint `GET /api/imports?take=N` adicionado para alimentar a listagem.
  - [x] **F2.7** — Worker (`CosmosPro.ML.DemandForCast.Worker`, BackgroundService). Claim com `;WITH cte AS (... WITH (UPDLOCK, READPAST) ...) UPDATE cte SET Status='Processando' OUTPUT INSERTED.*`. Download ZIP do MinIO + extract → DataTable tipada via `TableSchemas` → `DELETE FROM` (ordem reversa de FK) + `SqlBulkCopy` (ordem de FK) tudo em transação única. UPDATE final via EF Core `ExecuteUpdateAsync`. Validado end-to-end: ZIP com 18 linhas → Status=Concluida + LinhasImportadas=18 + tabelas Stage com contagem exata.
- [x] **F3 — Testes** (vai antes do resto pra evitar regressão silenciosa):
  - [x] **F3.1** — `tests/Tests.Shared` com Bogus fakers (um arquivo por entidade — `LojaFaker`, `ProdutoFaker`, `VendaFaker`, `EstoqueDiarioFaker`, `CompraFaker`, `PromocaoFaker`, `MercadoIqviaFaker`) + `CsvZipBuilder` (gera ZIP em memória com 7 CSVs).
  - [x] **F3.2** — Unit tests por projeto: `Engine.Tests` (4), `OlapSchema.Tests` (5), `ApiService.Tests` (4), `Worker.Tests` (16), `Web.Tests` (9) — xUnit v3 + FluentAssertions + NSubstitute. 38/38 verdes.
  - [x] **F3.3** — `ApiService.IntegrationTests` — Aspire.Hosting.Testing 13.3.4 + Refit 10.1.6 no Act. 3 cenários (upload happy path 202 + listagem, ZIP incompleto 400, GET inexistente 404). Workaround obrigatório no fixture para `SqlProjectResource` (remover anotação `IProjectMetadata` + chamar `WithDacpac(absolutePath)` apontando para o DACPAC buildado), porque o evaluation MSBuild falha sob `dotnet test`. 3/3 verdes.
  - [x] **F3.4** — `Web.E2ETests` — Aspire.Hosting.Testing + Playwright 1.59 no Act. Cenário: navegar para `/importar`, subir ZIP gerado por fakers, ver alerta verde, navegar para `/jobs`, confirmar linha. Localiza linha por filename (não por GUID prefix — UUIDv7 compartilha 8 chars iniciais entre cargas próximas no tempo). 1/1 verde.
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
- [x] **F12 — Captura da Sugestão de Compra do PBS**: `SUGESTOES_COMPRAS` + `SUGESTOES_COMPRAS_RESULTADO` → Stage. Tabelas `dbo.SugestoesCompra` (cabeçalho, com `TipoCalculo` **1 = `Emax e Eseg`** / **2 = `Dias de Reposição`**, `DiasCurvaA..E`, `Efetividade`, `ConsideraPedidosPendentes`) e `dbo.SugestoesCompraItens` (`DemandaDia` — a **própria previsão do ERP** —, `EstoqueMaximo`, `EstoqueSeguranca`, `CompraSugerida`, `CompraAutorizada`, `PrecoCompra`, `FatorEmbalagem`, `Falteiro`, `Curva`), ambas com `RedeId` na PK e FK composta para `Produtos`/`Lojas`. Extrator (`CosmosPro.ML.DemandForCast.Extractor`) ganhou catálogo de sugestões, escolha da sugestão a extrair, query de diagnóstico e manifesto no ZIP; import do Worker e `ImportSchemas` cobrem as duas CSVs novas. Mapeamento PBS → Stage documentado em [Docs/extracao-pbs-stage.md](Docs/extracao-pbs-stage.md).
- [x] **F13 — Comparação ML vs PBS**: o baseline do TCC passa a ser o **ERP real**, não cópia nossa. `EMaxESegPolicy` foi **apagada** (a `/sugestao-compra` ficou com uma política só, ver nota na F8). Três camadas, medindo coisas diferentes e nunca somadas. Protocolo completo, com as duas regras e as limitações, em [Docs/04 — Avaliação e métricas](Docs/04-avaliacao-metricas.md#comparativo-erp).
  - **Camada A — previsão × previsão (`ForecastVsErpComparer`, resultado de manchete):** `SugestoesCompraItens.DemandaDia` contra a previsão do LightGBM, ambas julgadas pela mesma venda real, em unidades/dia. Métricas `ForecastMetrics` reusadas do backtest + `WinRate` par a par, globais e por `Categoria`/`ClasseAbc`/`Loja`/`UF`/**`CurvaErp`**. Rotula `UnidadeMetrica.ErroPorParNaJanela` — promediar a janela encolhe a variância, então este MAE **não** é comparável com o do walk-forward de F7.
  - **Camada B — decisão × decisão (`DecisionComparer`):** troca só a demanda dentro da aritmética do próprio ERP (mesmo saldo, pendentes, `DiasEstoque`, `FatorEmbalagem`). Portão de validade: recalcula `CompraSugerida` a partir do `DemandaDia` gravado antes de comparar qualquer coisa. **Não compara nada hoje** — a cobertura do PBS é de 15–30 dias e o horizonte do pipeline é 7, então os itens caem em `ForaDoHorizonteMl` e o resultado sai com `Utilidade = ForaDoHorizonteMl`. Zero comparações não é empate, e a UI é obrigada a ler esse campo antes de qualquer número.
  - **Camada C — intervenção humana (`HumanOverrideReport`):** distância entre `CompraSugerida` e `CompraAutorizada`, em base não ponderada e ponderada por valor, com vetos/adições/ajustes separados e recorte por curva. **Descritiva** — mede que o comprador discordou, não quem estava certo.
  - **Duas regras verificadas em código:** *população* (só entram os pares `SugestaoId`/`LojaId`/`Sku` que o ERP avaliou; cada motivo de exclusão tem contador próprio) e *informação* (o ML só usa dado estritamente anterior a `SugestoesCompra.DataHora`), esta última aplicada em três pontos — `TreinoJob.TreinoAte` + filtro em toda consulta datada do `StageObservationLoader`, `FeatureConfig.PrecoCongeladoAPartirDe`, e validação por item nos dois comparadores.
  - **Ruptura:** default `ExcluirPar` (descarta o par inteiro). `ExcluirDia` é só sensibilidade na camada A e **recusado** na B — o ML se reprojeta sobre os dias sobreviventes e o ERP, que entrega um escalar único, não.
  - **Infra:** entity `ComparacaoPbs` + migration, `ComparacaoWorker`/`ComparacaoProcessor` (3 contratos anti-vazamento aplicados), `StageSugestaoLoader`, endpoints `POST /api/comparison/run`, `GET /api/comparison`, `GET /api/comparison/{id}`, e a tela técnica `/comparacao` (abas por `TipoCalculo`, portão de reconciliação antes dos números, contadores de exclusão em linhas distintas, ressalva treino/serviço no topo dos resultados).
  - **Ressalvas que viajam no `ResultadoJson`:** `ComparacaoOutput.RessalvaPadraoTreinoServe` documenta os dois desvios treino/serviço (preço congelado; classe ABC e orçamento de SKUs recalculados com o corte da sugestão). Ambos só podem prejudicar o braço de ML, nunca inflá-lo.
  - **Pergunta aberta antes de qualquer conclusão:** se o `DemandaDia` do PBS já é corrigido por ruptura. Verificar contra o campo `Falteiro` — se o ERP usa venda bruta e a nossa verdade exclui dias de ruptura, a comparação pende na direção que agrada à hipótese.
- [ ] **F14 — Sessões de comparação**: fluxo guiado para o usuário leigo da rede (entities `ComparacaoSessao`/`ComparacaoSessaoItem`, painel `/` e página `/comparacoes/{id}`, extrator com escolha de sugestão) — em andamento, iniciado antes da F13.

---

## 7. Referências consultadas

- Documentação atual ML.NET (`/dotnet/machinelearning`) via Context7 — confirma SSA, FastTree, LightGBM e AutoML mantidos.
- Repositório `dotnet/machinelearning` (ativo).
- M5 Forecasting Competition (Walmart / Kaggle) — referência da abordagem GBM + features para retail.
