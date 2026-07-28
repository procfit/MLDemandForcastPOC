# F10 — Isolamento por rede: plano de implementação

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe arquivos `.md` de planejamento.
> Gravado a pedido explícito do usuário em 2026-07-28, por causa do volume de
> tarefas (10 aqui + 9 em F11), que fica ruim de rastrear só na conversa.

> **Para quem for executar:** as etapas usam checkbox (`- [ ]`) para acompanhamento.

**Objetivo:** toda linha de Stage e todo job de engine pertencem a exatamente uma
rede, com integridade garantida pelo banco. Dois imports de redes distintas coexistem.

**Arquitetura:** `RedeId INT` como primeira coluna da PK das tabelas-pai (`Lojas`,
`Produtos`) e das de referência externa (`MercadoIqvia`, `SinaisExternos`), que têm FK
direta para `Redes`. As tabelas-filhas (`Vendas`, `EstoquesDiarios`, `Compras`,
`Promocoes`) recebem `RedeId` e trocam FKs simples por **compostas** `(RedeId, Sku)` /
`(RedeId, LojaId)` — o que já amarra cada linha transitivamente a `Redes`, sem o custo
de uma FK redundante no caminho do `SqlBulkCopy` de milhões de linhas.

**Stack:** SQL Server (DACPAC via `MSBuild.Sdk.SqlProj`), EF Core migrations, `SqlBulkCopy`.

## Por que `Redes` existe nos dois bancos

Não há FK entre bancos no SQL Server, e Stage/engine são separados (CLAUDE.md §4):

- **`Stage.dbo.Redes`** (DACPAC) — âncora referencial do dado staged; é o que permite FK real nas 8 tabelas.
- **`engine.Redes`** (EF) — registro do inquilino; FK real de `CargasStage`, `TreinoJobs`, `SimulacoesCompra`.

Mesmo `RedeId`, uma direção de verdade: engine é o registro, Stage é projeção que o
Worker sincroniza no import.

## Global Constraints

- `Lojas.LojaId` e `Produtos.Sku` são códigos internos do ERP e **colidem entre redes** — entram na PK depois de `RedeId`, nunca como chave única global.
- `RedeId` **não entra no CSV**. O Worker injeta a partir da `CargaStage`. Mantém intactos o contrato CSV, o Extractor, os fakers e o `CsvZipBuilder`, e impede que um cliente reivindique a rede de outro.
- `Promocoes.LojaId` é nullable (campanha nacional). Em FK composta o SQL Server não valida quando qualquer coluna é NULL — correto aqui; o vínculo com a rede segue garantido pela FK composta para `Produtos` (Sku é NOT NULL).
- Índices filtrados de `Produtos` precisam de `RedeId` como primeira coluna, senão viram varredura cross-tenant.
- Nenhum projeto novo na solução.

## Pré-requisito destrutivo — AUTORIZADO

Alterar PK exige rebuild de tabela; o `BlockOnPossibleDataLoss` do SqlPackage bloqueia a
publicação. Usuário autorizou descarte de qualquer dado/schema em 2026-07-28.

```powershell
docker exec -i <container-sql> /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P '<senha>' -Q "DROP DATABASE Stage;"
```

Não ligar `<BlockOnPossibleDataLoss>False</BlockOnPossibleDataLoss>` de forma permanente:
faria toda publicação futura descartar dado em silêncio.

---

## Task 1: Teste que falha primeiro

**Files:**
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/MultiRedeIntegrationTests.cs`

**Interfaces:**
- Consome: `CsvZipBuilder.Build(...)` de `Tests.Shared`.
- Produz: helpers de fixture `CriarRedeAsync`, `UploadAsync(zip, redeId)`, `AguardarConclusaoAsync`, `ContarAsync(alias, redeId)`.

- [ ] **Step 1: Escrever o teste**

```csharp
[Fact]
public async Task Import_de_duas_redes_preserva_os_dados_de_ambas()
{
    var rede2 = await CriarRedeAsync("Rede B", "rede-b");

    var zipA = CsvZipBuilder.Build(lojas: 2, produtos: 5, dias: 10, seed: 1);
    var zipB = CsvZipBuilder.Build(lojas: 3, produtos: 7, dias: 10, seed: 2);

    var cargaA = await UploadAsync(zipA, redeId: 1);
    await AguardarConclusaoAsync(cargaA);

    var cargaB = await UploadAsync(zipB, redeId: rede2.Id);
    await AguardarConclusaoAsync(cargaB);

    var lojasA = await ContarAsync("lojas", redeId: 1);
    var lojasB = await ContarAsync("lojas", redeId: rede2.Id);

    lojasA.Should().Be(2, "o import da rede B não pode apagar o Stage da rede A");
    lojasB.Should().Be(3);
}
```

- [ ] **Step 2: Rodar e ver falhar**

`dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests`
Esperado: FALHA — `lojasA` volta 0, porque o `DELETE FROM` sem filtro apagou tudo.

- [ ] **Step 3: Commit**

```bash
git add tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests
git commit -m "test(multi-tenant): cenário de duas redes (vermelho)"
```

---

## Task 2: `Redes` no Stage

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Database/Tables/Redes.sql`

- [ ] **Step 1: Criar a tabela**

```sql
-- Inquilinos do sistema (redes de farmácia). Mestre referencial de todo dado
-- staged: nenhuma linha de Stage existe sem uma rede dona.
-- RedeId NÃO é IDENTITY — o valor é atribuído pelo registro em engine.Redes e
-- projetado aqui pelo Worker no import. Cross-database FK não existe no SQL
-- Server; esta tabela é a âncora que permite FK real nas tabelas de dado.
CREATE TABLE dbo.Redes
(
    RedeId INT           NOT NULL,
    Nome   NVARCHAR(120) NOT NULL,
    Slug   VARCHAR(40)   NOT NULL,

    CONSTRAINT PK_Redes      PRIMARY KEY (RedeId),
    CONSTRAINT UQ_Redes_Slug UNIQUE (Slug)
);
```

---

## Task 3: Tabelas-pai — `Lojas` e `Produtos`

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Database/Tables/Produtos.sql`
- Modify: `CosmosPro.ML.DemandForCast.Database/Tables/Lojas.sql`

- [ ] **Step 1: `Produtos.sql` na forma final**

```sql
CREATE TABLE dbo.Produtos
(
    RedeId              INT             NOT NULL,
    Sku                 NVARCHAR(30)    NOT NULL,
    Nome                NVARCHAR(200)   NOT NULL,
    Categoria           NVARCHAR(80)    NULL,
    Subcategoria        NVARCHAR(80)    NULL,
    Fabricante          NVARCHAR(120)   NULL,
    PrincipioAtivo      NVARCHAR(200)   NULL,
    Apresentacao        NVARCHAR(120)   NULL,
    Ean                 VARCHAR(14)     NULL,
    RegistroAnvisa      VARCHAR(20)     NULL,
    ListaControle       VARCHAR(10)     NULL,
    ClasseTerapeutica   NVARCHAR(120)   NULL,
    Ativo               BIT             NOT NULL CONSTRAINT DF_Produtos_Ativo DEFAULT 1,

    CONSTRAINT PK_Produtos       PRIMARY KEY (RedeId, Sku),
    CONSTRAINT FK_Produtos_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId),

    INDEX IX_Produtos_PrincipioAtivo NONCLUSTERED (RedeId, PrincipioAtivo) WHERE PrincipioAtivo IS NOT NULL,
    INDEX IX_Produtos_Categoria      NONCLUSTERED (RedeId, Categoria) WHERE Categoria IS NOT NULL
);
```

- [ ] **Step 2: `Lojas.sql`** — `RedeId INT NOT NULL` como primeira coluna;
      `CONSTRAINT PK_Lojas PRIMARY KEY (RedeId, LojaId)`;
      `CONSTRAINT FK_Lojas_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId)`.
      Sem índices a ajustar.

---

## Task 4: Tabelas-filhas — FKs compostas

**Files:**
- Modify: `Database/Tables/Vendas.sql`, `EstoquesDiarios.sql`, `Compras.sql`, `Promocoes.sql`, `MercadoIqvia.sql`, `SinaisExternos.sql`

- [ ] **Step 1: `Vendas.sql` na forma final (padrão para as demais)**

```sql
CREATE TABLE dbo.Vendas
(
    RedeId          INT             NOT NULL,
    Data            DATE            NOT NULL,
    LojaId          INT             NOT NULL,
    Sku             NVARCHAR(30)    NOT NULL,
    Quantidade      DECIMAL(12,3)   NOT NULL,
    PrecoUnitario   DECIMAL(12,4)   NOT NULL,
    ValorTotal      DECIMAL(14,4)   NOT NULL,

    CONSTRAINT PK_Vendas PRIMARY KEY (RedeId, Data, LojaId, Sku),
    CONSTRAINT FK_Vendas_Produtos FOREIGN KEY (RedeId, Sku)    REFERENCES dbo.Produtos(RedeId, Sku),
    CONSTRAINT FK_Vendas_Lojas    FOREIGN KEY (RedeId, LojaId) REFERENCES dbo.Lojas(RedeId, LojaId),

    INDEX IX_Vendas_Sku_Data NONCLUSTERED (RedeId, Sku, Data) INCLUDE (LojaId, Quantidade)
);
```

- [ ] **Step 2: aplicar os deltas nas outras cinco**

| Arquivo | PK | FKs | Índices |
|---|---|---|---|
| `EstoquesDiarios.sql` | `(RedeId, Data, LojaId, Sku)` | `(RedeId,Sku)`→Produtos, `(RedeId,LojaId)`→Lojas | `IX_..._Sku_Data` → `(RedeId, Sku, Data)` |
| `Compras.sql` | `(CompraId)` inalterada (IDENTITY) | idem acima | ambos os IX ganham `RedeId` na frente |
| `Promocoes.sql` | `(PromocaoId)` inalterada | idem acima | `IX_..._Sku_DataInicio` → `(RedeId, Sku, DataInicio, DataFim)` |
| `MercadoIqvia.sql` | `(RedeId, Mes, PrincipioAtivo, UF)` | `FK_MercadoIqvia_Redes` | — |
| `SinaisExternos.sql` | `(RedeId, Data, Geografia, Tipo)` | `FK_SinaisExternos_Redes` | `IX_...` → `(RedeId, Tipo, Geografia, Data)` |

Clima e gripe por UF são idênticos entre redes, então isto duplica linhas. É
deliberado: mantém a semântica "cada import é dono completo do Stage da sua rede" e o
volume é desprezível. Compartilhar significaria o import de uma rede alterar dado
visível pela outra.

- [ ] **Step 3: publicar e commitar**

```bash
git add CosmosPro.ML.DemandForCast.Database
git commit -m "feat(stage): RedeId com FK em todas as tabelas de Stage"
```

---

## Task 5: `Redes` no engine + `RedeId` nos jobs

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Engine/Entities/Rede.cs`
- Modify: `Engine/Entities/CargaStage.cs`, `TreinoJob.cs`, `SimulacaoCompra.cs`
- Modify: `Engine/EngineDbContext.cs`

**Interfaces:**
- Produz: `Rede` (Id, Nome, Slug, CnpjRaiz, Ativo, CriadoEm); `int RedeId` nos três jobs.

- [ ] **Step 1: entidade `Rede`**

```csharp
namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Inquilino do sistema. Registro criado no onboarding da rede, antes de
/// qualquer import. Fonte de verdade do RedeId — Stage.dbo.Redes é projeção.
/// </summary>
public sealed class Rede
{
    public int Id { get; set; }
    public required string Nome { get; set; }
    /// <summary>Identificador curto e estável, usado em prefixo de blob no MinIO.</summary>
    public required string Slug { get; set; }
    public string? CnpjRaiz { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTimeOffset CriadoEm { get; set; }
}
```

- [ ] **Step 2: `public int RedeId { get; set; }`** em `CargaStage`, `TreinoJob`, `SimulacaoCompra`.

- [ ] **Step 3: mapeamento + seed**

```csharp
modelBuilder.Entity<Rede>(b =>
{
    b.ToTable("Redes");
    b.HasKey(x => x.Id);
    b.Property(x => x.Nome).IsRequired().HasMaxLength(120);
    b.Property(x => x.Slug).IsRequired().HasMaxLength(40);
    b.Property(x => x.CnpjRaiz).HasMaxLength(14);
    b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("UQ_Redes_Slug");

    b.HasData(new Rede
    {
        Id = 1, Nome = "Rede Demo", Slug = "demo", Ativo = true,
        CriadoEm = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    });
});
```

O seed da rede `demo` (Id 1) mantém sintético, testes e E2E funcionando sem UI de
cadastro. `CriadoEm` é literal fixo porque `HasData` com `DateTimeOffset.UtcNow` gera
migration nova a cada `dotnet ef`.

Em cada job, dentro do bloco existente:

```csharp
b.Property(x => x.RedeId).IsRequired();
b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId).OnDelete(DeleteBehavior.Restrict);
b.HasIndex(x => new { x.RedeId, x.Status, x.DataAgendamento })
 .HasDatabaseName("IX_CargasStage_Rede_Status_DataAgendamento");
```

O índice de polling ganha `RedeId` na frente nos três.

- [ ] **Step 4: gerar a migration**

`dotnet ef migrations add AddRedes --project CosmosPro.ML.DemandForCast.Engine`

Armadilha registrada: **não** criar `IDesignTimeDbContextFactory` neste projeto — o
tool do EF prefere o factory ao service provider e aplica no banco errado.

---

## Task 6: `TableSchemas` — coluna injetada pelo servidor

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Worker/TableSchemas.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Worker.Tests/TableSchemasParseTests.cs`

**Interfaces:**
- Produz: `Column(string Name, Type Type, bool Nullable, bool ServerSupplied = false)`.

- [ ] **Step 1: flag na `Column`**

```csharp
internal record Column(string Name, Type Type, bool Nullable, bool ServerSupplied = false);

/// <summary>RedeId nunca vem do CSV — o Worker injeta a partir da CargaStage.</summary>
private static readonly Column RedeId = new("RedeId", typeof(int), false, ServerSupplied: true);
```

- [ ] **Step 2:** `RedeId` como **primeiro** elemento de cada um dos 8 arrays de `ByTable`.

- [ ] **Step 3: teste** — colunas `ServerSupplied` não são procuradas no header; CSV sem `RedeId` continua parseando.

---

## Task 7: `CargaProcessor` — delete escopado e upsert da projeção

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Worker/CargaProcessor.cs`
- Modify: `CosmosPro.ML.DemandForCast.Worker/ImportWorker.cs`

**Interfaces:**
- Consome: `Rede` de `Engine.Entities`.
- Produz: `ProcessAsync(CargaStage carga, Rede rede, CancellationToken ct)`.

- [ ] **Step 1: assinatura**

```csharp
public async Task<long> ProcessAsync(CargaStage carga, Rede rede, CancellationToken ct)
```

- [ ] **Step 2: upsert antes dos inserts, filtro no delete**

```csharp
await UpsertRedeAsync(rede, conn, tx, ct);

foreach (var table in DeleteOrder)
{
    await using var cmd = new SqlCommand(
        $"DELETE FROM dbo.{table} WHERE RedeId = @redeId;", conn, tx);
    cmd.Parameters.AddWithValue("@redeId", rede.Id);
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync(ct);
}
```

```csharp
private static async Task UpsertRedeAsync(
    Rede rede, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
{
    await using var cmd = new SqlCommand("""
        UPDATE dbo.Redes SET Nome = @nome, Slug = @slug WHERE RedeId = @redeId;
        IF @@ROWCOUNT = 0
            INSERT INTO dbo.Redes (RedeId, Nome, Slug) VALUES (@redeId, @nome, @slug);
        """, conn, tx);
    cmd.Parameters.AddWithValue("@redeId", rede.Id);
    cmd.Parameters.AddWithValue("@nome", rede.Nome);
    cmd.Parameters.AddWithValue("@slug", rede.Slug);
    await cmd.ExecuteNonQueryAsync(ct);
}
```

`UPDATE`-então-`INSERT` em vez de `IF NOT EXISTS`: dentro da transação do import, evita
a condição de corrida do check-then-insert.

- [ ] **Step 3: injeção no `BulkInsertAsync`**

```csharp
foreach (var col in schema)
{
    if (col.ServerSupplied)
    {
        row[col.Name] = redeId;
        continue;
    }
    // ...restante inalterado...
}
```

- [ ] **Step 4: `ImportWorker`** — após o claim, carregar a `Rede` por `carga.RedeId` e
      repassar. Rede inexistente ou inativa ⇒ falhar a carga com mensagem clara, em vez
      de estourar FK.

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Worker CosmosPro.ML.DemandForCast.Engine
git commit -m "feat(worker): escopa import por rede e projeta Redes no Stage"
```

---

## Task 8: Consumidores de Stage

**Files:**
- Modify: `Worker/Training/StageObservationLoader.cs`
- Modify: `Worker/Purchasing/StageEstoqueInicialLoader.cs`
- Modify: `Worker/Training/TreinoProcessor.cs`
- Modify: `Worker/Purchasing/SimulacaoProcessor.cs`
- Modify: `ApiService/Stage/StageBrowser.cs`

- [ ] **Step 1: adicionar `redeId` e filtro**

| Arquivo | Mudança |
|---|---|
| `StageObservationLoader.cs` | `LoadAsync(int redeId, int maxSkus, ct)`; filtro nas 6 queries (`:85` top-SKUs, `:113` vendas, `:139` rupturas, `:157` produtos, `:169` lojas, `:184` promoções) |
| `StageEstoqueInicialLoader.cs` | `LoadAsync` + filtro na query de estoque (`:35`) |
| `TreinoProcessor.cs` | repassa `job.RedeId` |
| `SimulacaoProcessor.cs` | repassa `sim.RedeId` |
| `StageBrowser.cs` | `QueryAsync(..., int redeId, ...)`; `WHERE RedeId = @redeId` no `COUNT_BIG` (`:63`) e na página (`:74`) |

- [ ] **Step 2:** conferir `StageObservationLoader:85` — o ranking de top-SKUs por volume
      tem que ser **por rede**, senão a rede maior monopoliza o corte `MaxSkus` e a menor
      treina com quase nada.

---

## Task 9: API e UI

**Files:**
- Modify: endpoints de `ApiService` (`imports/upload`, `imports/synthetic`, `imports`, `training/run`, `training`, `purchasing/simulate`, `purchasing`, browse de Stage)
- Create: `GET /api/redes`
- Modify: `Web/Components/Layout/MainLayout.razor`, `Pages/Imports.razor`, `Pages/Dados.razor`, `Pages/Treinamento.razor`, `Pages/SugestaoCompra.razor`

- [ ] **Step 1:** endpoints passam a exigir `redeId`.
- [ ] **Step 2:** `GET /api/redes` para popular seletor.
- [ ] **Step 3:** `RadzenDropDown` de rede ativa no `MainLayout`, persistido em sessão.

**ANDAIME TEMPORÁRIO — registrar como dívida:** enquanto `redeId` vier do request,
qualquer um lê qualquer rede trocando o valor. F11 substitui pelo claim e o parâmetro sai
da superfície pública. **Não publicar nada com dado real de cliente antes de F11.**

---

## Task 10: Verde e documentação

- [ ] **Step 1:** `dotnet test` verde nos 13 projetos, com o teste da Task 1 incluso.
- [ ] **Step 2:** `Web.E2ETests` — selecionar rede antes de subir o ZIP.
- [ ] **Step 3:** `README.md` §6 com F10 marcada; `CLAUDE.md` §4 descrevendo `Redes` nos dois bancos e a regra de que `RedeId` nunca trafega em CSV.
- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md tests
git commit -m "docs(roadmap): F10 concluída — isolamento por rede"
```

---

## O que este plano não faz

- **Não** cria autenticação. `redeId` vem do request e é falsificável até F11.
- **Não** prefixa blobs do MinIO por rede. Não é bloqueador porque a chave é GUID.
- **Não** toca no Extractor. Ele produz os mesmos 6 CSVs.

## Ordem de execução

Task 1 (vermelho) → pré-requisito destrutivo → Tasks 2–4 (DACPAC, publica junto) →
Task 5 (migration) → Tasks 6–7 (Worker escreve) → Task 8 (Worker/API leem) →
Task 9 (superfície) → Task 10 (verde + docs).
