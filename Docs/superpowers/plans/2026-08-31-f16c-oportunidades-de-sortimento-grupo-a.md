# F16 parte C, grupo A — Oportunidades de sortimento

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** O comprador passa a ver uma lista de produtos que o mercado vende nos bairros dele e que **não estão no cadastro da rede**, filtrada por relevância comercial.

**Architecture:** O cadastro de códigos de barras da rede vira uma tabela no banco `engine` (`RedeCatalogoEans`), alimentada por um CSV novo do extrator e **substituída inteira** a cada envio. A tela `/mercado/oportunidades` é **consulta ao vivo**: as três fontes que ela cruza (`MercadoObservacoes`, `MercadoBrickPdvs`, `RedeCatalogoEans`) vivem todas no `engine` e sobrevivem aos imports, então não há o que materializar.

**Tech Stack:** .NET 10, EF Core (banco `engine`), `Microsoft.Data.SqlClient` (extrator), Blazor SSR, xUnit + FluentAssertions, Playwright (E2E).

**Spec:** [2026-08-30-iqvia-alertas-ao-comprador-design.md](../specs/2026-08-30-iqvia-alertas-ao-comprador-design.md) — este plano cobre as regras A1, A2 e A3.

**Plano irmão, já concluído:** [grupo B](2026-08-30-f16c-alertas-de-mercado-grupo-b.md) — alertas na tabela de decisão. Leia o "Fechamento" dele antes de começar: as três armadilhas registradas lá valem aqui.

**Branch:** criar `feat/f16c-oportunidades-de-sortimento` a partir de `origin/main` **depois** que o grupo B for integrado. Enquanto o grupo B não estiver em `main`, trabalhe em cima da branch dele — este plano usa `MercadoAlertas`, `MercadoMesResolver` e a tela `/mercado`.

## Global Constraints

- **A pergunta que a tela responde é "o que eu não tenho", então o cadastro é a fonte de verdade e ele tem de estar completo.** Cadastro parcial produz falso positivo, não lista menor: o sistema afirma "você não vende isto" sobre item que a rede vende. Ver a Task 1.
- **O CSV do catálogo vai para o `engine`, NUNCA para o Stage.** O Stage é apagado a cada import (`DELETE ... WHERE RedeId`) e a tela de oportunidades não pertence a sessão nenhuma — no Stage ela zeraria no envio seguinte. Uma tabela de Stage que ninguém lê é exatamente o defeito que a F16 corrigiu ao remover `MercadoIqvia`.
- **Substituição por `RedeId` inteiro** a cada envio de catálogo. É retrato do cadastro, não série histórica — diferente de `MercadoObservacoes`, cuja recarga é por `(mês, brick)` justamente para não destruir a série.
- **`RedeId` nunca vem de rota, query ou arquivo** — sai do `IRedeContext`. 404 e não 403 para dado de outra rede.
- **Nenhum número sem denominador.** Toda contagem exibida declara sobre quantos itens foi apurada, no padrão que `SessaoResultado.ItensSemPrecoCompra` e o painel de mercado do grupo B já usam.
- **Normalização de EAN: só dígitos, sem zeros à esquerda.** Regra medida e fixada no grupo B (`MercadoSinalLoader.NormalizarEan`) — o PBS grava 14 caracteres com zero à esquerda, a IQVIA grava 13, e comparação exata casa **zero**. Reaproveite a função, não reescreva.
- **Componente Radzen não repassa atributo desconhecido ao DOM.** `data-test` vai em elemento HTML puro. Custou uma rodada de debug no grupo B.
- **Idioma:** identificadores em inglês ou no padrão do arquivo; comentários e XML docs em pt-BR quando carregam contexto de negócio. Comentário só onde o *porquê* não é óbvio.
- **Commits:** Conventional Commits em inglês no *subject*, corpo em pt-BR quando explicar negócio. Terminar com `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- **Rodar os testes:** `dotnet test -m:1` na raiz. **O `-m:1` não é opcional:** em paralelo, as suítes de integração e E2E disputam os mesmos containers persistentes e os 34 testes de E2E falham em 29ms, antes de qualquer asserção.

---

## File Structure

**Criar:**

| arquivo | responsabilidade |
|---|---|
| `Docs/consultas/cobertura-de-ean-no-cadastro.sql` | consulta do portão (Task 1), rodada pelo Perez |
| `CosmosPro.ML.DemandForCast.Engine/Entities/RedeCatalogoEan.cs` | a entidade do catálogo |
| `CosmosPro.ML.DemandForCast.Extractor/Queries/catalogo_eans.sql` | extração do mestre inteiro no PBS |
| `CosmosPro.ML.DemandForCast.ApiService/Mercado/MercadoOportunidadesQuery.cs` | regras A1 e A2, em consulta testável |
| `CosmosPro.ML.DemandForCast.Web/Components/Pages/Oportunidades.razor` | a tela (regra A3) |
| `tests/CosmosPro.ML.DemandForCast.ApiService.Tests/MercadoOportunidadesQueryTests.cs` | o corte de relevância, em teste puro |
| `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/OportunidadesIntegrationTests.cs` | A1 e A2 contra banco real |

**Modificar:**

| arquivo | mudança |
|---|---|
| `CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs` | `DbSet` + configuração de `RedeCatalogoEan` |
| `CosmosPro.ML.DemandForCast.Engine/Migrations/` | migration `AddRedeCatalogoEans` |
| `CosmosPro.ML.DemandForCast.Extractor/StageContract.cs` | o CSV novo no contrato |
| `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs` | gerar o CSV |
| `CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj` | versão `0.17.0` → `0.18.0` |
| `CosmosPro.ML.DemandForCast.ApiService/Imports/ImportSchemas.cs` | o CSV como arquivo **opcional** |
| `CosmosPro.ML.DemandForCast.Worker/CargaProcessor.cs` | upsert do catálogo no `engine` |
| `CosmosPro.ML.DemandForCast.ApiService/Mercado/MercadoEndpoints.cs` | `GET /oportunidades` |
| `CosmosPro.ML.DemandForCast.Web/MercadoApiClient.cs` | cliente do endpoint novo |
| `CosmosPro.ML.DemandForCast.Web/Components/Pages/DadosMercado.razor` | link para a tela nova |
| `CosmosPro.ML.DemandForCast.Web/Components/Layout/NavMenu.razor` | item de menu |

---

## Antes das Tasks 5 a 8: leia o arquivo antes de editar

As Tasks 1 a 4 vêm com o conteúdo completo. As Tasks 5 a 8 vêm com as **interfaces exatas e o código de teste completo**, mas não com o corpo da implementação: elas editam arquivos grandes e já existentes, e código inventado contra eles não compila — o que atrasa mais do que uma instrução precisa. Os testes são o contrato.

---

## Task 1: Portão — o cadastro tem código de barras suficiente?

**Este é um portão, e ele pode mudar o desenho da tela antes de a tela existir.**

**Por que primeiro.** A1 responde "este produto está no meu cadastro?" comparando código de barras. Produto que a rede **tem** mas cujo cadastro não registra o código fica invisível para a comparação, e a tela afirma "você não vende isto" sobre algo que a rede vende. Isso não é ruído: é a tela mentindo na direção que faz o comprador comprar o que já tem.

**MEDIDO EM 2026-08-31, direto na instância da Natusfarma** (MCP `mssql-natusfarma-pbs-prod`,
consulta de agregado, sem leitura de venda). O portão **passa com folga** — e a leitura que eu
tinha feito antes estava enganada.

| medida | valor |
|---|---|
| produtos no cadastro | 79.873 |
| **produtos ativos** | **32.215 (40,3%)** |
| ativos com EAN usável (formatado, não interno) | **29.053** |
| **cobertura de EAN sobre o cadastro ATIVO** | **90,2%** |
| ativos sem EAN usável | 3.162 |
| inativos com EAN usável | 15 |

**O 63,6% "sem código de barras" que eu havia calculado do ZIP contava produto morto.** Dos
79.873 registros, 47.658 estão **inativos** — cadastro descontinuado, duplicado, código antigo.
Eles não respondem "a rede vende isto"; são histórico. E são justamente eles que não têm EAN:
dos 29.068 registros com EAN usável, **29.053 são ativos** — sobram 15 inativos.

**Onde está a lacuna, por seção do cadastro** (ativos sem EAN usável / ativos na seção):

| seção | sem EAN | total ativo | cobertura |
|---|---|---|---|
| `NÃO DEFINIDO` | **2.756** | 3.339 | 17,5% |
| `USO_CONSUMO` | 44 | 78 | 43,6% |
| `NAO_MEDICAMENTO_PEC` | 192 | 14.095 | **98,6%** |
| `RX_PROMOVIDO` | 39 | 3.917 | **99,0%** |
| `RX_GENERICO` | 19 | 2.162 | **99,1%** |
| `MIP_MARCA` | 15 | 1.062 | **98,6%** |
| `NAO_MEDICAMENTO_NTR` | 57 | 2.076 | 97,3% |
| `NAO_MEDICAMENTO_OTC` | 14 | 1.347 | 99,0% |
| `NAO_MEDICAMENTO_PAC` | 11 | 1.864 | 99,4% |
| `MERCEARIA` | 9 | 1.450 | 99,4% |

**87% da lacuna está numa seção só: `NÃO DEFINIDO`** — 2.756 dos 3.162. Cadastro sem seção
definida, com 17,5% de cobertura de EAN, é registro incompleto, não produto de gôndola.
`USO_CONSUMO` (44) é material de consumo da própria loja, que a IQVIA não cobre.

**Nas seções que a IQVIA de fato cobre — medicamento, MIP, perfumaria, mercearia — a lacuna é
de 362 produtos em ~28.800, ou 1,3%.** É esse o teto do falso positivo, e ele fica na faixa
"abaixo de 10%" da regra de decisão: **mitigação A, declaração na tela, sem coluna de
casamento por nome.**

**Duas ressalvas que não dá para eliminar daqui.**

1. **Isto é a Natusfarma, não a Retiro.** Mesmo software de PBS, mas higiene de cadastro é por
   rede. O número da Retiro pode ser outro; o método e a ordem de grandeza transferem.
2. **Não há acesso direto ao SQL Server da Retiro.** A medição lá sai **pelo extrator**: o
   `catalogo_eans.csv` da Task 3 já devolve exatamente o que o portão precisa (uma linha por
   produto com EAN usável), e a contagem de ativos sem EAN vira um **aviso do próprio
   extrator**, na tela de extração. Ver o Step 4 abaixo.

**Consequência de tamanho para a Task 3:** o `catalogo_eans.csv` sai com ~29 mil linhas, e não
79 mil. O filtro `WHERE EANP.EAN_FORMATADO IS NOT NULL` descarta os dois terços que são
cadastro morto sem código. Arquivo de poucas centenas de KB.

**Files:**
- Create: `Docs/consultas/cobertura-de-ean-no-cadastro.sql`


**Interfaces:**
- Consumes: nada.
- Produces: a taxa de cobertura de EAN no cadastro da Retiro, e a decisão de mitigação (§ Step 4).

- [ ] **Step 1: Escrever a consulta**

Criar `Docs/consultas/cobertura-de-ean-no-cadastro.sql`. **Já rodada na Natusfarma** (resultado
acima); ela fica no repositório como **especificação do que o extrator vai contar** no Step 5 e
como forma de repetir a medição em qualquer PBS que se tenha acesso direto.

A versão abaixo usa `OUTER APPLY`. A que rodou no MCP da Natus foi reformulada com `LEFT JOIN`
porque o `queryBuilder` do MCP não aceita `OUTER APPLY` — as duas contam a mesma coisa; a de
`LEFT JOIN` precisa de `COUNT(DISTINCT P.PRODUTO)` para o produto com dois EANs não contar duas
vezes.

```sql
-- Quantos produtos do cadastro da rede têm código de barras registrado.
--
-- Por que importa: a tela de oportunidades de sortimento responde "o mercado vende
-- isto e você não tem" comparando código de barras. Produto que a rede TEM mas cujo
-- cadastro não registra o código fica invisível para a comparação, e a tela afirma
-- "você não vende isto" sobre algo que ela vende.
--
-- A mesma preferência de EAN que produtos.sql usa: principal antes de secundário,
-- externo antes de interno. EAN interno (código de balança, etiqueta da loja) não
-- casa com a IQVIA e não deve contar como cobertura.
SELECT
    Produtos              = COUNT(*),
    ProdutosAtivos        = SUM(CASE WHEN P.CADASTRO_ATIVO = 'S' THEN 1 ELSE 0 END),
    ComEanQualquer        = SUM(CASE WHEN E.QtdEan       > 0 THEN 1 ELSE 0 END),
    ComEanNaoInterno      = SUM(CASE WHEN E.QtdNaoInterno > 0 THEN 1 ELSE 0 END),
    AtivosComEanNaoInterno = SUM(CASE WHEN P.CADASTRO_ATIVO = 'S' AND E.QtdNaoInterno > 0
                                      THEN 1 ELSE 0 END)
FROM dbo.PRODUTOS P
OUTER APPLY (
    SELECT QtdEan        = COUNT(*),
           QtdNaoInterno = SUM(CASE WHEN X.EAN_INTERNO = 'N' THEN 1 ELSE 0 END)
    FROM   dbo.PRODUTOS_EAN X
    WHERE  X.PRODUTO = P.PRODUTO
) E;
```

- [x] **Step 2: Rodar na Natusfarma** — feito em 2026-08-31, resultado acima.

- [x] **Step 3: Localizar a lacuna por seção** — feito. A quebra por seção respondeu a
      pergunta melhor que o casamento por nome que este step previa: 87% da lacuna está em
      `NÃO DEFINIDO`, que é cadastro incompleto e não produto de gôndola. Nas seções que a
      IQVIA cobre, a cobertura de EAN é de 98,6% a 99,4%.

      O casamento por nome **fica de reserva**: se a Retiro medir abaixo de 90% nas seções
      farma, ele volta como forma de estimar quantos dos ausentes a rede tem sob cadastro sem
      código. Com 99%, ele mediria ruído.

- [x] **Step 4: Aplicar a regra de decisão** — 1,3% nas seções que a IQVIA cobre, faixa
      "abaixo de 10%": **mitigação A**, declaração na tela, sem coluna de casamento por nome.

      As faixas ficam registradas porque a medição da Retiro ainda vai acontecer: abaixo de
      10% → só a declaração; entre 10% e 40% → declaração mais coluna "possivelmente já
      cadastrado"; acima de 40% → parar e reabrir a spec, porque aí o caminho é cadastro no
      PBS e não software.

- [ ] **Step 5: O extrator passa a declarar a cobertura de EAN do cadastro** (novo, veio da
      medição). Não há acesso direto ao SQL Server da Retiro, então a medição de lá vem pelo
      extrator: ao gerar o `catalogo_eans.csv`, contar também os **produtos ativos sem EAN
      usável** e mostrar no log e na confirmação da extração, no mesmo lugar onde
      `SkusSemCadastro` já aparece.

      O número viaja no `manifesto.json` (campo novo, `ProdutosAtivosSemEan`) para a tela de
      oportunidades poder declará-lo sem consultar o PBS. Sem isso, a frase da Task 7 fica
      sem o N e o comprador não sabe o tamanho da ressalva.

      **Contagem por seção não entra.** O que muda a decisão é o total nas seções que a IQVIA
      cobre, e a quebra por seção só serviu para eu entender de onde vinha a lacuna — ela não
      é dado que o comprador aja sobre.

- [ ] **Step 5: Commit da consulta e do resultado**

```bash
git add Docs/consultas/cobertura-de-ean-no-cadastro.sql \
        Docs/superpowers/plans/2026-08-31-f16c-oportunidades-de-sortimento-grupo-a.md
git commit -m "docs(mercado): gate the assortment screen on catalog EAN coverage

A tela de oportunidades afirma 'o mercado vende isto e voce nao tem'
comparando codigo de barras. Produto que a rede tem sem codigo registrado
fica invisivel e a tela mente na direcao que faz o comprador comprar o que
ja tem -- por isso a medicao vem antes do desenho, nao depois.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 2: A tabela do catálogo no banco `engine`

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Engine/Entities/RedeCatalogoEan.cs`
- Modify: `CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs`
- Create: migration `AddRedeCatalogoEans`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/OportunidadesIntegrationTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `RedeCatalogoEan { int RedeId; string Ean; string Sku; string? Nome; }`, `EngineDbContext.RedeCatalogoEans`, PK `(RedeId, Ean)`.

- [ ] **Step 1: Escrever o teste que falha**

Criar `OportunidadesIntegrationTests.cs`. Copiar de `MercadoSinalIntegrationTests.cs` (grupo B) o `[Collection(AspireCollection.Name)]`, o `AbrirEngineAsync` e o `EnsureRedeAsync` — não invente fixture nova.

```csharp
    [Fact]
    public async Task O_catalogo_faz_round_trip_e_a_recarga_substitui_a_rede_inteira()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Rede das oportunidades", Slug);

        await SubstituirCatalogoAsync(redeId,
        [
            new() { RedeId = redeId, Ean = "7891721201806", Sku = "100", Nome = "GLIFAGE XR 500MG" },
            new() { RedeId = redeId, Ean = "7896714231143", Sku = "200", Nome = "NEOSORO AD" },
        ]);

        await using (var db = await AbrirEngineAsync(ct))
        {
            (await db.RedeCatalogoEans.CountAsync(c => c.RedeId == redeId, ct)).Should().Be(2);
        }

        // Segundo envio com UM item: o catálogo é retrato, não série. O item que saiu do
        // cadastro tem de sair da tabela, senão a tela deixa de oferecer como oportunidade
        // um produto que a rede descadastrou.
        await SubstituirCatalogoAsync(redeId,
        [
            new() { RedeId = redeId, Ean = "7891721201806", Sku = "100", Nome = "GLIFAGE XR 500MG" },
        ]);

        await using var leitura = await AbrirEngineAsync(ct);
        var restantes = await leitura.RedeCatalogoEans
            .Where(c => c.RedeId == redeId).Select(c => c.Ean).ToListAsync(ct);

        restantes.Should().BeEquivalentTo(["7891721201806"]);
    }

    [Fact]
    public async Task Catalogo_de_uma_rede_nao_alcanca_a_outra()
    {
        // A substituição é por RedeId. Um DELETE sem o filtro apagaria o catálogo do
        // vizinho e a tela dele passaria a oferecer o cadastro inteiro como oportunidade.
        var ct = TestContext.Current.CancellationToken;
        var redeA = await EnsureRedeAsync("Oportunidades rede A", Slug + "-a");
        var redeB = await EnsureRedeAsync("Oportunidades rede B", Slug + "-b");

        await SubstituirCatalogoAsync(redeA,
            [new() { RedeId = redeA, Ean = "111", Sku = "A1", Nome = "DA REDE A" }]);
        await SubstituirCatalogoAsync(redeB,
            [new() { RedeId = redeB, Ean = "222", Sku = "B1", Nome = "DA REDE B" }]);

        await SubstituirCatalogoAsync(redeA,
            [new() { RedeId = redeA, Ean = "333", Sku = "A2", Nome = "DA REDE A, DE NOVO" }]);

        await using var db = await AbrirEngineAsync(ct);
        (await db.RedeCatalogoEans.Where(c => c.RedeId == redeB).Select(c => c.Ean).ToListAsync(ct))
            .Should().BeEquivalentTo(["222"], "a recarga da rede A não pode tocar a rede B");
    }
```

O helper `SubstituirCatalogoAsync(int redeId, List<RedeCatalogoEan> itens)` faz `DELETE WHERE RedeId` + insert, na mesma transação — é a operação que a Task 4 vai exercitar de verdade pelo import.

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter OportunidadesIntegrationTests`
Expected: FAIL na compilação — `RedeCatalogoEan` não existe.

- [ ] **Step 3: Escrever a entidade**

Criar `CosmosPro.ML.DemandForCast.Engine/Entities/RedeCatalogoEan.cs`:

```csharp
namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Retrato do cadastro de produtos da rede, reduzido ao que a comparação com a IQVIA
/// precisa: o código de barras. Uma linha por EAN.
///
/// <para>
/// <b>Vive no banco `engine`, e não no Stage, por ciclo de vida.</b> A tela de
/// oportunidades não pertence a sessão nenhuma, e cada import apaga o Stage da rede
/// (<c>DELETE ... WHERE RedeId</c>) — no Stage este catálogo zeraria no envio seguinte e
/// a tela passaria a oferecer o cadastro inteiro como oportunidade de sortimento.
/// </para>
///
/// <para>
/// <b>Substituição por <c>RedeId</c> inteiro a cada envio</b>, e não por chave parcial:
/// é retrato, não série histórica. Produto que a rede descadastrou tem de sair, senão a
/// tela deixa de oferecer como oportunidade algo que ela já teve. Diferente de
/// <see cref="MercadoObservacao"/>, cuja recarga é por (mês, brick) justamente para
/// preservar a série.
/// </para>
///
/// <para>
/// <b>Não substitui <c>Stage.Produtos</c>.</b> Aquele é escopado aos SKUs da sugestão e
/// tem a hierarquia comercial; este é o mestre inteiro e só tem o código. As duas coisas
/// respondem perguntas diferentes: "como este item da compra se comporta" e "este item
/// do mercado existe no meu cadastro".
/// </para>
/// </summary>
public sealed class RedeCatalogoEan
{
    public int RedeId { get; set; }

    /// <summary>
    /// Só dígitos, sem zeros à esquerda — normalizado na gravação pela mesma regra do
    /// sinal de mercado. O PBS grava 14 caracteres com zero à esquerda e a IQVIA grava
    /// 13; comparação exata casa <b>zero</b>, e a falha é silenciosa.
    /// </summary>
    public required string Ean { get; set; }

    /// <summary>Código do produto no ERP, para a tela poder citar o cadastro.</summary>
    public required string Sku { get; set; }

    /// <summary>
    /// Nome no cadastro da rede. Serve ao casamento por nome que estima falso positivo
    /// (produto que a rede tem sob cadastro sem código de barras) — ver a Task 1 do plano.
    /// </summary>
    public string? Nome { get; set; }
}
```

- [ ] **Step 4: Configurar no `EngineDbContext`**

`DbSet` junto aos demais de mercado:

```csharp
    public DbSet<RedeCatalogoEan> RedeCatalogoEans => Set<RedeCatalogoEan>();
```

Configuração, no bloco dos `Mercado*`:

```csharp
        modelBuilder.Entity<RedeCatalogoEan>(b =>
        {
            b.ToTable("RedeCatalogoEans");
            b.HasKey(x => new { x.RedeId, x.Ean });

            // varchar e não nvarchar: EAN é dígito. Espelha MercadoProduto.Ean, que é o
            // outro lado do join -- collation ou tipo divergente aqui produz table scan
            // sem erro nenhum.
            b.Property(x => x.Ean).HasMaxLength(14).IsUnicode(false);
            b.Property(x => x.Sku).HasMaxLength(30);
            b.Property(x => x.Nome).HasMaxLength(200);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);
        });
```

- [ ] **Step 5: Gerar a migration**

```bash
dotnet ef migrations add AddRedeCatalogoEans \
  --project CosmosPro.ML.DemandForCast.Engine \
  --startup-project CosmosPro.ML.DemandForCast.ApiService
```

Conferir no arquivo gerado: PK composta `(RedeId, Ean)`, `Ean` como `varchar(14)`, FK `Restrict`. **Não** criar `IDesignTimeDbContextFactory` no projeto Engine — o EF tool o prefere ao service provider do Aspire e aplica a migration no banco errado.

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter OportunidadesIntegrationTests`
Expected: PASS, 2 testes.

- [ ] **Step 7: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Engine/ tests/
git commit -m "feat(mercado): store the network EAN catalog in the engine database

Retrato do cadastro reduzido ao codigo de barras, substituido inteiro por rede
a cada envio -- produto descadastrado tem de sair, senao a tela oferece como
oportunidade algo que a rede ja teve.

No engine e nao no Stage por ciclo de vida: a tela de oportunidades nao
pertence a sessao nenhuma, e cada import apaga o Stage da rede. No Stage este
catalogo zeraria no envio seguinte e a tela passaria a oferecer o cadastro
inteiro como oportunidade.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 3: O extrator passa a trazer o catálogo

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/Queries/catalogo_eans.sql`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/StageContract.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/StageContractTests.cs` (existente)

**Interfaces:**
- Consumes: nada.
- Produces: entrada `catalogo_eans.csv` no ZIP, header `Sku,Ean,Nome`.

- [ ] **Step 1: Escrever o teste que falha**

Em `StageContractTests.cs`, seguindo a forma dos testes de contrato que já existem lá:

```csharp
    [Fact]
    public void O_catalogo_de_eans_esta_no_contrato_e_na_ordem_de_escrita()
    {
        StageContract.Headers.Should().ContainKey(StageContract.CatalogoEans);
        StageContract.Headers[StageContract.CatalogoEans].Should().Equal(["Sku", "Ean", "Nome"]);
        StageContract.WriteOrder.Should().Contain(StageContract.CatalogoEans);
    }

    [Fact]
    public void O_catalogo_e_escrito_depois_dos_produtos_da_sugestao()
    {
        // Ordem importa para o log fazer sentido: o comprador vê "produtos da sugestão:
        // 43" e depois "catálogo completo: 79.711", e a diferença entre os dois números é
        // exatamente o motivo pelo qual este arquivo existe.
        var ordem = StageContract.WriteOrder.ToList();
        ordem.IndexOf(StageContract.CatalogoEans)
             .Should().BeGreaterThan(ordem.IndexOf(StageContract.Produtos));
    }
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests --filter StageContractTests`
Expected: FAIL na compilação — `StageContract.CatalogoEans` não existe.

- [ ] **Step 3: Escrever a consulta do PBS**

Criar `CosmosPro.ML.DemandForCast.Extractor/Queries/catalogo_eans.sql`:

```sql
-- Catálogo de códigos de barras da rede: o mestre INTEIRO, sem escopo por sugestão.
--
-- Por que sem escopo, ao contrário de produtos.sql: este arquivo existe para responder
-- "este produto do mercado está no meu cadastro?", e um cadastro escopado à sugestão
-- responde outra pergunta -- "está nesta compra?". Contra o escopado, todo produto fora
-- da sugestão parece ausente do cadastro, e a tela de oportunidades vira lista de itens
-- que a rede já tem.
--
-- Só código e nome, sem hierarquia nem preço: é o menor recorte que fecha a comparação.
-- No cadastro medido, 79.711 produtos dão poucos MB.
--
-- Mesma preferência de EAN de produtos.sql: principal antes de secundário, externo antes
-- de interno. EAN interno (balança, etiqueta da loja) não existe no cadastro da IQVIA e
-- contá-lo como cobertura inflaria a comparação em silêncio.
SELECT
    Sku  = CONVERT(varchar(30), P.PRODUTO),
    Ean  = EANP.EAN_FORMATADO,
    Nome = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(P.DESCRICAO)), ''), P.DESCRICAO_REDUZIDA), 200)
FROM dbo.PRODUTOS P
OUTER APPLY (
    SELECT TOP 1 E.EAN_FORMATADO
    FROM dbo.PRODUTOS_EAN E
    WHERE E.PRODUTO = P.PRODUTO
    ORDER BY CASE WHEN E.EAN_PRINCIPAL = 'S' THEN 0 ELSE 1 END,
             CASE WHEN E.EAN_INTERNO   = 'N' THEN 0 ELSE 1 END,
             E.PRODUTO_EAN
) EANP
WHERE EANP.EAN_FORMATADO IS NOT NULL
ORDER BY P.PRODUTO;
```

**`WHERE EANP.EAN_FORMATADO IS NOT NULL` é decisão, não filtro trivial:** produto sem código não serve à comparação e ocuparia dois terços do arquivo. Quem precisa do número de produtos sem código é a Task 1, que o mede na origem.

- [ ] **Step 4: Declarar no contrato**

Em `StageContract.cs`:

```csharp
    public const string CatalogoEans = "catalogo_eans.csv";
```

No dicionário `Headers`: `[CatalogoEans] = ["Sku", "Ean", "Nome"],`
Em `WriteOrder`: depois de `Produtos`.

- [ ] **Step 5: Gerar o arquivo na extração**

Em `ExtractionService.cs`, junto às demais chamadas de `CopyQuery`, **sem** passar `skusCsv` — este é o único CSV que ignora o escopo por SKU de propósito. Somar 1 no total de etapas do `progress`.

- [ ] **Step 6: Subir a versão**

No `.csproj`: `<Version>0.17.0</Version>` → `<Version>0.18.0</Version>`.

**Não é formalidade.** O `0.17.0` do repositório emite `Cnpj` em `lojas.csv` e o `0.17.0` que o comprador tem não emite — dois executáveis com o mesmo número e comportamentos diferentes, e foi isso que fez o grupo B chegar ao fim sem dado na tela. Um número novo é o que permite dizer ao comprador qual build ele precisa.

- [ ] **Step 7: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests`
Expected: PASS. Os testes de compatibilidade de import (`ImportCompatibilityTests`) também precisam continuar verdes — entrada nova no ZIP não pode invalidar ZIP antigo.

- [ ] **Step 8: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/ tests/
git commit -m "feat(extractor): export the whole EAN catalog, not just the suggestion

O ZIP passa a trazer catalogo_eans.csv com o mestre inteiro. Sem escopo por
sugestao, ao contrario de produtos.csv: contra um cadastro escopado, todo
produto fora da sugestao parece ausente do cadastro, e a tela de oportunidades
viraria lista de itens que a rede ja tem.

Versao 0.17.0 -> 0.18.0. O 0.17.0 do repositorio emite Cnpj e o 0.17.0 que o
comprador usa nao emite -- dois executaveis com o mesmo numero e comportamentos
diferentes. Um numero novo e o que permite dizer qual build ele precisa.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 4: O import grava o catálogo no `engine`

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.ApiService/Imports/ImportSchemas.cs`
- Modify: `CosmosPro.ML.DemandForCast.Worker/CargaProcessor.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/OportunidadesIntegrationTests.cs`

**Interfaces:**
- Consumes: `RedeCatalogoEan` (Task 2), `catalogo_eans.csv` (Task 3), `MercadoSinalLoader.NormalizarEan` — **extraia-a para um helper compartilhado** (`Engine/Mercado/Ean.cs`) em vez de duplicar; ela é a regra medida que faz o join funcionar.
- Produces: catálogo em `engine.RedeCatalogoEans` após cada import que traga o CSV.

- [ ] **Step 1: Escrever o teste que falha**

```csharp
    [Fact]
    public async Task Import_com_catalogo_grava_no_engine_e_sobrevive_ao_import_seguinte()
    {
        // O ponto do teste: o catálogo NÃO pode morar no Stage. Depois de um segundo
        // import (que apaga o Stage da rede), ele tem de continuar de pé -- senão a tela
        // de oportunidades zera a cada envio de sessão.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Catalogo pelo import", Slug + "-import");

        await ImportarZipAsync(redeId, comCatalogo: true, ct);

        await using (var db = await AbrirEngineAsync(ct))
        {
            (await db.RedeCatalogoEans.CountAsync(c => c.RedeId == redeId, ct))
                .Should().BeGreaterThan(0);
        }

        // Segundo import SEM catálogo (ZIP de extrator antigo): o catálogo anterior fica.
        // Apagá-lo aqui puniria o comprador por usar um build velho, tirando uma tela que
        // funcionava.
        await ImportarZipAsync(redeId, comCatalogo: false, ct);

        await using var depois = await AbrirEngineAsync(ct);
        (await depois.RedeCatalogoEans.CountAsync(c => c.RedeId == redeId, ct))
            .Should().BeGreaterThan(0, "ZIP sem catálogo não apaga o catálogo que existia");
    }

    [Fact]
    public async Task O_ean_e_normalizado_na_gravacao()
    {
        // O PBS manda 14 caracteres com zero à esquerda. Se ele entrar cru, o join com
        // MercadoObservacoes (13 dígitos) casa ZERO -- silenciosamente, e a tela mostra o
        // cadastro inteiro como oportunidade.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Ean normalizado", Slug + "-ean");

        await ImportarZipAsync(redeId, comCatalogo: true, ct,
            linhasDoCatalogo: [("100", "07896094928060", "AAS INFANTIL 10 COMPRIMIDOS")]);

        await using var db = await AbrirEngineAsync(ct);
        var eans = await db.RedeCatalogoEans
            .Where(c => c.RedeId == redeId).Select(c => c.Ean).ToListAsync(ct);

        eans.Should().BeEquivalentTo(["7896094928060"], "sem o zero à esquerda");
    }
```

`ImportarZipAsync` monta o ZIP com `CsvZipBuilder` (de `tests/CosmosPro.ML.DemandForCast.Tests.Shared/Csv/`), sobe pelo endpoint de import real e espera a carga concluir — copie a forma de `ImportsIntegrationTests`.

- [ ] **Step 2: Rodar e confirmar que falha**

Expected: FAIL — o CSV é ignorado, `RedeCatalogoEans` fica vazia.

- [ ] **Step 3: Declarar o CSV como opcional**

Em `ImportSchemas.cs`, no dicionário `OptionalFiles`:

```csharp
        // Catálogo completo de códigos de barras. Opcional: ZIP de extrator anterior à
        // 0.18.0 não o traz, e recusar o import puniria o comprador por usar um build
        // velho. Sem ele a tela de oportunidades não roda e diz isso.
        ["catalogo_eans.csv"] = ["Sku", "Ean"],
```

`Nome` fica fora da validação de header de propósito: ele é conveniência de exibição, e exigi-lo quebraria um ZIP que trouxesse só as duas colunas essenciais.

- [ ] **Step 4: Gravar no `engine` durante o import**

Em `CargaProcessor.cs`, depois das cargas do Stage: ler `catalogo_eans.csv`, normalizar o EAN, deduplicar por EAN (o mestre pode ter dois SKUs com o mesmo código — o primeiro vence, e é determinístico porque a consulta ordena por `PRODUTO`), e fazer `DELETE WHERE RedeId` + `SqlBulkCopy` **na mesma transação**, no banco `engine`.

**Ausência do arquivo não é erro e não apaga nada:** sem o CSV, o `DELETE` não roda. Apagar aqui tiraria uma tela que funcionava porque o comprador usou um extrator velho.

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter OportunidadesIntegrationTests`
Expected: PASS, 4 testes.

- [ ] **Step 6: Commit**

```bash
git add CosmosPro.ML.DemandForCast.ApiService/ CosmosPro.ML.DemandForCast.Worker/ \
        CosmosPro.ML.DemandForCast.Engine/Mercado/Ean.cs tests/
git commit -m "feat(import): load the EAN catalog into the engine database

O CSV novo e opcional e vai para o engine, nao para o Stage: depois de um
segundo import -- que apaga o Stage da rede -- o catalogo tem de continuar de
pe, senao a tela de oportunidades zera a cada envio de sessao.

ZIP sem o arquivo nao apaga o catalogo que existia. Apagar puniria o comprador
por usar um build velho, tirando uma tela que funcionava.

A normalizacao do EAN saiu do MercadoSinalLoader para um helper compartilhado:
ela e a regra medida que faz o join funcionar, e duplica-la e convidar as duas
copias a divergirem.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 5: As regras A1 e A2 em consulta

**Files:**
- Create: `CosmosPro.ML.DemandForCast.ApiService/Mercado/MercadoOportunidadesQuery.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.Tests/MercadoOportunidadesQueryTests.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/OportunidadesIntegrationTests.cs`

**Interfaces:**
- Consumes: `RedeCatalogoEan` (Task 2), `MercadoObservacao`, `MercadoProduto`, `MercadoBrickPdv`.
- Produces:
  - `record OportunidadeDeSortimento(string Ean, string Brick, string Descricao, string? Laboratorio, string? AreaFarmacia, string? Classe4, decimal UnidadesConcorrentes, decimal ValorCpp, int PdvsConcorrentes)`
  - `record OportunidadesPagina(IReadOnlyList<OportunidadeDeSortimento> Itens, int Total, DateOnly Mes, int EansNoCatalogo)`
  - `MercadoOportunidadesQuery.CorteMinimoPadrao` — `const decimal` = `200m`
  - `MercadoOportunidadesQuery.PassaNoCorte(decimal unidades, decimal corteMinimo) -> bool` — a decisão de corte, separada da consulta para ser testável sem banco
  - `MercadoOportunidadesQuery.ConsultarAsync(EngineDbContext db, int redeId, decimal corteMinimo, string? brick, string? areaFarmacia, int skip, int take, CancellationToken ct)`

**`EansDoMercado` ficou fora do contrato de propósito.** Ele era um campo que nenhum teste
e nenhuma tela leem — e número persistido que ninguém lê envelhece sem que teste ou tela
denunciem erro nele. Se a tela passar a querer "N EANs no mercado" como contexto, ele volta
com um leitor.

- [ ] **Step 1: Escrever os testes**

**Puro** (`ApiService.Tests`), sobre a decisão de corte — não precisa de banco:

```csharp
    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(201, true)]
    public void O_corte_e_inclusivo_no_valor_configurado(int unidades, bool entra)
    {
        MercadoOportunidadesQuery.PassaNoCorte(unidades, corteMinimo: 200m).Should().Be(entra);
    }

    [Fact]
    public void O_corte_padrao_e_duzentas_unidades()
    {
        // Calibrado em junho/2026 para render ~156 avisos e ~116 produtos, faixa que um
        // comprador olha. Sem corte a regra devolve 44.874 avisos e a tela é abandonada.
        MercadoOportunidadesQuery.CorteMinimoPadrao.Should().Be(200m);
    }
```

**Integração**, sobre A1 e A2 contra banco real:

```csharp
    [Fact]
    public async Task Traz_o_que_o_mercado_vende_e_o_cadastro_nao_tem()
    {
        // Cenário: 3 EANs no mercado do brick, 1 deles no catálogo da rede.
        // A1 tem de devolver os outros 2 -- e nunca o que está no catálogo.
        var cenario = await SemearMercadoEcatalogoAsync(
            noMercado: [("111", 500m), ("222", 300m), ("333", 50m)],
            noCatalogo: ["111"]);

        var pagina = await MercadoOportunidadesQuery.ConsultarAsync(
            cenario.Db, cenario.RedeId, corteMinimo: 0m, brick: null, areaFarmacia: null,
            skip: 0, take: 100, ct: TestContext.Current.CancellationToken);

        pagina.Itens.Select(i => i.Ean).Should().BeEquivalentTo(["222", "333"]);
    }

    [Fact]
    public async Task O_corte_de_relevancia_reduz_a_lista()
    {
        var cenario = await SemearMercadoEcatalogoAsync(
            noMercado: [("111", 500m), ("222", 300m), ("333", 50m)],
            noCatalogo: []);

        var comCorte = await MercadoOportunidadesQuery.ConsultarAsync(
            cenario.Db, cenario.RedeId, corteMinimo: 200m, brick: null, areaFarmacia: null,
            skip: 0, take: 100, ct: TestContext.Current.CancellationToken);

        comCorte.Itens.Select(i => i.Ean).Should().BeEquivalentTo(["111", "222"]);
        comCorte.Total.Should().Be(2, "o total acompanha o corte, não a população");
    }

    [Fact]
    public async Task So_conta_a_venda_dos_CONCORRENTES()
    {
        // Venda de bandeira própria não é oportunidade: se a rede vendeu, ela tem o item.
        // Somar as duas bandeiras faria a tela oferecer o próprio sortimento de volta.
        var cenario = await SemearMercadoEcatalogoAsync(
            noMercado: [("111", 100m)], noCatalogo: [], unidadesDaRede: 900m);

        var pagina = await MercadoOportunidadesQuery.ConsultarAsync(
            cenario.Db, cenario.RedeId, corteMinimo: 500m, brick: null, areaFarmacia: null,
            skip: 0, take: 100, ct: TestContext.Current.CancellationToken);

        pagina.Itens.Should().BeEmpty(
            "só 100 unidades são de concorrente, e o corte é 500");
    }

    [Fact]
    public async Task Usa_o_mes_mais_recente_coberto()
    {
        // Regra diferente do grupo B, e de propósito: aqui a pergunta é "o que devo
        // incluir agora", não "o que o comprador poderia ter sabido então". Sem sugestão
        // pontuada, não há vazamento possível.
        var cenario = await SemearDoisMesesAsync(
            antigo: new DateOnly(2025, 6, 1), recente: new DateOnly(2026, 6, 1));

        var pagina = await MercadoOportunidadesQuery.ConsultarAsync(
            cenario.Db, cenario.RedeId, corteMinimo: 0m, brick: null, areaFarmacia: null,
            skip: 0, take: 100, ct: TestContext.Current.CancellationToken);

        pagina.Mes.Should().Be(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public async Task Sem_catalogo_a_consulta_recusa_em_vez_de_devolver_tudo()
    {
        // Catálogo vazio significa "o comprador não enviou o arquivo", NÃO "a rede não tem
        // produto nenhum". Devolver o mercado inteiro como oportunidade seria a tela
        // mentindo em 100% das linhas.
        var cenario = await SemearMercadoEcatalogoAsync(
            noMercado: [("111", 500m)], noCatalogo: []);
        await LimparCatalogoAsync(cenario.RedeId);

        var pagina = await MercadoOportunidadesQuery.ConsultarAsync(
            cenario.Db, cenario.RedeId, corteMinimo: 0m, brick: null, areaFarmacia: null,
            skip: 0, take: 100, ct: TestContext.Current.CancellationToken);

        pagina.EansNoCatalogo.Should().Be(0);
        pagina.Itens.Should().BeEmpty("sem catálogo não há como afirmar ausência");
    }
```

- [ ] **Step 2: Rodar e confirmar que falham**

Expected: FAIL na compilação — `MercadoOportunidadesQuery` não existe.

- [ ] **Step 3: Escrever a consulta**

Estrutura obrigatória:

1. **Mês**: o mais recente presente em `MercadoObservacoes` da rede. Nulo → página vazia com `Mes` do dia corrente e contadores zerados.
2. **Catálogo**: `SELECT COUNT(*)` de `RedeCatalogoEans` da rede. **Zero → devolver página vazia** com `EansNoCatalogo = 0`; a tela usa isso para explicar em vez de mostrar lista.
3. **Mercado do mês**: `MercadoObservacoes` da rede, mês escolhido, `Bandeira = 'CONCORRENTES'`, agrupado por `(Brick, Ean)`, somando `Unidades` e `ValorCpp`.
4. **Anti-join** com `RedeCatalogoEans` por `Ean` — `LEFT JOIN ... WHERE catalogo.Ean IS NULL`, não `NOT IN` (que trata nulo de forma surpreendente).
5. **Corte** `Unidades >= corteMinimo`, filtros opcionais de brick e área.
6. **Dimensão**: `LEFT JOIN MercadoProdutos` por `(RedeId, Ean)` para descrição, laboratório, área e classe. Produto sem linha na dimensão entra com descrição nula — a tela mostra o EAN.
7. **`PdvsConcorrentes`**: `MercadoBrickPdvs` do brick com CNPJ do agregado. Hoje é sempre 1 (o contador real não está no banco — ver a régua do B2 no grupo B); **exponha o campo e deixe a tela declarar a limitação**, não finja que é 37.
8. Ordenar por `Unidades` desc; paginar por `skip`/`take` com `take` limitado a 200.

- [ ] **Step 4: Rodar e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.Tests --filter MercadoOportunidadesQueryTests`
Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter OportunidadesIntegrationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.ApiService/Mercado/ tests/
git commit -m "feat(mercado): query the assortment gap against the network catalog

Regras A1 e A2. Anti-join por LEFT JOIN ... IS NULL e nao NOT IN, que trata
nulo de forma surpreendente.

Duas recusas que nao sao estilo. Catalogo vazio devolve lista VAZIA, nao o
mercado inteiro: catalogo vazio significa 'o comprador nao enviou o arquivo',
nao 'a rede nao tem produto nenhum', e devolver tudo seria a tela mentindo em
100% das linhas. E so a venda de CONCORRENTES conta -- somar a bandeira propria
faria a tela oferecer o proprio sortimento de volta.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 6: O endpoint

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.ApiService/Mercado/MercadoEndpoints.cs`
- Modify: `CosmosPro.ML.DemandForCast.Web/MercadoApiClient.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/OportunidadesIntegrationTests.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/IMercadoApi.cs` (existente)

**Interfaces:**
- Consumes: `MercadoOportunidadesQuery` (Task 5).
- Produces: `GET /api/mercado/oportunidades?redeId&corteMinimo&brick&areaFarmacia&skip&take`.

- [ ] **Step 1: Escrever o teste**

```csharp
    [Fact]
    public async Task O_endpoint_devolve_a_pagina_e_respeita_o_inquilino()
    {
        var ct = TestContext.Current.CancellationToken;
        var (redeComDado, redeVizinha) = await SemearDuasRedesAsync();

        var minha = await fixture.MercadoApi.OportunidadesAsync(redeComDado, ct: ct);
        minha.IsSuccessStatusCode.Should().BeTrue();
        minha.Content!.Itens.Should().NotBeEmpty();

        // A rede vizinha não vê o mercado nem o catálogo da outra. Escopo por RedeId, e
        // o redeId vem do IRedeContext na Web -- aqui a apiservice é interna.
        var dela = await fixture.MercadoApi.OportunidadesAsync(redeVizinha, ct: ct);
        dela.Content!.Itens.Should().BeEmpty();
    }

    [Fact]
    public async Task Take_e_limitado_para_a_tela_nao_pedir_a_base_inteira()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearMuitasOportunidadesAsync(quantas: 300);

        var pagina = await fixture.MercadoApi.OportunidadesAsync(redeId, take: 5000, ct: ct);

        pagina.Content!.Itens.Count.Should().BeLessThanOrEqualTo(200);
        pagina.Content.Total.Should().Be(300, "o total fala da população, não da página");
    }
```

- [ ] **Steps 2–4: Implementar, rodar, commitar**

Seguir a forma dos handlers vizinhos em `MercadoEndpoints.cs`: `ValidateRedeAsync`, 404 para rede inexistente, `[FromQuery]` com **`decimal?`/`int?`, nunca `= default`** (a armadilha de `Guid` documentada em CLAUDE.md vale para o hábito).

---

## Task 7: A tela

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Oportunidades.razor`
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Pages/DadosMercado.razor`
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Layout/NavMenu.razor`
- Test: `tests/CosmosPro.ML.DemandForCast.Web.E2ETests/OportunidadesE2ETests.cs`

**Interfaces:**
- Consumes: `MercadoApiClient` (Task 6).
- Produces: nada que outra tarefa consuma.

- [ ] **Step 1: Escrever o E2E**

```csharp
    [Fact]
    public async Task A_tela_lista_oportunidades_e_declara_a_cobertura_do_catalogo()
    {
        await SemearAsync();
        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            await page.GotoAsync($"{fixture.WebfrontendUrl.TrimEnd('/')}/mercado/oportunidades");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            (await page.Locator("[data-test='linha-oportunidade']").CountAsync())
                .Should().BeGreaterThan(0);

            // O painel declara sobre o que a lista foi apurada: mês, tamanho do catálogo e
            // o corte. Sem isso o comprador não sabe se "12 oportunidades" é a rede
            // inteira ou um recorte.
            var painel = page.Locator("[data-test='painel-oportunidades']");
            (await painel.InnerTextAsync()).Should().Contain("no seu cadastro");
        }
        finally { await page.CloseAsync(); }
    }

    [Fact]
    public async Task Sem_catalogo_a_tela_explica_em_vez_de_listar()
    {
        // O estado em que a tela mais pode mentir: sem catálogo, TODO produto do mercado
        // parece ausente. A tela tem de dizer o que falta e como conseguir.
        await SemearSemCatalogoAsync();
        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            await page.GotoAsync($"{fixture.WebfrontendUrl.TrimEnd('/')}/mercado/oportunidades");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            (await page.Locator("[data-test='linha-oportunidade']").CountAsync()).Should().Be(0);

            var aviso = page.Locator("[data-test='aviso-sem-catalogo']");
            (await aviso.CountAsync()).Should().Be(1);
            (await aviso.InnerTextAsync()).Should().Contain("extrator");
        }
        finally { await page.CloseAsync(); }
    }
```

- [ ] **Step 2: Construir a tela**

Estrutura, reaproveitando o vocabulário que a tela de itens já tem (painel compacto com `Totalizador`, e não parágrafos de letra miúda — ver o commit `205a331`):

- **Painel de contexto** (`data-test="painel-oportunidades"`), com: mês da IQVIA usado, quantos EANs há no catálogo da rede, o corte aplicado, e quantas oportunidades sobraram. O porquê de cada um vai em `HelpTip`.
- **Corte de relevância** como controle, com o padrão de 200 e as opções da tabela do grupo B. O rótulo diz **"unidades no bairro"** e não "por loja": o contador de PDVs concorrentes não está no banco, então o divisor não existe.
- **Filtros** por brick e por área da farmácia.
- **Tabela** ordenada por unidades desc, com `data-test="linha-oportunidade"` por linha: descrição da IQVIA, laboratório, classe, brick, unidades e valor. Produto sem linha na dimensão mostra o EAN em vez de célula vazia.
- **A declaração do falso positivo** (mitigação **A** da Task 1), sempre visível: *"Esta lista compara por código de barras. Produto que você tem cadastrado sem código de barras aparece aqui como se não existisse no seu mix — N produtos do seu cadastro estão nessa situação."* O N vem da Task 1; se o portão exigir a mitigação **B**, entra também a coluna "possivelmente já cadastrado".
- **Sem catálogo**: `data-test="aviso-sem-catalogo"`, dizendo que falta o arquivo, que ele vem do extrator 0.18.0 ou mais novo, e que basta reenviar uma sessão com o build novo.

- [ ] **Steps 3–4: Rodar e commitar**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Web.E2ETests --filter OportunidadesE2ETests`

---

## Task 8: Fechar a documentação

- [ ] **Step 1: README** — marcar o grupo A como feito na F16 parte C, com os números medidos e as limitações. O README não pode mentir (CLAUDE.md §9).
- [ ] **Step 2: CLAUDE.md §4** — as invariantes novas: o catálogo no `engine` e não no Stage, substituição por rede inteira, catálogo vazio recusa em vez de devolver tudo, e só `CONCORRENTES` conta.
- [ ] **Step 3: Suíte inteira** — `dotnet test -m:1`, e conferir que a contagem subiu.
- [ ] **Step 4: Commit** dos dois documentos.

---

## Lacunas conhecidas, registradas para não voltarem como esquecimento

- **O corte é em unidades absolutas, não por loja.** O contador de PDVs concorrentes vive numa área de tabela dinâmica que o `IqviaXlsxParser` ignora. O corte absoluto favorece o brick com mais lojas concorrentes (526 tem 37, 527 tem 28). O caminho robusto é pedir uma coluna de contagem ao fornecedor do relatório, não raspar o pivot.
- **`FARMA ONE` tem 6 PDVs próprios no painel e nenhuma coluna de venda.** Se as vendas deles estiverem em `CONCORRENTES`, esta tela pode oferecer como oportunidade item que a própria rede vende sob outra bandeira. Pergunta aberta para quem puxa o relatório.
- **Volta Redonda tem mais bricks que os três do arquivo** — o filtro da consulta pediu quatro. Oportunidade em bairro fora dos cobertos não aparece, e a tela não tem como saber que existe.
- **A lista não estima demanda.** Ela diz "o bairro vende N unidades disto e você não tem"; quanto **você** venderia depende do seu fluxo, e o ML não pode prever série que não existe. Não transformar unidades do bairro em sugestão de compra sem uma decisão explícita sobre isso.
