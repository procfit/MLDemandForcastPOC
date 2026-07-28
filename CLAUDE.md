# CLAUDE.md — Guia operacional para o assistente neste repositório

Este arquivo é o **contrato de trabalho** entre Claude (Claude Code) e o desenvolvedor neste repositório. Lê antes de propor qualquer alteração relevante. Se uma instrução aqui conflitar com pedido explícito do usuário na conversa, **o pedido do usuário prevalece** — mas registre o desvio.

---

## 1. Contexto do projeto

POC de **engine de previsão de demanda** para varejo farmacêutico, alimentando um processo de **sugestão de compra**. Arquitetura: .NET 10 + .NET Aspire, fontes em **SQL Server** e **ClickHouse**. Detalhes em [README.md](README.md).

**Estágio atual (F1 concluído):** AppHost orquestra SQL Server (persistente, com volume) + ClickHouse (persistente, com volume) + SQL Server Project (DACPAC). Cada `F5` sobe tudo lado a lado. Bancos declarados: `vendas` (SQL Server, schema via DACPAC), `engine` (SQL Server, schema futuro via EF Core migrations), `vendas-olap` (ClickHouse). Ainda não há código de domínio.

**Decisão técnica firmada:**
- Motor principal = **ML.NET + LightGBM regressão** sobre features engenheiradas (lags, rolling, calendário, promoção, hierarquia).
- **SSA (`ForecastBySsa`) NÃO é o motor principal.** Não sugerir como tal. Pode aparecer apenas como (a) baseline didático ou (b) detector de padrão/anomalia em fluxo paralelo.
- Abstração `IForecastEngine` desde o início para permitir, no futuro, um sidecar Python (Nixtla / Darts) para itens intermitentes ou métodos não disponíveis em ML.NET.
- Sem cloud lock-in no POC.

---

## 2. Stack e convenções

- **Runtime:** .NET 10. `Nullable` e `ImplicitUsings` habilitados nos projetos.
- **Orquestração:** Aspire (`Aspire.AppHost.Sdk/13.1.0`). Toda dependência de infra (DB, cache, sidecar) **deve** ser declarada no AppHost — não usar connection strings hard-coded em `appsettings*.json` para recursos que o Aspire provê.
- **Idioma do código:** identificadores em inglês; comentários e documentação em **português-BR** quando contêm contexto de negócio (varejo farma); inglês quando puramente técnicos.
- **Solução:** formato `.slnx`. Ao adicionar projeto novo, editar `MLDemandForCastPOC.slnx` manualmente (linha `<Project Path="..."/>`).
- **Naming dos projetos:** `CosmosPro.ML.DemandForCast.<Papel>` — mantenha o prefixo.

### Pacotes preferidos
- **ML.NET:** `Microsoft.ML`, `Microsoft.ML.TimeSeries`, `Microsoft.ML.LightGbm`, `Microsoft.ML.FastTree`, `Microsoft.ML.AutoML`.
- **SQL Server (cliente):** `Microsoft.Data.SqlClient` puro; ou `Aspire.Microsoft.Data.SqlClient` quando o consumidor for um projeto Aspire que precise da injeção via `WithReference`.
- **ClickHouse (cliente):** `Aspire.ClickHouse.Driver` (compatível com `Aspire.Hosting.ClickHouse`).
- **SQL Project SDK:** `MSBuild.Sdk.SqlProj/4.2.0` (escolhido sobre `Microsoft.Build.Sql` porque integra de forma transparente com `AddSqlProject<Projects.X>` da CommunityToolkit; `Microsoft.Build.Sql` não declara `TargetFramework` e quebra a metadata gen do Aspire — ver histórico de decisão).
- **Aspire hosting:** SDK `Aspire.AppHost.Sdk/13.3.1`; pacotes `Aspire.Hosting.SqlServer` 13.3.1, `Aspire.Hosting.ClickHouse` 13.1.2 (publicado pela ClickHouse Inc.), `CommunityToolkit.Aspire.Hosting.SqlDatabaseProjects` 13.1.1, `CommunityToolkit.Aspire.Hosting.SqlServer.Extensions` 13.1.1 (traz `.WithDbGate()`), `CommunityToolkit.Aspire.Hosting.Minio` 13.3.0 (object storage).
- **DbGate (UI web de DB):** chamado via `.WithDbGate(cfg => cfg.WithDataVolume().WithLifetime(Persistent))` no recurso SQL Server. O recurso `dbgate` é único na app — `AddDbGate` é idempotente (retorna o existente). ClickHouse usa um **helper local** em `ClickHouseDbGateExtensions.cs` que reproduz o mesmo padrão das `SqlServer.Extensions` (env vars `LABEL_/SERVER_/USER_/PASSWORD_/PORT_/ENGINE_` + `CONNECTIONS`), porque o pacote `Aspire.Hosting.ClickHouse` ainda não traz suporte nativo. Quando o upstream cobrir, remover o helper. Se precisar de outras DB UIs, considere também `.WithAdminer()`.
- **EF Core migrations no AppHost:** `Aspire.Hosting.EntityFrameworkCore` (`AddEFMigrations` + `RunDatabaseUpdateOnStart`) — anunciado no changelog do Aspire 13.3 mas ainda não publicado no NuGet em 2026-05-13. Há um TODO marcado no `AppHost.cs`; revisar a cada bump de Aspire.
- **Testes:** **xUnit** + `FluentAssertions`. Sem MSTest, sem NUnit.

---

## 3. Como o assistente deve agir

### Antes de codar
- Para qualquer pacote ML.NET / ClickHouse / Aspire, **consulte Context7** (`/dotnet/machinelearning`, etc.) antes de escrever — a documentação muda e o conhecimento de treino pode estar defasado.
- Se a tarefa for ambígua quanto a granularidade (diário vs. semanal), horizonte, ou métrica alvo: **pergunte** antes de assumir, salvo se o usuário disse para não pausar.
- Para mudanças que afetam estrutura (novo projeto, novo recurso Aspire, troca de pacote), **proponha em texto antes de aplicar** e espere o "ok".

### Ao codar
- Editar arquivos existentes > criar arquivos novos.
- **Não** introduzir abstrações ou camadas que a tarefa atual não exige. Em particular: não criar `IRepository<T>` genérico, *mediator*, *event bus*, *unit of work* sem necessidade demonstrada.
- **Não** adicionar comentários narrando o que o código faz. Comentário só quando o **porquê** for não-óbvio (uma invariante escondida, um *workaround* de bug específico, uma decisão contraintuitiva).
- **Não** criar arquivos `.md` de planejamento, decisão ou análise — use a conversa. Só CLAUDE.md, README.md, ADR (se o usuário pedir) ou docs explicitamente solicitadas.
- **Não** usar emoji em código ou nos arquivos do repo, salvo pedido explícito.
- Em pipelines ML.NET, **prefira** APIs tipadas (`mlContext.Regression.Trainers.LightGbm(options => ...)`) com classes de dados explícitas (`record`/`class`) — evitar `DataView` "anônimo" salvo em código de exploração.

### Backtest e métricas
- Toda mudança no engine que altere previsão **precisa** ser acompanhada de backtest comparativo (walk-forward) contra o baseline anterior. Sem backtest, não há mérito.
- Métricas reportadas: **MAE, MAPE, WAPE, RMSE** + um intervalo (pinball loss em quantis 50/80/95) quando o modelo for probabilístico.
- Reportar métricas agregadas **e** por hierarquia (categoria, loja, segmento ABC). Médias globais escondem regressões locais.

### Dados sensíveis
- Este é um POC para varejo. **Nunca** commitar dumps reais de vendas, mestres de produto, dados de farmácia identificáveis. Datasets em `samples/` devem ser **sintéticos** ou claramente anonimizados.
- Connection strings reais ficam em user secrets / `.env` / variáveis Aspire — nunca em `appsettings.json` versionado.

---

## 4. Topologia de bancos & schema (F2.1 — atual)

| Banco | Servidor | Schema gerenciado por | Conteúdo |
|---|---|---|---|
| `Stage` | SQL Server | **DACPAC** — SQL Server Project `CosmosPro.ML.DemandForCast.Database`, publicado pelo Aspire via `AddSqlProject` (`stage-schema` resource, one-shot, `WaitForCompletion`). | Staging area dos dados importados pelo usuário via UI (CSV/ZIP). Tabelas plural: `Redes`, `Lojas`, `Produtos`, `Vendas`, `EstoquesDiarios`, `Compras`, `Promocoes`, `MercadoIqvia`, `SinaisExternos`. Engine só lê, nunca escreve — o **Worker** é o único que escreve. |
| `engine` | SQL Server | **EF Core migrations** via `Aspire.Hosting.EntityFrameworkCore.AddEFMigrations` + `RunDatabaseUpdateOnStart` (pacote prerelease 13.3.4-preview). Source-of-truth no projeto `CosmosPro.ML.DemandForCast.Engine`. | Metadados do engine: `CargasStage` (jobs de import), futuros experimentos, runs, modelos, métricas. |
| `vendas-olap` | ClickHouse | Runner one-shot `CosmosPro.ML.DemandForCast.OlapSchema` (console .NET) que aplica scripts `Scripts/*.sql` embarcados, controlando versão via tabela `__schema_migrations`. AppHost wires com `AddProject(...).WithReference(vendasOlapDb)`; apiservice usa `WaitForCompletion(olapSchema)`. | Histórico denso para varredura analítica e feature extraction em massa. |

**Regra:** schema do `Stage` **só** muda via SQL Project (não por script ad-hoc). Schema do `engine` **só** muda via EF Core migration. Sem migrations imperativas no banco `Stage`; sem CREATE TABLE manual em código consumidor. Esta separação é deliberada para manter `Stage` como contrato declarativo das fontes que o engine consome.

**Multi-inquilino (F10):** toda tabela de `Stage` tem `RedeId` com FK. `Lojas` e `Produtos` têm PK `(RedeId, <chave>)` porque `LojaId` e `Sku` são códigos de ERP e **colidem entre redes**; as filhas usam FK **composta** `(RedeId, Sku)` / `(RedeId, LojaId)`, que amarra a linha ao inquilino transitivamente sem FK redundante no caminho do `SqlBulkCopy`. `Redes` existe nos dois bancos porque FK entre bancos não existe no SQL Server: `engine.Redes` é o registro (fonte de verdade do id) e `Stage.dbo.Redes` é projeção que o Worker sincroniza no início de cada import.

**`RedeId` nunca trafega em CSV.** O Worker injeta a partir da `CargaStage` (coluna marcada `ServerSupplied` em `TableSchemas`). Isso mantém o contrato CSV, o Extractor e os fakers intactos, e impede que um cliente reivindique a rede de outro escrevendo um id no arquivo. Ao adicionar tabela nova ao Stage, replique o padrão — e lembre que o polling do Worker é **cross-rede** (pega a próxima pendente de qualquer inquilino), então `RedeId` não entra nos índices de polling.

**Object storage (MinIO):** recursos do tipo Container com `WithLifetime(Persistent) + WithDataVolume()`. Acesso via `CommunityToolkit.Aspire.Hosting.Minio` 13.3.0. Credenciais (access key + secret key) injetadas como `ParameterResource` Aspire (user-secrets para o secret). Usado para armazenar ZIPs de import.

**Persistência:** containers `sql` e `clickhouse` usam `WithLifetime(ContainerLifetime.Persistent) + WithDataVolume()`. Dados sobrevivem entre F5s. Reset completo exige `docker volume rm` explícito — alerte o usuário antes de sugerir reset.

## 5. Operações de risco — peça confirmação

Mesmo com instrução de autonomia, **pause e confirme** antes de:
- Adicionar/remover projeto da solução.
- Trocar versão major de ML.NET ou Aspire SDK.
- Rodar `dotnet ef` migrations contra DB com dado real.
- Apagar artefatos em `bin/`, `obj/` de outros projetos além do que está sendo trabalhado.
- Qualquer `git push`, criação de branch remoto, ou interação com Azure DevOps / GitHub.
- Subir/derrubar containers que não façam parte do `AppHost.cs` atual.

Repositório **não é git** ainda (verificado no bootstrap). Não rodar `git init` sem pedido — o usuário pode estar planejando hospedar em Azure DevOps com layout específico.

---

## 6. Anti-patterns a evitar neste projeto

| Anti-pattern | Por quê |
|---|---|
| Usar `ForecastBySsa` como motor de produção. | Univariado, sem covariáveis, inadequado para retail. Já decidido. |
| Tratar venda = 0 como demanda = 0 sem checar ruptura. | Vira viés sistemático para baixo, modelo aprende a subestimar SKUs com ruptura frequente. |
| Treinar um modelo por SKU. | Inviável em escala farma (dezenas de milhares). Padrão é **modelo global** com SKU como feature (embedding/one-hot/target encoding). |
| MAPE como métrica única. | Quebra em demanda zero/baixa (comum em farma). Sempre acompanhar com WAPE/MAE. |
| Misturar treino/validação cronologicamente. | Sempre **walk-forward**, nunca split aleatório em séries temporais. |
| Esconder *leakage* em features. | Lags precisam respeitar lead time da decisão de compra; preço/promoção precisam ser conhecidos no momento da previsão. |

---

## 7. Quando o usuário pedir algo que cheire a fora-de-escopo do POC

Diga isso explicitamente e pergunte. Exemplos:
- "Posso adicionar autenticação?" → POC, provavelmente não. Confirme.
- "Vamos deployar no Azure?" → fora do roadmap atual (F0–F6 em [README.md](README.md)).
- "Adicionar GraphQL/gRPC/SignalR?" → não há demanda; provavelmente *over-engineering*.

---

## 8. Convenções de commit (quando virar repo git)

Padrão Conventional Commits em **inglês** no *subject*, corpo em pt-BR se contiver explicação de negócio:

```
feat(engine): add LightGBM trainer with quantile loss

Adiciona pipeline LightGBM para previsão pontual + quantis (50/80/95).
Métricas de backtest em walk-forward por categoria, comparadas ao baseline naive.
```

---

## 9. Para o próximo Claude que abrir este repo

Leia [README.md](README.md) §2 (a justificativa da escolha do ML.NET) e §6 (roadmap). Se algo no roadmap já estiver implementado mas não marcado, **atualize o roadmap no mesmo PR** — não deixe o README mentir.
