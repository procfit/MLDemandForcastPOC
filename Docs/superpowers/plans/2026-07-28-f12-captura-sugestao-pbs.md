# F12 — Captura da Sugestão de Compra do PBS: plano de implementação

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe arquivos `.md` de planejamento.
> Gravado a pedido explícito do usuário em 2026-07-28.

> **Depende de:** [F10 — isolamento por rede](2026-07-28-f10-isolamento-por-rede.md).

**Objetivo:** trazer para o Stage as sugestões de compra que o ERP PBS realmente gerou —
os parâmetros, a previsão de demanda do próprio ERP, o eMax/eSeg que ele calculou, o que
mandou comprar e o que o comprador aprovou.

**Por que existe:** o comparativo do mestrado é ML **contra o ERP**, não contra uma
reimplementação nossa. Com estes dados, a classe `EMaxESegPolicy` deste repo deixa de ter
função (ver [F13](2026-07-28-f13-comparacao-ml-vs-pbs.md)).

## Descobertas na instância NatusFarma (2026-07-28)

Investigado via MCP `mssql-natusfarma-pbs-prod`. **Não validado na Retiro** — a versão do
PBS pode diferir.

| Objeto PBS | Linhas | Papel |
|---|---:|---|
| `dbo.SUGESTOES_COMPRAS` | 19.183 | cabeçalho: parâmetros de cada sugestão gerada |
| `dbo.SUGESTOES_COMPRAS_RESULTADO` | 120.464.445 | linhas: item × loja, com demanda, eMax/eSeg e quantidades |
| `dbo.TIPOS_CALCULO_SUGESTAO` | 2 | `1 = Emax e Eseg`, `2 = Dias de Reposição` |

**Existem dois métodos tradicionais, não um:**

| `TIPO_CALCULO` | Descrição | Sugestões |
|---|---|---:|
| 1 | Emax e Eseg | 5.098 |
| 2 | Dias de Reposição | 14.085 |

Histórico de 2025-08-06 a 2026-07-28 — um ano completo.

Quando `TIPO_CALCULO = 2`, as colunas `ESTOQUE_SEGURANCA` e `ESTOQUE_MAXIMO` vêm **zeradas**.
Isso não é dado faltando: Dias de Reposição não usa eSeg/eMax. Confirmado na sugestão
21217 ("MATTEL", 30 dias em todas as curvas, lead time 0).

## Mapeamento PBS → Stage (confirmado com o usuário)

| Coluna PBS (`SUGESTOES_COMPRAS_RESULTADO`) | Significado |
|---|---|
| `PRODUTO` / `FILIAL` | item e loja |
| `CURVA` | classe do item (A–E) |
| `DEMANDA_DIA` | **previsão de venda diária do próprio ERP** |
| `ESTOQUE_SALDO` | estoque no momento do cálculo |
| `ESTOQUE_SEGURANCA` | o eSeg calculado |
| `ESTOQUE_MAXIMO` | o eMax calculado |
| `DIAS_ESTOQUE` | dias de cobertura da compra |
| `COMPRA_SUGERIDA` | quanto o ERP mandou comprar |
| `COMPRA_AUTORIZADA` | quanto o comprador aprovou |

## Global Constraints

- `LojaId` do Stage = `EMPRESA_USUARIA` do PBS (ver `Queries/lojas.sql`).
- **Item aberto:** `SUGESTOES_COMPRAS_RESULTADO` tem `EMPRESA` **e** `FILIAL`. Na NatusFarma os dois são iguais (ambos 86 na amostra), então a amostra não distingue. Decisão: usar `FILIAL`, e o extrator emite aviso se encontrar `EMPRESA <> FILIAL`. Validar na Retiro antes de confiar.
- O CSV de sugestões é **opcional** no ZIP. Um import sem ele tem de continuar funcionando — senão quebra o fluxo sintético e os testes existentes.
- **Integridade referencial:** as FKs de F10 exigem que todo `Sku`/`LojaId` das linhas exista em `Produtos`/`Lojas`. O extrator precisa filtrar as linhas às lojas selecionadas **e** garantir que os produtos citados entrem em `produtos.csv`. Sem isso o `SqlBulkCopy` estoura FK.
- Nunca misturar `TipoCalculo` 1 e 2 numa mesma métrica.

---

## Task 1: Tabelas de Stage

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Database/Tables/SugestoesCompra.sql`
- Create: `CosmosPro.ML.DemandForCast.Database/Tables/SugestoesCompraItens.sql`

- [ ] **Step 1: cabeçalho**

```sql
-- Cabeçalho de cada sugestão gerada pelo ERP PBS (dbo.SUGESTOES_COMPRAS).
-- SugestaoId preserva o identificador do PBS para o usuário rastrear no ERP.
-- TipoCalculo: 1 = "Emax e Eseg", 2 = "Dias de Reposição". Os dois convivem no
-- ERP e o comparativo trata cada um como baseline separado.
CREATE TABLE dbo.SugestoesCompra
(
    RedeId                    INT           NOT NULL,
    SugestaoId                BIGINT        NOT NULL,
    Descricao                 NVARCHAR(100) NULL,
    DataHora                  DATETIME2(0)  NOT NULL,
    TipoCalculo               TINYINT       NOT NULL,
    LeadTimeDias              SMALLINT      NULL,
    DiasCurvaA                SMALLINT      NOT NULL,
    DiasCurvaB                SMALLINT      NOT NULL,
    DiasCurvaC                SMALLINT      NOT NULL,
    DiasCurvaD                SMALLINT      NOT NULL,
    DiasCurvaE                SMALLINT      NOT NULL,
    Efetividade               DECIMAL(6,2)  NOT NULL,
    ConsideraPedidosPendentes BIT           NOT NULL,
    IncluiEstoqueZerado       BIT           NOT NULL,

    CONSTRAINT PK_SugestoesCompra       PRIMARY KEY (RedeId, SugestaoId),
    CONSTRAINT FK_SugestoesCompra_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId),
    CONSTRAINT CK_SugestoesCompra_TipoCalculo CHECK (TipoCalculo IN (1, 2)),

    INDEX IX_SugestoesCompra_Tipo_Data NONCLUSTERED (RedeId, TipoCalculo, DataHora)
);
```

- [ ] **Step 2: linhas**

```sql
-- Linhas do resultado da sugestão (dbo.SUGESTOES_COMPRAS_RESULTADO).
-- DemandaDia é a PREVISÃO DE DEMANDA DO PRÓPRIO ERP — é contra ela que a
-- previsão do ML é comparada diretamente (camada A do comparativo em F13).
-- EstoqueSeguranca/EstoqueMaximo vêm zerados quando TipoCalculo = 2: Dias de
-- Reposição não usa eSeg/eMax. Não é dado faltando.
-- CompraSugerida = o que o ERP mandou comprar; CompraAutorizada = o que o
-- comprador aprovou. A diferença mede a intervenção humana.
CREATE TABLE dbo.SugestoesCompraItens
(
    RedeId              INT           NOT NULL,
    SugestaoId          BIGINT        NOT NULL,
    LojaId              INT           NOT NULL,
    Sku                 NVARCHAR(30)  NOT NULL,
    Curva               CHAR(1)       NULL,
    DemandaDia          DECIMAL(12,4) NOT NULL,
    DemandaDiaPonderada DECIMAL(15,4) NULL,
    EstoqueSaldo        DECIMAL(15,3) NOT NULL,
    EstoqueSeguranca    DECIMAL(15,3) NULL,
    EstoqueMaximo       DECIMAL(15,3) NULL,
    EstoqueMinimo       DECIMAL(15,3) NULL,
    DiasEstoque         SMALLINT      NOT NULL,
    PedidosPendentes    DECIMAL(15,3) NOT NULL,
    CompraSugerida      DECIMAL(15,3) NOT NULL,
    CompraAutorizada    DECIMAL(15,3) NOT NULL,
    PrecoCompra         DECIMAL(15,4) NULL,
    FatorEmbalagem      DECIMAL(7,2)  NULL,
    Falteiro            BIT           NOT NULL,

    CONSTRAINT PK_SugestoesCompraItens PRIMARY KEY (RedeId, SugestaoId, LojaId, Sku),
    CONSTRAINT FK_SugestoesCompraItens_Sugestoes FOREIGN KEY (RedeId, SugestaoId) REFERENCES dbo.SugestoesCompra(RedeId, SugestaoId),
    CONSTRAINT FK_SugestoesCompraItens_Produtos  FOREIGN KEY (RedeId, Sku)        REFERENCES dbo.Produtos(RedeId, Sku),
    CONSTRAINT FK_SugestoesCompraItens_Lojas     FOREIGN KEY (RedeId, LojaId)     REFERENCES dbo.Lojas(RedeId, LojaId),

    INDEX IX_SugestoesCompraItens_Sku NONCLUSTERED (RedeId, Sku, LojaId) INCLUDE (DemandaDia, CompraSugerida)
);
```

---

## Task 2: Contrato CSV e Worker

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/StageContract.cs`
- Modify: `CosmosPro.ML.DemandForCast.Worker/TableSchemas.cs`
- Modify: `CosmosPro.ML.DemandForCast.Worker/CargaProcessor.cs`
- Modify: `CosmosPro.ML.DemandForCast.ApiService` — validador estrutural do ZIP
- Test: `tests/CosmosPro.ML.DemandForCast.Worker.Tests/TableSchemasParseTests.cs`

- [ ] **Step 1:** `StageContract` ganha `SugestoesCompra = "sugestoes_compra.csv"` e `SugestoesCompraItens = "sugestoes_compra_itens.csv"`.
- [ ] **Step 2:** `TableSchemas.ByTable` ganha as duas tabelas, com `RedeId` como coluna `ServerSupplied` (padrão de F10).
- [ ] **Step 3:** `DeleteOrder` — itens **antes** do cabeçalho; ambos antes de `Produtos`/`Lojas`.
      `InsertOrder` — cabeçalho depois de `Lojas`/`Produtos`, itens depois do cabeçalho.
- [ ] **Step 4:** validador do ZIP trata os dois CSVs como **opcionais**.
- [ ] **Step 5:** teste com ZIP sem os arquivos novos → import conclui normalmente.

---

## Task 3: Queries do Extractor

> **Substituída pelo plano da [F14](2026-07-28-f14-sessoes-de-comparacao.md)** (Tasks 2–3 de lá).
> O spec das sessões de comparação mudou a UX do extrator: **uma** sugestão escolhida em
> catálogo, e não uma janela de datas com múltiplos tipos de cálculo. As queries entregues
> são `catalogo_sugestoes.sql`, `sugestoes_compra.sql` e `sugestoes_compra_itens.sql`
> escopadas a uma `SUGESTAO_COMPRA`, mais a query de diagnóstico `EMPRESA <> FILIAL`
> (`sugestoes_compra_diagnostico.sql`), que sobreviveu do Step 3 daqui. O Step 4 (contagem
> prévia) foi absorvido pelo catálogo, que já traz o tamanho de cada sugestão. Os blocos SQL
> abaixo ficam como registro do desenho original.

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/Queries/sugestoes_compra.sql`
- Create: `CosmosPro.ML.DemandForCast.Extractor/Queries/sugestoes_compra_itens.sql`

- [ ] **Step 1: cabeçalho**

```sql
-- Stage.SugestoesCompra <- SUGESTOES_COMPRAS
-- Filtra por janela de data e tipo de cálculo. Só sugestões que tenham ao menos
-- uma linha nas lojas selecionadas — evita cabeçalho órfão no Stage.
SELECT
    SugestaoId                = CONVERT(bigint,   S.SUGESTAO_COMPRA),
    Descricao                 = LEFT(S.DESCRICAO, 100),
    DataHora                  = CONVERT(datetime2(0), S.DATA_HORA),
    TipoCalculo               = CONVERT(tinyint,  S.TIPO_CALCULO),
    LeadTimeDias              = CONVERT(smallint, S.LEADTIME),
    DiasCurvaA                = CONVERT(smallint, S.DIAS_CURVA_A),
    DiasCurvaB                = CONVERT(smallint, S.DIAS_CURVA_B),
    DiasCurvaC                = CONVERT(smallint, S.DIAS_CURVA_C),
    DiasCurvaD                = CONVERT(smallint, S.DIAS_CURVA_D),
    DiasCurvaE                = CONVERT(smallint, S.DIAS_CURVA_E),
    Efetividade               = CONVERT(decimal(6,2), S.EFETIVIDADE),
    ConsideraPedidosPendentes = CAST(CASE WHEN S.PEDIDOS_PENDENTES = 'S' THEN 1 ELSE 0 END AS bit),
    IncluiEstoqueZerado       = CAST(CASE WHEN S.ESTOQUE_ZERADO    = 'S' THEN 1 ELSE 0 END AS bit)
FROM dbo.SUGESTOES_COMPRAS S
WHERE S.TIPO_CALCULO IS NOT NULL
  AND S.TIPO_CALCULO IN ({{TIPOS_CALCULO}})
  AND S.DATA_HORA >= {{DATA_INICIO}}
  AND S.DATA_HORA <  {{DATA_FIM}}
  AND EXISTS (
        SELECT 1 FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
        WHERE R.SUGESTAO_COMPRA = S.SUGESTAO_COMPRA
          AND R.FILIAL IN ({{LOJAS}})
      )
ORDER BY S.SUGESTAO_COMPRA;
```

- [ ] **Step 2: linhas**

```sql
-- Stage.SugestoesCompraItens <- SUGESTOES_COMPRAS_RESULTADO
-- LojaId = FILIAL (ver item aberto no plano: EMPRESA e FILIAL coincidem na
-- NatusFarma; validar na Retiro).
-- Sku sai como texto para casar com Stage.Produtos.Sku (NVARCHAR(30)).
SELECT
    SugestaoId          = CONVERT(bigint, R.SUGESTAO_COMPRA),
    LojaId              = CONVERT(int,    R.FILIAL),
    Sku                 = CONVERT(varchar(30), R.PRODUTO),
    Curva               = CONVERT(char(1), R.CURVA),
    DemandaDia          = CONVERT(decimal(12,4), R.DEMANDA_DIA),
    DemandaDiaPonderada = CONVERT(decimal(15,4), R.DEMANDA_DIA_PONDERADA),
    EstoqueSaldo        = CONVERT(decimal(15,3), R.ESTOQUE_SALDO),
    EstoqueSeguranca    = CONVERT(decimal(15,3), R.ESTOQUE_SEGURANCA),
    EstoqueMaximo       = CONVERT(decimal(15,3), R.ESTOQUE_MAXIMO),
    EstoqueMinimo       = CONVERT(decimal(15,3), R.ESTOQUE_MINIMO),
    DiasEstoque         = CONVERT(smallint,      R.DIAS_ESTOQUE),
    PedidosPendentes    = CONVERT(decimal(15,3), R.PEDIDOS_PENDENTES),
    CompraSugerida      = CONVERT(decimal(15,3), R.COMPRA_SUGERIDA),
    CompraAutorizada    = CONVERT(decimal(15,3), R.COMPRA_AUTORIZADA),
    PrecoCompra         = CONVERT(decimal(15,4), R.PRECO_COMPRA),
    FatorEmbalagem      = CONVERT(decimal(7,2),  R.FATOR_EMBALAGEM),
    Falteiro            = CAST(CASE WHEN R.FALTEIRO = 'S' THEN 1 ELSE 0 END AS bit)
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
JOIN dbo.SUGESTOES_COMPRAS S ON S.SUGESTAO_COMPRA = R.SUGESTAO_COMPRA
WHERE S.TIPO_CALCULO IN ({{TIPOS_CALCULO}})
  AND S.DATA_HORA >= {{DATA_INICIO}}
  AND S.DATA_HORA <  {{DATA_FIM}}
  AND R.FILIAL IN ({{LOJAS}})
ORDER BY R.SUGESTAO_COMPRA, R.FILIAL, R.PRODUTO;
```

- [ ] **Step 3: query de aviso** — contar linhas com `EMPRESA <> FILIAL` na janela; se > 0, o extrator avisa o usuário em vez de seguir em silêncio.

- [ ] **Step 4: query de contagem prévia** — `COUNT(*)` das linhas na janela, exibido antes de extrair. Com 120M linhas na origem, o usuário precisa ver o tamanho antes de esperar.

---

## Task 4: Extractor — UI e serviço

> **Substituída pelo plano da [F14](2026-07-28-f14-sessoes-de-comparacao.md)** (Tasks 1 e 4 de lá).
> No lugar da seção "Sugestões de compra" com janela de datas e checkbox por tipo de cálculo,
> o extrator lista as sugestões, o usuário escolhe **uma**, e a janela do ZIP é **derivada**
> dela por `ExtractionWindow.Derive` (12 meses de histórico antes; cobertura depois). O Step 3
> daqui — união de produtos, para o `SqlBulkCopy` não estourar FK — permaneceu obrigatório e
> foi implementado com linha placeholder em `produtos.csv`, contada em
> `ZipManifest.SkusSemCadastro`. O Step 4 (avisar quando não há sugestão na janela) deixou de
> existir: sem sugestão no catálogo não há o que escolher.

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/MainForm.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionModels.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/AppConfig.cs`

- [ ] **Step 1:** seção "Sugestões de compra" no form: janela de datas, checkbox por tipo de cálculo (Emax/Eseg, Dias de Reposição), e botão "Contar antes de extrair".
- [ ] **Step 2:** reaproveita as lojas já selecionadas para o resto da extração — não duplicar seleção.
- [ ] **Step 3:** **união de produtos.** Depois de extrair as linhas, coletar os `Sku` distintos e garantir que `produtos.csv` os inclua, senão o import falha por FK. Este passo é obrigatório e é a armadilha mais provável desta fase.
- [ ] **Step 4:** avisar quando um tipo de cálculo selecionado não retornar sugestão nenhuma na janela (caso plausível na Retiro, que pode usar só um dos dois).

---

## Task 5: Visualização no Stage

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.ApiService/Stage/StageBrowser.cs`
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Dados.razor`

- [ ] **Step 1:** dois aliases novos no `StageBrowser.Tables` (`sugestoes`, `sugestoes-itens`) para o usuário conferir o que subiu.

---

## Task 6: Testes

**Files:**
- Modify: `tests/CosmosPro.ML.DemandForCast.Tests.Shared/Csv/CsvZipBuilder.cs`
- Create: `tests/CosmosPro.ML.DemandForCast.Tests.Shared/Fakers/SugestaoCompraFaker.cs`
- Modify: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/StageContractTests.cs`

- [ ] **Step 1:** faker que gera cabeçalho + itens coerentes, incluindo o caso `TipoCalculo = 2` com eSeg/eMax zerados.
- [ ] **Step 2:** `CsvZipBuilder` com flag para incluir ou não os CSVs de sugestão (o caso "não incluir" cobre a opcionalidade).
- [ ] **Step 3:** teste de import end-to-end com sugestões, conferindo contagem e que `TipoCalculo = 2` preserva os zeros.
- [ ] **Step 4:** `dotnet test` verde.

---

## Task 7: Documentação

- [ ] **Step 1:** `Docs/extracao-pbs-stage.md` — trocar a linha `MercadoIqvia ⏭️` e **adicionar** as duas tabelas novas na tabela de progresso, com as descobertas de TIPO_CALCULO.
- [ ] **Step 2:** `Docs/schema.md` com as duas tabelas.
- [ ] **Step 3:** `README.md` §6 com F12.

---

## Itens abertos

1. **`EMPRESA` vs `FILIAL`** — coincidem na NatusFarma; validar na Retiro.
2. **Qual método a Retiro usa** — se só "Dias de Reposição", o baseline eMax/eSeg do título do trabalho precisa ser reenquadrado. Pergunta em aberto com o usuário.
3. **Volume** — 120M linhas na origem. Estimar o recorte de 5 lojas × 12 meses antes de rodar na Retiro.
4. **Dias de cobertura** — confirmar com o operador que "dias de cobertura" é o que o PBS chama `DIAS_ESTOQUE` / `DIAS_CURVA_*`.

## Ordem de execução

Task 1 (DACPAC) → Task 2 (contrato + Worker) → Task 3 (queries) → Task 4 (Extractor) →
Task 5 (visualização) → Task 6 (testes verdes) → Task 7 (docs).
