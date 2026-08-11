# CLAUDE.md — Guia operacional para o assistente neste repositório

Este arquivo é o **contrato de trabalho** entre Claude (Claude Code) e o desenvolvedor neste repositório. Lê antes de propor qualquer alteração relevante. Se uma instrução aqui conflitar com pedido explícito do usuário na conversa, **o pedido do usuário prevalece** — mas registre o desvio.

---

## 1. Contexto do projeto

POC de **engine de previsão de demanda** para varejo farmacêutico, alimentando um processo de **sugestão de compra**. Arquitetura: .NET 10 + .NET Aspire, dados em **SQL Server** (há um ClickHouse provisionado no código, mas **desativado no AppHost** — não roda nem no `F5` nem no compose publicado, e nenhum caminho de código o consulta — ver §4). Detalhes em [README.md](README.md).

**Estágio atual (F1 concluído):** AppHost orquestra SQL Server (persistente, com volume) + SQL Server Project (DACPAC). Cada `F5` sobe tudo lado a lado. Bancos declarados: `Stage` (SQL Server, schema via DACPAC), `engine` (SQL Server, schema futuro via EF Core migrations). Há também um banco `vendas-olap` em ClickHouse, provisionado em F1 mas **desativado no AppHost** desde então — não sobe nem no `F5` nem no compose publicado (ver §4). Ainda não há código de domínio.

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
- **ClickHouse (cliente):** `Aspire.ClickHouse.Driver` (compatível com `Aspire.Hosting.ClickHouse`). Referenciado no repositório, mas hoje sem consumidor: o recurso ClickHouse está desativado no AppHost (ver §4).
- **SQL Project SDK:** `MSBuild.Sdk.SqlProj/4.2.0` (escolhido sobre `Microsoft.Build.Sql` porque integra de forma transparente com `AddSqlProject<Projects.X>` da CommunityToolkit; `Microsoft.Build.Sql` não declara `TargetFramework` e quebra a metadata gen do Aspire — ver histórico de decisão).
- **Deploy de DACPAC:** `Microsoft.SqlServer.DacFx` (`DacServices` + `DacPackage`), no projeto `Migrator`. Multiplataforma — roda em container Linux, ao contrário do SqlPackage.exe.
- **Aspire hosting:** SDK `Aspire.AppHost.Sdk/13.3.1`; pacotes `Aspire.Hosting.SqlServer` 13.3.1, `Aspire.Hosting.ClickHouse` 13.1.2 (publicado pela ClickHouse Inc.; recurso que ele suporta está comentado em `AppHost.cs` — ver §4), `CommunityToolkit.Aspire.Hosting.SqlDatabaseProjects` 13.1.1, `CommunityToolkit.Aspire.Hosting.SqlServer.Extensions` 13.1.1 (traz `.WithDbGate()`), `CommunityToolkit.Aspire.Hosting.Minio` 13.3.0 (object storage).
- **DbGate (UI web de DB):** chamado via `.WithDbGate(cfg => cfg.WithDataVolume().WithLifetime(Persistent))` no recurso SQL Server. O recurso `dbgate` é único na app — `AddDbGate` é idempotente (retorna o existente). Existe um **helper local** em `ClickHouseDbGateExtensions.cs` que reproduz o mesmo padrão das `SqlServer.Extensions` (env vars `LABEL_/SERVER_/USER_/PASSWORD_/PORT_/ENGINE_` + `CONNECTIONS`), para o dia em que o ClickHouse for reativado, porque o pacote `Aspire.Hosting.ClickHouse` ainda não traz suporte nativo — hoje ele não é chamado, já que o recurso ClickHouse está comentado em `AppHost.cs` (ver §4). Quando o upstream cobrir, remover o helper. Se precisar de outras DB UIs, considere também `.WithAdminer()`.
- **Migração de schema:** feita pelo projeto `Migrator` (§4), **não** por `AddSqlProject`/`AddEFMigrations`. Os pacotes `CommunityToolkit.Aspire.Hosting.SqlDatabaseProjects` e `Aspire.Hosting.EntityFrameworkCore` continuam referenciados no AppHost mas sem uso em código — mantidos porque o `.sqlproj` segue sendo a fonte de verdade do `Stage` e o retorno a `AddSqlProject` em modo `run` é uma linha. Não introduzir um segundo caminho de migração ao lado do `db-migrator`.
- **Resultado sem exceção (só no extrator):** `FluentResults` 4.0.0. A camada de serviço do extrator (`CatalogoService`, `ExtractionService`) devolve `Result<T>` com erros tipados (`ExtratorErros.cs`) que carregam etapa, arquivo `.sql`, número do erro SQL e duração; o form e o CLI só traduzem — um pinta a tela, o outro devolve código de saída, os dois pelo **mesmo** mapa. Exceção que escapa dessa camada passa a significar bug. Duas regras que não são estilo: **`Result` nunca representa cancelamento** (`OperationCanceledException` continua sendo o veículo, e a tradução de falha chama `ThrowIfCancellationRequested` **antes** de classificar, senão um Cancelar vira "a conexão caiu" — que é transitório, e seria retentado); e **retry só em leitura curta e idempotente**, nunca na extração, que escreve arquivo e custa minutos.
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

## 4. Topologia de bancos & schema (atual — F14)

| Banco | Servidor | Schema gerenciado por | Conteúdo |
|---|---|---|---|
| `Stage` | SQL Server | **DACPAC** — SQL Server Project `CosmosPro.ML.DemandForCast.Database`, publicado pelo `db-migrator` via `Microsoft.SqlServer.DacFx`. | Staging area dos dados importados pelo usuário via UI (CSV/ZIP). Tabelas plural: `Redes`, `Lojas`, `Produtos`, `Vendas`, `EstoquesDiarios`, `Compras`, `Promocoes`, `MercadoIqvia`, `SinaisExternos`, `SugestoesCompra`, `SugestoesCompraItens`. Engine só lê, nunca escreve — o **Worker** é o único que escreve. |
| `engine` | SQL Server | **EF Core migrations**, aplicadas pelo `db-migrator` com `Database.MigrateAsync()`. Source-of-truth no projeto `CosmosPro.ML.DemandForCast.Engine`. | Metadados do engine: `Redes` (registro dos inquilinos, fonte de verdade do `RedeId`), `CargasStage`, `TreinoJobs`, `SimulacoesCompra`, `ComparacoesPbs`, **`ComparacaoSessoes`** e **`ComparacaoSessaoItens`** (F14), **`Questionarios`** e **`QuestionarioRespostas`** (F15) — todas ver abaixo —, e as 7 tabelas do ASP.NET Core Identity (`AspNetUsers` com `RedeId` + FK para `Redes`, `AspNetRoles`, etc). |
| `vendas-olap` | ClickHouse | Runner one-shot `CosmosPro.ML.DemandForCast.OlapSchema` (console .NET) que aplicaria scripts `Scripts/*.sql` embarcados, controlando versão via tabela `__schema_migrations` — mas `Scripts/` está vazio, então nunca aplicou nenhum. **Desativado**: os recursos `clickhouse`/`vendas-olap`/`vendas-olap-schema` estão comentados no `AppHost.cs` (ver abaixo), então hoje o banco não existe em lugar nenhum, nem `F5` nem compose. | Era para ser o histórico denso para varredura analítica. **Nunca teve consumidor**: nenhum código lê este banco. |

**Regra:** schema do `Stage` **só** muda via SQL Project (não por script ad-hoc). Schema do `engine` **só** muda via EF Core migration. Sem migrations imperativas no banco `Stage`; sem CREATE TABLE manual em código consumidor. Esta separação é deliberada para manter `Stage` como contrato declarativo das fontes que o engine consome.

**Quem aplica (`db-migrator`):** o projeto `CosmosPro.ML.DemandForCast.Migrator` — console .NET one-shot que roda o DACPAC no `Stage` e, em seguida, as EF Core migrations no `engine`, nessa ordem, e encerra com **código de saída != 0** se qualquer etapa falhar. Substituiu `AddSqlProject` e `AddEFMigrations().RunDatabaseUpdateOnStart()`: os dois eram recursos de hosting que **só existiam em modo `run`** e sumiam do `docker-compose.yaml` do `aspire publish`, deixando o deploy com os dois bancos vazios (sem Identity, sem login). Um console comum vira serviço de compose normal, então o mecanismo é o mesmo no `F5` e no deploy — e é por isso que ele fica em **um** projeto, não em dois: no destino o operador tem uma coisa para checar e um log para ler. Ao mexer no schema, o que muda continua sendo o `.sqlproj` ou a migration; o migrador não deve ganhar lógica de schema. O `.dacpac` entra na imagem por um target no `.csproj` do Migrator que pergunta o caminho de saída ao projeto `Database` (`GetTargetPath`) e o copia como `Content` — em produção não há `.sqlproj` para compilar.

**Multi-inquilino (F10):** toda tabela de `Stage` tem `RedeId` com FK. `Lojas` e `Produtos` têm PK `(RedeId, <chave>)` porque `LojaId` e `Sku` são códigos de ERP e **colidem entre redes**; as filhas usam FK **composta** `(RedeId, Sku)` / `(RedeId, LojaId)`, que amarra a linha ao inquilino transitivamente sem FK redundante no caminho do `SqlBulkCopy`. `Redes` existe nos dois bancos porque FK entre bancos não existe no SQL Server: `engine.Redes` é o registro (fonte de verdade do id) e `Stage.dbo.Redes` é projeção que o Worker sincroniza no início de cada import.

**`RedeId` nunca trafega em CSV.** O Worker injeta a partir da `CargaStage` (coluna marcada `ServerSupplied` em `TableSchemas`). Isso mantém o contrato CSV, o Extractor e os fakers intactos, e impede que um cliente reivindique a rede de outro escrevendo um id no arquivo. Ao adicionar tabela nova ao Stage, replique o padrão — e lembre que o polling do Worker é **cross-rede** (pega a próxima pendente de qualquer inquilino), então `RedeId` não entra nos índices de polling.

**Identidade e escopo (F11):** login com ASP.NET Core Identity hospedado na **Web** (é o único processo que o navegador alcança — a `apiservice` não tem endpoint externo, e isso é invariante, não acidente). Papéis: `PowerUser` global (`RedeId` nulo) e `UsuarioRede` escopado. Nenhum código deve ler `redeId` de rota, query ou formulário para decidir escopo — use `IRedeContext`, que o deriva do usuário autenticado. A `apiservice` recebe `redeId` na query porque é interna; se algum dia for publicada, este modelo cai e ela precisa de auth própria.

**Sessões de comparação (F14):** duas tabelas novas no `engine`, ambas com source-of-truth em `CosmosPro.ML.DemandForCast.Engine/Entities/`.

- **`ComparacaoSessoes`** (`ComparacaoSessao`) — uma comparação ancorada a **uma** sugestão do PBS. PK `Id` (UUIDv7), `RedeId` com FK para `Redes`, `Status` gravado como texto (`HasConversion<string>`, **30 chars** — `AguardandoQuestionario` tem 22 e não caberia nos 20 originais) e máquina de estados de oito fases em `ComparacaoSessao.PodeTransicionar`. Guarda a declaração que veio do `manifesto.json` (`SugestaoId`, `SugestaoDescricao`, `SugestaoDataHora`, `SugestaoTipoCalculo`, `SkusSemCadastro`), os ids das três fases (`CargaStageId`, `TreinoJobId`, `ComparacaoPbsId`), os agregados da manchete em `ResultadoJson` e os dois textos de desfecho (`MotivoInviabilidade` para pré-condição do envio, `MensagemErro` para o que quebrou). Índices `(Status, AtualizadoEm)` — polling **cross-rede**, como as demais filas, então sem `RedeId` — e `(RedeId, CriadoEm)` para a listagem do painel.
- **`ComparacaoSessaoItens`** (`ComparacaoSessaoItem`) — o detalhe por item, PK `(SessaoId, LojaId, Sku)` e FK `Cascade` para a sessão (apagar a sessão apaga o detalhe). Os ponteiros da sessão para as três fases (`CargaStageId`, `TreinoJobId`, `ComparacaoPbsId`) são **FKs lógicas** — índice sim, constraint não, como em `SimulacoesCompra` e `ComparacoesPbs` —, para o histórico da sessão sobreviver à remoção do job. As precisões são declaradas porque o default do EF Core é `decimal(18,2)` e truncaria em silêncio: unidades `decimal(15,3)` e taxa de demanda/dia `decimal(12,4)` **espelham** o Stage; o valor, `decimal(14,4)`, não espelha coluna nenhuma (a monetária do Stage é `PrecoCompra DECIMAL(15,4)`, preço unitário) — mantém as 4 casas do dinheiro com 10 dígitos inteiros.

**O resultado da sessão é materializado, nunca recalculado.** Cada import faz `DELETE ... WHERE RedeId` no Stage (`CargaProcessor`), então o próximo ZIP da rede apaga a sugestão, as vendas e o cadastro que o resultado descreve. Uma sessão que só guardasse ponteiros viraria tela vazia — ou, pior, tela preenchida com os dados do envio seguinte e a cara do anterior. É por isso que `ComparacaoSessaoItens` existe como tabela em vez de consulta, e por isso `SessaoResultadoMaterializador` roda no fim da fase de comparação (última volta em que a sessão ainda está em `Comparando`), num `DELETE` + `SqlBulkCopy` + `UPDATE Status='Concluida'` na **mesma transação** — o `WHERE ... AND Status = <fase reclamada>` do `UPDATE` final é o que impede materialização em dobro quando dois processos reclamam a mesma sessão. Não mova isso para a abertura da tela e não troque a tabela por um recálculo do Stage.

**As colunas do braço de ML são anuláveis, e isso é o contrato da tabela.** Nulo significa "não foi possível calcular", **nunca** "o ML disse zero" — duas afirmações opostas para quem lê a tela. Hoje a ausência é o desfecho esperado (ver a limitação de horizonte no README §6, F14), então gravar ou renderizar zero faria a tela dizer ao comprador que o ML mandaria não comprar nada. Vale igual para `SobraPbsValor`/`SobraMlValor`, nulos quando o item não tem `PrecoCompra`: zero ali afirmaria "esta compra não deixou capital parado", e é justamente a coluna pela qual o comprador ordena a tabela para achar o pior item. Não "simplifique" nulo para zero em nenhuma das pontas — montador, `DataTable` do bulk, DTO ou Razor.

**`TreinoJob.TreinoAte` é controle anti-vazamento, não parâmetro de desempenho.** O import da sessão traz **de propósito** os dias posteriores à sugestão do ERP: eles são o gabarito contra o qual os dois braços são pontuados. Um treino sem corte aprende sobre esses mesmos dias e a comparação passa a medir memória, não previsão — por isso `ComparacaoProcessor` **recusa** um treino com `TreinoAte` nulo, e recusa também um cuja `TrainingResult.UltimaDataTreinada` não seja estritamente anterior à sugestão mais antiga da janela. `SessaoJobs.Treino` corta no **próprio dia da sugestão** porque o `StageObservationLoader` aplica o corte de forma exclusiva (`Data < @treinoAte`). Encurtar a janela de treino não deixa o modelo melhor, deixa-o honesto: não "otimize" esse corte, e não faça a recusa virar aviso.

**Uma sessão em voo por rede, bloqueada no envio dos dados.** O Stage é por rede e cada import o **substitui inteiro**, então duas sessões em voo na mesma rede não competem por recurso — elas se destroem: na melhor hipótese o segundo envio apaga a sugestão que a primeira ia comparar e a primeira morre culpando um ZIP que estava certo; na pior, a sugestão nova cai no mesmo dia e método e a primeira pontua **a sugestão da segunda** contra o próprio modelo, produzindo um número plausível e falso. A recusa vive em `ComparacoesEndpoints.SessaoConcorrenteAsync`, no `POST /api/comparacoes/{id}/dados` e **não** na criação da sessão: criar sessão não escreve nada no Stage, e bloquear ali deixaria o botão "Nova comparação" quebrado por causa de uma sessão travada. "Em voo" quer dizer *viva* — `AtualizadoEm` dentro de `ComparacaoSessao.LimiteDeFaseSemProgresso` —, de propósito: um worker que morre não pode trancar a rede para sempre, e quem solta o bloqueio precisa usar o mesmo relógio que encerra a fase abandonada.

**`ComparacaoSessaoItens` não tem `RedeId`** — o escopo é transitivo pela FK para a sessão. Todo endpoint que ler a tabela **tem** de juntar a sessão pai e filtrar pelo inquilino que vem do `IRedeContext`, num único round-trip: consultar por `SessaoId` sozinho entregaria o detalhe comercial de um inquilino a quem acertasse um Guid, e conferir o pai numa consulta à parte deixaria a janela entre as duas. É o que `ComparacoesEndpoints.ItensDaSessao` e `TotalDeItensAsync` fazem, e é por onde página, contagem e agregado passam. Quando a sessão não é da rede do chamador, responda **404 e não 403** — um 403 confirmaria a quem sondasse que a sessão existe em outra rede.

**Questionário do comprador (F15): é a última fase da sessão, não um anexo dela.** `Comparando → AguardandoQuestionario → Concluida`, e `Concluida` passou a significar "o comprador respondeu". Três consequências que não são estilo:

- **É a única transição da máquina que não sai do Worker.** `AguardandoQuestionario` está de fora do allowlist de `SessaoWorker.ClaimNextAsync` (`WHERE Status IN ('ProcessandoDados','Treinando','Comparando')`), então nenhuma fila a reclama e o relógio de `LimiteDeFaseSemProgresso` não a alcança — correto, porque o que falta é um humano, e 2h matariam uma sessão esperando o comprador voltar do almoço. Quem grava `Concluida` é `POST /api/comparacoes/{id}/questionario/enviar`. Pelo mesmo motivo ela também **não** conta como sessão viva em `SessaoConcorrenteAsync`: o resultado já está materializado e um ZIP novo não o alcança. `SessaoResultadoMaterializador` agora grava `AguardandoQuestionario` no `UPDATE` final — o resto da regra de materialização não mudou.
- **`PodeExcluir` tem duas recusas, por motivos diferentes.** As três fases de worker recusam para proteger o *job*; `Concluida` recusa para proteger o *dado*. A equivalência "concluída ⟺ respondida" é o que a migration `AddQuestionarios` garante ao reclassificar as sessões que já estavam concluídas — sem aquele `UPDATE`, elas ficariam inexcluíveis para sempre sem nunca ter sido avaliadas. Na Web, `SessaoView.EstadoTerminal` virou `SemPollNecessario`: "não muda sozinha" e "fluxo terminou" deixaram de coincidir, e `AguardandoQuestionario` é o primeiro caso.
- **`Questionarios` não tem coluna de situação, e `QuestionarioRespostas` não tem `RedeId`.** A autoridade sobre "selado" é o status da sessão; um `Status` no cabeçalho repetiria a mesma verdade em dois lugares que podem divergir, e a tela leria o errado — `EnviadoEm` é carimbo, não situação. O escopo das respostas é transitivo pela FK, como em `ComparacaoSessaoItens`, então **todo endpoint que as ler tem de juntar o pai e filtrar pelo inquilino do `IRedeContext`**, e responder 404 (não 403) para sessão de outra rede.

**O catálogo vive em código (`Engine/Questionarios/`), e o texto exibido é gravado junto com a resposta.** O instrumento é fixo e revisado por quem conduz a pesquisa; tabelas de perguntas com CRUD seriam a abstração que §3 proíbe. O preço é que o catálogo muda com deploy — pago em `QuestionarioRespostas`, que guarda o retrato do enunciado e da opção escolhida, para um ajuste de redação não reescrever retroativamente o que foi perguntado (mesmo princípio de `SugestaoDescricao` e da materialização). `OpcaoValor` é a posição na escala em pergunta ordinal; **nulo ali significa "esta pergunta não é ordinal", nunca "grau zero"** — atribuir 0 a uma nominal produziria média onde não existe média. Ao trocar o conteúdo, suba `QuestionarioCatalogo.Versao`.

**O instrumento é o Apêndice A da dissertação, e três coisas nele não são negociáveis por conveniência de código.** (1) O texto está em **português europeu** e o resto da aplicação em pt-BR — a diferença é visível na tela de propósito; reescrever enunciado invalida a comparação com o que foi submetido, então não "corrija" e não reordene as perguntas (os códigos A1–A3 e B1–B7 são os do documento, e é por eles que a análise casa as respostas). (2) **A4 do documento está deliberadamente ausente** — o extrator só lê PBS, a resposta seria constante, e a decisão está registrada em comentário exatamente onde a pergunta caberia, para ninguém "restaurá-la" como se fosse esquecimento. (3) **A apresentação/consentimento fica fora do wizard**, acima do primeiro passo, porque é o que o participante concorda antes de responder — e há uma pendência aberta ali: o texto afirma que "não será recolhida qualquer informação que permita identificar os participantes", enquanto `Questionarios.UsuarioId` grava quem respondeu. Ou a coluna sai, ou o texto muda; é decisão de quem conduz a pesquisa, não de refatoração.

**Ao mexer na tela do questionário, mantenha o `data-test="pergunta-<codigo>"` por pergunta.** As sete afirmações da Parte B compartilham os mesmos rótulos de alternativa ("4 – Concordo" etc.), então sem esse escopo um seletor por texto casa com a primeira ocorrência — o E2E responderia B1 sete vezes, deixaria B2–B7 em branco e passaria provando o oposto do que afirma.

**Rascunho e envio exigem coisas diferentes, de propósito.** `QuestionarioValidator.Conferir` vale nos dois (resposta malformada nunca é gravada); `ObrigatoriasFaltando` vale só no envio — exigir completude no rascunho transformaria "salvo, volto depois" em perda de tudo, e aí o dado da pesquisa não existiria. O `PUT` substitui o conjunto inteiro: a tela manda o estado completo do wizard, então pergunta ausente do corpo é "sem resposta", não "mantenha a antiga".

**Duas armadilhas de plataforma encontradas aqui, ambas invisíveis ao compilador.** Estão comentadas no código; repito porque as duas custam horas: (1) **`[FromQuery] Guid x = default` derruba a aplicação inteira** — o compilador não grava constante de `Guid` em metadata, o default chega como `null`, o `RequestDelegateFactory` estoura, e como a tabela de rotas é construída inteira na primeira requisição *todos* os endpoints caem, inclusive `/health`; o sintoma é o AppHost não subir. Use `Guid?`, nunca `= default`. (2) **Transação EF sob estratégia de retry** — `AddSqlServerDbContext` liga `EnableRetryOnFailure` e o EF recusa transação iniciada pelo usuário; as outras duas transações do repositório escapam por usarem `SqlConnection` própria. Quando o trabalho é de entidade, envolva em `CreateExecutionStrategy().ExecuteAsync` com `ChangeTracker.Clear()` na entrada, senão a retentativa herda as entidades da tentativa que rolou atrás.

**Object storage (MinIO):** recursos do tipo Container com `WithLifetime(Persistent) + WithDataVolume()`. Acesso via `CommunityToolkit.Aspire.Hosting.Minio` 13.3.0. Credenciais (access key + secret key) injetadas como `ParameterResource` Aspire (user-secrets para o secret). Usado para armazenar ZIPs de import.

**Persistência:** o container `sql` usa `WithLifetime(ContainerLifetime.Persistent) + WithDataVolume()`. Dados sobrevivem entre F5s. Reset completo exige `docker volume rm` explícito — alerte o usuário antes de sugerir reset.

**ClickHouse está desativado, código preservado.** Provisionado em F1, quando a escolha de armazenamento analítico ainda estava aberta, acabou sem nenhum consumidor: todo o caminho real (import → `Stage` → features → treino → comparação → materialização) é SQL Server; o `apiservice` recebia a connection string e nunca a abriu. Os três recursos (`clickhouse`, `vendas-olap`, `vendas-olap-schema`) e o trecho de wiring do `apiservice` que os referenciava estão **comentados** em `AppHost.cs` (seção "ClickHouse: desativado, código preservado"), com o comentário explicando o porquê. Antes disso eles rodavam condicionalmente em modo `run` (`if (builder.ExecutionContext.IsRunMode)`), então já não entravam no `docker-compose.yaml` do `aspire publish` nem nas imagens do `aspire do push`; a mudança foi torná-los ausentes também do `F5` — um container e um gate de startup a menos para um banco que ninguém consulta. **Nada foi removido**: o projeto `CosmosPro.ML.DemandForCast.OlapSchema`, o helper `ClickHouseDbGateExtensions.cs` e os pacotes ClickHouse continuam no repositório, porque a necessidade analítica é esperada mais adiante. **Para reativar:** descomente o bloco de recursos em `AppHost.cs` e o trecho de wiring logo após a definição de `apiService` (`apiService.WithReference(vendasOlapDb)...`), lembrando de `.WithContainerRegistry(registry)` no runner se ele também for para o compose publicado. Os fixtures de teste sobem o AppHost real via `Aspire.Hosting.Testing`, então também deixaram de ver o ClickHouse.

## 5. Operações de risco — peça confirmação

Mesmo com instrução de autonomia, **pause e confirme** antes de:
- Adicionar/remover projeto da solução.
- Trocar versão major de ML.NET ou Aspire SDK.
- Rodar `dotnet ef` migrations contra DB com dado real.
- Apagar artefatos em `bin/`, `obj/` de outros projetos além do que está sendo trabalhado.
- Qualquer `git push`, criação de branch remoto, ou interação com Azure DevOps / GitHub.
- Subir/derrubar containers que não façam parte do `AppHost.cs` atual.

Repositório **é git**, com remoto `origin` no GitHub (`procfit/MLDemandForcastPOC`) e `main` como base de PR. O trabalho acontece em branch por fase. `push`, criação de branch remoto e abertura de PR continuam exigindo pedido explícito — ver a lista acima.

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
- ~~"Posso adicionar autenticação?" → POC, provavelmente não.~~ **Desatualizado desde F11.** O sistema tem ASP.NET Core Identity com login, papéis (`PowerUser` global e `UsuarioRede` escopado) e área administrativa. Foi necessário porque usuários de redes reais distintas operam a aplicação e o dado comercial de uma não pode alcançar a outra.
- "Vamos deployar no Azure?" → fora do roadmap atual (F0–F6 em [README.md](README.md)).
- "Adicionar GraphQL/gRPC/SignalR?" → não há demanda; provavelmente *over-engineering*.

---

## 8. Convenções de commit

Padrão Conventional Commits em **inglês** no *subject*, corpo em pt-BR se contiver explicação de negócio:

```
feat(engine): add LightGBM trainer with quantile loss

Adiciona pipeline LightGBM para previsão pontual + quantis (50/80/95).
Métricas de backtest em walk-forward por categoria, comparadas ao baseline naive.
```

---

## 9. Para o próximo Claude que abrir este repo

Leia [README.md](README.md) §2 (a justificativa da escolha do ML.NET) e §6 (roadmap). Se algo no roadmap já estiver implementado mas não marcado, **atualize o roadmap no mesmo PR** — não deixe o README mentir.
