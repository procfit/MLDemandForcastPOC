# F13 — Comparação ML vs PBS: plano de implementação

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe arquivos `.md` de planejamento.
> Gravado a pedido explícito do usuário em 2026-07-28.

> **Depende de:** [F10](2026-07-28-f10-isolamento-por-rede.md) e [F12](2026-07-28-f12-captura-sugestao-pbs.md).

**Objetivo:** medir se o ML acerta mais que os métodos do ERP, em três camadas, e mostrar
tanto onde ganha quanto onde perde.

**Mudança em relação ao desenho anterior:** a camada de "validar nossa cópia do eMax/eSeg"
**deixa de existir**. O PBS entrega o eMax, o eSeg e a própria previsão de demanda que usou.
Não há cópia a validar. A classe `EMaxESegPolicy` sai do repo.

## As três camadas

### Camada A — previsão contra previsão *(o resultado principal)*

O campo `DemandaDia` é a previsão de venda diária do próprio ERP. O ML produz a sua. A
venda real está em `Vendas`. Compara-se os dois contra a mesma verdade.

É a comparação mais limpa que existe neste projeto: mesma grandeza, mesma data, mesma
unidade. Não depende de arredondamento de embalagem, de política de estoque, nem de
decisão humana.

Métricas: **MAE, WAPE** por hierarquia (categoria, curva, loja), mais **taxa de vitória** —
em que fração dos pares item × loja o ML errou menos. A taxa de vitória é o número que vai
para a tela do usuário leigo.

### Camada B — decisão contra decisão

Quanto comprar. Aqui existe uma armadilha: `CompraSugerida` não é previsão, é quantidade de
pedido — depende do estoque atual, dos pedidos pendentes e do arredondamento de embalagem.

**Regra que isola a variável testada:** o braço do ML reaproveita do próprio ERP o
`EstoqueSaldo`, o `PedidosPendentes`, o `DiasEstoque` e o `FatorEmbalagem`, e troca
**apenas** a estimativa de demanda. Assim a diferença de quantidade vem só da previsão, não
de divergência de posição de estoque.

Verdade: a venda real na janela coberta pela compra (de `DataHora` até `DataHora + DiasEstoque`).

Métricas em unidades e em R$: **excesso** comprado, **falta**, e venda perdida estimada.

### Camada C — intervenção humana *(descritiva)*

`CompraSugerida` versus `CompraAutorizada`. Mede quanto o comprador mexe na sugestão do
sistema. Não é comparação de método — é contexto, e responde de forma empírica a pergunta
"o método tradicional na prática é o ERP ou o ERP mais uma pessoa?".

## Global Constraints

- **Regra de população:** só entram os pares (`SugestaoId`, `LojaId`, `Sku`) presentes em `SugestoesCompraItens`. O ML nunca é avaliado sobre itens que o ERP não olhou, e vice-versa.
- **Regra de informação:** o ML só pode usar dado com data **estritamente anterior** a `SugestaoesCompra.DataHora`. Nada de espiar o futuro (CLAUDE.md §6).
- **Nunca agrupar `TipoCalculo` 1 com 2.** São dois baselines distintos; média entre eles não significa nada.
- **Nunca agrupar redes.** Com duas redes é estudo multi-caso, não amostra. Resultado sempre por rede.
- Quando `TipoCalculo = 2`, `EstoqueSeguranca`/`EstoqueMaximo` são zero por construção — não tratar como dado faltando nem como erro.

---

## Task 1: Remover a política reimplementada

**Files:**
- Delete: `CosmosPro.ML.DemandForCast.Purchasing/Policies/EMaxESegPolicy.cs`
- Modify: `tests/CosmosPro.ML.DemandForCast.Purchasing.Tests/` — remover os testes dela
- Modify: `CosmosPro.ML.DemandForCast.Worker/Purchasing/SimulacaoProcessor.cs`
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Pages/SugestaoCompra.razor`

- [ ] **Step 1:** apagar `EMaxESegPolicy` e seus testes.
- [ ] **Step 2:** `ForecastRopPolicy` **permanece** — é o que transforma previsão em quantidade na camada B.
- [ ] **Step 3:** o `PurchasingSimulator` deixa de ser o comparativo principal e passa a ser ferramenta secundária (replay longo). Não apagar; rebaixar na UI.
- [ ] **Step 4:** `README.md` §6 — corrigir o texto de F8, que hoje descreve as duas políticas como o comparativo do TCC.
- [ ] **Step 5: Commit**

```bash
git rm CosmosPro.ML.DemandForCast.Purchasing/Policies/EMaxESegPolicy.cs
git commit -m "refactor(purchasing): remove eMax/eSeg reimplementado — baseline agora é o ERP"
```

---

## Task 2: Entidade e job de comparação

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Engine/Entities/ComparacaoPbs.cs`
- Modify: `CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs`

- [ ] **Step 1: entidade** — segue o padrão de `TreinoJob`/`SimulacaoCompra`: `Id`, `RedeId`,
      `TreinoJobId`, `Status`, `DataAgendamento`, `DataInicioProcessamento`, `DataConclusao`,
      `MensagemErro`, `ResultadoJson`, mais filtros da execução (janela de datas, `TipoCalculo`).
- [ ] **Step 2:** mapeamento com FK real para `Rede` (padrão F10) e índice de polling
      `(RedeId, Status, DataAgendamento)`.
- [ ] **Step 3:** `dotnet ef migrations add AddComparacoesPbs`.

---

## Task 3: Camada A — previsão contra previsão

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Forecasting/Comparison/ForecastVsErpComparer.cs`
- Create: `CosmosPro.ML.DemandForCast.Forecasting/Comparison/ComparisonModels.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Forecasting.Tests/ForecastVsErpComparerTests.cs`

**Interfaces:**
- Consome: `FeatureVector` (F5), previsão do `LightGbmEngine` (F6), linhas de `SugestoesCompraItens`.
- Produz: `ComparisonResult` com métricas globais, por dimensão, e `TaxaVitoriaMl`.

- [ ] **Step 1: teste primeiro** — dado um item onde o ERP previu 2,0/dia, o ML previu 3,0/dia
      e a venda real foi 3,0/dia, o comparador aponta o ML como vencedor naquele par e a taxa
      de vitória reflete isso.
- [ ] **Step 2: teste de anti-leakage** — features usadas na comparação nunca têm data ≥ `DataHora`.
- [ ] **Step 3: implementação** — MAE/WAPE dos dois braços contra a venda real, agregados global
      e por dimensão (categoria, curva, loja), mais taxa de vitória.
- [ ] **Step 4:** verde.

---

## Task 4: Camada B — decisão contra decisão

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Purchasing/Comparison/DecisionComparer.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Purchasing.Tests/DecisionComparerTests.cs`

- [ ] **Step 1: teste primeiro** — reaproveitando `EstoqueSaldo`, `PedidosPendentes`,
      `DiasEstoque` e `FatorEmbalagem` do ERP, uma previsão de ML maior gera quantidade maior,
      e o excesso/falta é medido contra a venda real da janela.
- [ ] **Step 2: implementação** — quantidade do braço ML pela mesma aritmética do ERP,
      trocando só a demanda; excesso, falta e venda perdida em unidades e R$ (usa `PrecoCompra`).
- [ ] **Step 3:** verde.

---

## Task 5: Camada C — intervenção humana

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Purchasing/Comparison/HumanOverrideReport.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Purchasing.Tests/HumanOverrideReportTests.cs`

- [ ] **Step 1:** fração de linhas em que `CompraAutorizada <> CompraSugerida`; desvio médio
      relativo; fração zerada pelo comprador (`CompraAutorizada = 0` com `CompraSugerida > 0`).
- [ ] **Step 2:** verde. É estatística descritiva — não precisa de mais que isso.

---

## Task 6: Worker e API

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Worker/Comparison/ComparacaoWorker.cs`
- Create: `CosmosPro.ML.DemandForCast.Worker/Comparison/ComparacaoProcessor.cs`
- Create: `CosmosPro.ML.DemandForCast.Worker/Comparison/StageSugestaoLoader.cs`
- Modify: `CosmosPro.ML.DemandForCast.ApiService/Program.cs`

- [ ] **Step 1:** `ComparacaoWorker` com o mesmo padrão de polling `UPDLOCK/READPAST` das outras filas.
- [ ] **Step 2:** `StageSugestaoLoader` carrega cabeçalhos + itens filtrados por rede, janela e `TipoCalculo`.
- [ ] **Step 3:** `ComparacaoProcessor` orquestra: baixa o modelo do MinIO, prevê nos pares da
      população, roda as três camadas, grava `ResultadoJson` separado por `TipoCalculo`.
- [ ] **Step 4:** endpoints `POST /api/comparison/run`, `GET /api/comparison`, `GET /api/comparison/{id}`.

---

## Task 7: UI técnica

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Comparacao.razor`
- Create: `CosmosPro.ML.DemandForCast.Web/Services/ComparisonApiClient.cs`

- [ ] **Step 1:** seleção de treino + janela + tipo de cálculo; tabela de execuções com polling 3s.
- [ ] **Step 2:** três blocos, um por camada, com **abas separadas por `TipoCalculo`** — nunca somados.
- [ ] **Step 3:** drill-down por categoria/curva/loja, reaproveitando o padrão de F7, com a
      flag de regressão onde o ML perde (CLAUDE.md §6).

A tela para o usuário leigo é **F14**, não aqui. Esta é a tela do instrumento.

---

## Task 8: Documentação

- [ ] **Step 1:** `Docs/04-avaliacao-metricas.md` com as três camadas e as duas regras (população, informação).
- [ ] **Step 2:** `Docs/tcc-design.md` §2 — a tabela de níveis N1/N2 precisa refletir que o
      baseline agora é o ERP real, com **dois** métodos, e não implementação nossa.
- [ ] **Step 3:** `README.md` §6 com F13.

---

## Riscos

| Risco | Observação |
|---|---|
| O ML ganhar "fácil demais" | `DemandaDia` é essencialmente média de meses recentes (colunas `MES_0`..`MES_4`). Um modelo com sazonalidade, promoção e calendário deve ganhar. Se ganhar muito, o resultado honesto não é comemorar: é dizer **onde** o método antigo é razoável (provavelmente curva A de giro alto) e onde falha (sazonal, promocional, intermitente). |
| Retiro usar só "Dias de Reposição" | Reenquadra o título do trabalho. Pergunta aberta em F12. |
| População pequena após filtros | Se as sugestões cobrirem poucos itens por loja, a camada A perde poder. Medir a contagem antes de concluir qualquer coisa. |
| `DemandaDia` já vir ajustada por ruptura | Não sabemos se o ERP corrige demanda em dias sem estoque. Verificar contra `Falteiro` antes de afirmar vantagem do ML nesse ponto. |

## Ordem de execução

Task 1 (remoção) → Task 2 (migration) → Tasks 3–5 (as três camadas, cada uma TDD) →
Task 6 (Worker + API) → Task 7 (UI técnica) → Task 8 (docs).
