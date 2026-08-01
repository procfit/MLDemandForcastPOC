# F14 — Sessões de Comparação: plano de implementação

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe `.md` de planejamento.
> Gravado a pedido explícito do usuário, seguindo o precedente desta pasta.

> **Para quem executar:** etapas com checkbox (`- [ ]`) para acompanhamento.
> Spec: [2026-07-28-sessoes-de-comparacao-design.md](../specs/2026-07-28-sessoes-de-comparacao-design.md)

**Objetivo:** o comprador entra no app, vê suas comparações anteriores, começa uma
nova, é conduzido a extrair uma sugestão do PBS, volta com o ZIP, e recebe a diferença
entre os métodos em linguagem de negócio.

**Arquitetura:** uma entidade `ComparacaoSessao` no banco `engine` funciona como
máquina de estados de sete fases; um `SessaoWorker` novo faz polling e cria o próximo
job quando o anterior conclui, deixando os três workers existentes intocados. A escolha
da sugestão acontece no Extractor (único com acesso ao PBS), que deriva as janelas de
dados da data da sugestão. O resultado é **materializado** em `engine`, nunca
recalculado do Stage.

**Stack:** .NET 10, Blazor Server + Radzen, EF Core migrations, SQL Server, MinIO,
WinForms (Extractor), xUnit v3 + FluentAssertions + Playwright.

## Pré-requisitos — leia antes de começar

Este plano **não é executável sozinho**. Duas dependências:

| Dependência | Estado | O que falta |
|---|---|---|
| [F12](2026-07-28-f12-captura-sugestao-pbs.md) Tasks 1–2, 5 | ✅ feito | — |
| [F12](2026-07-28-f12-captura-sugestao-pbs.md) Tasks 3–4 | **substituídas por este plano** | O spec mudou a UX do Extractor: uma sugestão, janelas derivadas. Absorvidas nas Tasks 1–4 aqui. |
| [F13](2026-07-28-f13-comparacao-ml-vs-pbs.md) | ❌ não iniciada | Entidade `ComparacaoPbs`, `ForecastVsErpComparer`, `DecisionComparer`, `ComparacaoProcessor`. **Execute F13 antes da Task 7 daqui.** |

Tasks 1–6 (Extractor + sessão + painel) não dependem da F13 e podem ser feitas antes.

## Global Constraints

- Toda entidade nova em `engine` leva `RedeId` com FK para `Redes` (padrão F10).
- Escopo de rede vem do `IRedeContext`, **nunca** de rota, query ou formulário (F11).
- `RedeId` não trafega em CSV — o Worker injeta da `CargaStage`.
- Páginas de dados levam `@attribute [Authorize]`; nada de admin sem `Roles = Papeis.PowerUser`.
- Acesso a banco em componente Blazor via escopo por operação, nunca `DbContext` injetado.
- Parâmetro de componente Razor inexistente falha em **runtime**, não no build — confira os parâmetros reais antes de usar um componente.
- Comentário só quando o **porquê** não é óbvio (CLAUDE.md §3).

---

## Task 1: Janela de extração derivada da sugestão

Cálculo puro, testável sem banco. É a peça que garante "prazos corretos".

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/ExtractionWindow.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtractionWindowTests.cs`

**Interfaces:**
- Produz: `ExtractionWindow.Derive(DateOnly dataSugestao, int diasCobertura, DateOnly hoje)` → `record ExtractionWindow(DateOnly Inicio, DateOnly Fim, bool Viavel, string? MotivoInviabilidade)`

- [ ] **Step 1: Escrever os testes**

```csharp
public sealed class ExtractionWindowTests
{
    private static readonly DateOnly Hoje = new(2026, 7, 28);

    [Fact]
    public void Janela_cobre_historico_de_treino_antes_e_a_cobertura_depois()
    {
        var j = ExtractionWindow.Derive(
            dataSugestao: new DateOnly(2026, 3, 10), diasCobertura: 30, hoje: Hoje);

        j.Viavel.Should().BeTrue();
        j.Inicio.Should().Be(new DateOnly(2025, 3, 10),
            "12 meses de historico antes de T para o modelo aprender sazonalidade");
        j.Fim.Should().Be(new DateOnly(2026, 4, 9),
            "T + dias de cobertura, para julgar quem acertou");
    }

    [Fact]
    public void Sugestao_recente_demais_e_inviavel_com_motivo()
    {
        var j = ExtractionWindow.Derive(
            dataSugestao: new DateOnly(2026, 7, 25), diasCobertura: 30, hoje: Hoje);

        j.Viavel.Should().BeFalse();
        j.MotivoInviabilidade.Should().Contain("ainda não aconteceram");
    }

    [Fact]
    public void Limite_e_exatamente_hoje_menos_cobertura()
    {
        // A cobertura precisa ter terminado. T + cobertura == hoje ja serve.
        var limite = Hoje.AddDays(-30);

        ExtractionWindow.Derive(limite, 30, Hoje).Viavel.Should().BeTrue();
        ExtractionWindow.Derive(limite.AddDays(1), 30, Hoje).Viavel.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

`dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests --filter ExtractionWindow`
Esperado: FALHA — `ExtractionWindow` não existe.

- [ ] **Step 3: Implementar**

```csharp
namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Janela de dados que o ZIP precisa cobrir para uma sugestão datada em T.
/// <para>
/// Antes de T: histórico para o modelo aprender. O pipeline de features exige no
/// mínimo 34 dias por SKU×loja, mas 34 dias não pegam sazonalidade — usamos 12 meses.
/// Depois de T: até T + dias de cobertura, que é o período que a compra deveria
/// suprir e portanto o único que revela quem acertou.
/// </para>
/// </summary>
internal sealed record ExtractionWindow(
    DateOnly Inicio, DateOnly Fim, bool Viavel, string? MotivoInviabilidade)
{
    private const int MesesHistorico = 12;

    public static ExtractionWindow Derive(DateOnly dataSugestao, int diasCobertura, DateOnly hoje)
    {
        var fim = dataSugestao.AddDays(diasCobertura);
        var inicio = dataSugestao.AddMonths(-MesesHistorico);

        // A cobertura tem de ter terminado: sem as vendas do periodo nao ha como
        // dizer quem acertou.
        if (fim > hoje)
        {
            var limite = hoje.AddDays(-diasCobertura);
            return new ExtractionWindow(inicio, fim, false,
                $"Esta sugestão é de {dataSugestao:dd/MM/yyyy} e cobre {diasCobertura} dias. " +
                $"As vendas que provariam quem acertou ainda não aconteceram. " +
                $"Escolha uma sugestão de até {limite:dd/MM/yyyy}.");
        }

        return new ExtractionWindow(inicio, fim, true, null);
    }
}
```

- [ ] **Step 4: Verde e commit**

```bash
dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests --filter ExtractionWindow
git add CosmosPro.ML.DemandForCast.Extractor/ExtractionWindow.cs tests/
git commit -m "feat(extractor): janela de extracao derivada da data da sugestao"
```

---

## Task 2: Query do catálogo de sugestões

O Extractor precisa listar as sugestões do PBS para o usuário escolher.

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/Queries/catalogo_sugestoes.sql`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionModels.cs`

**Interfaces:**
- Produz: `record SugestaoCatalogo(long SugestaoId, string? Descricao, DateTime DataHora, byte TipoCalculo, int DiasCoberturaMax, int QtdLinhas, int QtdLojas)`

- [ ] **Step 1: A query**

```sql
-- Catalogo para o usuario escolher a sugestao. Traz contagem de linhas e lojas para
-- ele ver o tamanho antes de extrair, e DiasCoberturaMax (o maior DIAS_CURVA_*) para
-- derivar a janela.
SELECT
    SugestaoId        = CONVERT(bigint, S.SUGESTAO_COMPRA),
    Descricao         = LEFT(S.DESCRICAO, 100),
    DataHora          = CONVERT(datetime2(0), S.DATA_HORA),
    TipoCalculo       = CONVERT(tinyint, S.TIPO_CALCULO),
    DiasCoberturaMax  = CONVERT(int, (SELECT MAX(v) FROM (VALUES
                            (S.DIAS_CURVA_A), (S.DIAS_CURVA_B), (S.DIAS_CURVA_C),
                            (S.DIAS_CURVA_D), (S.DIAS_CURVA_E)) AS t(v))),
    QtdLinhas         = COUNT(R.SUGESTAO_COMPRA_RESULTADO),
    QtdLojas          = COUNT(DISTINCT R.FILIAL)
FROM dbo.SUGESTOES_COMPRAS S
JOIN dbo.SUGESTOES_COMPRAS_RESULTADO R ON R.SUGESTAO_COMPRA = S.SUGESTAO_COMPRA
WHERE S.TIPO_CALCULO IS NOT NULL
  AND S.DATA_HORA >= {{DATA_INICIO}}
GROUP BY S.SUGESTAO_COMPRA, S.DESCRICAO, S.DATA_HORA, S.TIPO_CALCULO,
         S.DIAS_CURVA_A, S.DIAS_CURVA_B, S.DIAS_CURVA_C, S.DIAS_CURVA_D, S.DIAS_CURVA_E
ORDER BY S.DATA_HORA DESC;
```

- [ ] **Step 2: Modelo** — adicionar o record `SugestaoCatalogo` em `ExtractionModels.cs`.

- [ ] **Step 3: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/
git commit -m "feat(extractor): catalogo de sugestoes do PBS"
```

---

## Task 3: Queries das sugestões, escopadas a uma sugestão

Substitui a Task 3 da F12: filtro por `SUGESTAO_COMPRA`, não por janela de datas.

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/Queries/sugestoes_compra.sql`
- Create: `CosmosPro.ML.DemandForCast.Extractor/Queries/sugestoes_compra_itens.sql`

- [ ] **Step 1: cabeçalho** — `WHERE S.SUGESTAO_COMPRA = {{SUGESTAO}}`, colunas na ordem
      de `StageContract.Headers[SugestoesCompra]`.

- [ ] **Step 2: itens** — `WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}}`, `LojaId = R.FILIAL`,
      `Sku = CONVERT(varchar(30), R.PRODUTO)`, colunas na ordem de
      `StageContract.Headers[SugestoesCompraItens]`.

- [ ] **Step 3: aviso EMPRESA vs FILIAL** — query que conta
      `WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}} AND R.EMPRESA <> R.FILIAL`. Se > 0, o
      extrator avisa em vez de seguir em silêncio. Item aberto do plano da F12:
      coincidem na NatusFarma, não validado na Retiro.

- [ ] **Step 4: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/Queries/
git commit -m "feat(extractor): queries da sugestao escolhida"
```

---

## Task 4: Extractor — escolha da sugestão e manifesto no ZIP

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/MainForm.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs`
- Create: `CosmosPro.ML.DemandForCast.Extractor/ZipManifest.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ZipManifestTests.cs`

**Interfaces:**
- Consome: `ExtractionWindow.Derive(...)` (Task 1), `SugestaoCatalogo` (Task 2)
- Produz: `manifesto.json` na raiz do ZIP —
  `record ZipManifest(long SugestaoId, string? SugestaoDescricao, DateTime SugestaoDataHora, byte SugestaoTipoCalculo, DateOnly JanelaInicio, DateOnly JanelaFim, string VersaoExtractor)`

- [ ] **Step 1: Teste do manifesto** — serializa e desserializa preservando os campos;
      `SugestaoId` obrigatório.

```csharp
[Fact]
public void Manifesto_roundtrip_preserva_a_sugestao_e_a_janela()
{
    var m = new ZipManifest(21217, "MATTEL", new DateTime(2026, 3, 10, 10, 27, 0), 2,
                            new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "1.0.0");

    var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

    volta.Should().BeEquivalentTo(m);
}
```

- [ ] **Step 2: Implementar `ZipManifest`** com `Escrever`/`Ler` em JSON (cultura invariante).

- [ ] **Step 3: `MainForm`** — grid com o catálogo (descrição, data, método, linhas,
      lojas). Ao selecionar, mostra a janela derivada e, se `Viavel == false`, exibe
      `MotivoInviabilidade` e desabilita o botão de extrair.

- [ ] **Step 4: `ExtractionService`** — extrai as tabelas na janela derivada, filtra
      `Vendas`/`EstoquesDiarios`/`Compras`/`Promocoes` por `[Inicio, Fim]`, e **garante a
      união dos produtos**: coleta os `Sku` distintos das linhas da sugestão e assegura
      que `produtos.csv` os inclua. Sem isso o `SqlBulkCopy` estoura FK (ver comentário
      em `SugestoesCompraItens.sql`).

- [ ] **Step 5: Verde e commit**

```bash
dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests
git add CosmosPro.ML.DemandForCast.Extractor/ tests/
git commit -m "feat(extractor): escolha da sugestao e manifesto no ZIP"
```

---

## Task 5: Entidades da sessão e migration

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Engine/Entities/ComparacaoSessao.cs`
- Create: `CosmosPro.ML.DemandForCast.Engine/Entities/ComparacaoSessaoItem.cs`
- Modify: `CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Engine.Tests/SessaoEstadoTests.cs`

**Interfaces:**
- Produz: `ComparacaoSessao` com `SessaoStatus { AguardandoDados, ProcessandoDados, Treinando, Comparando, Concluida, Inviavel, Falha }`; `ComparacaoSessaoItem`

- [ ] **Step 1: Teste da transição válida**

```csharp
public sealed class SessaoEstadoTests
{
    [Theory]
    [InlineData(SessaoStatus.AguardandoDados, SessaoStatus.ProcessandoDados, true)]
    [InlineData(SessaoStatus.ProcessandoDados, SessaoStatus.Treinando, true)]
    [InlineData(SessaoStatus.Treinando, SessaoStatus.Comparando, true)]
    [InlineData(SessaoStatus.Comparando, SessaoStatus.Concluida, true)]
    [InlineData(SessaoStatus.AguardandoDados, SessaoStatus.Concluida, false)]
    [InlineData(SessaoStatus.Concluida, SessaoStatus.Treinando, false)]
    public void Transicoes_permitidas(SessaoStatus de, SessaoStatus para, bool permitida)
        => ComparacaoSessao.PodeTransicionar(de, para).Should().Be(permitida);

    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados)]
    [InlineData(SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Comparando)]
    public void Qualquer_fase_em_andamento_pode_falhar_ou_ficar_inviavel(SessaoStatus de)
    {
        ComparacaoSessao.PodeTransicionar(de, SessaoStatus.Falha).Should().BeTrue();
        ComparacaoSessao.PodeTransicionar(de, SessaoStatus.Inviavel).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Entidades**

```csharp
namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Uma comparação entre a sugestão de compra do ERP e a que o ML faria, ancorada a
/// UMA sugestão do PBS.
/// <para>
/// Nasce sem <see cref="SugestaoId"/>: a sugestão é escolhida no Extractor, que é o
/// único com acesso ao PBS, e o ZIP declara qual foi. É assim que o ovo-e-galinha se
/// resolve — a web não pode pedir a sugestão antes de ter os dados.
/// </para>
/// </summary>
public sealed class ComparacaoSessao
{
    public Guid Id { get; set; }
    public int RedeId { get; set; }
    public string? Nome { get; set; }

    public SessaoStatus Status { get; set; }
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }

    public long? SugestaoId { get; set; }
    /// <summary>Retrato da sugestão, para o painel ser legível sem consultar o Stage.</summary>
    public string? SugestaoDescricao { get; set; }
    public DateTime? SugestaoDataHora { get; set; }
    public byte? SugestaoTipoCalculo { get; set; }

    public Guid? CargaStageId { get; set; }
    public Guid? TreinoJobId { get; set; }
    public Guid? ComparacaoPbsId { get; set; }

    /// <summary>Agregados da manchete. O detalhe por item vive em ComparacaoSessaoItens.</summary>
    public string? ResultadoJson { get; set; }

    public string? MotivoInviabilidade { get; set; }
    public string? MensagemErro { get; set; }

    private static readonly Dictionary<SessaoStatus, SessaoStatus[]> Permitidas = new()
    {
        [SessaoStatus.AguardandoDados] = [SessaoStatus.ProcessandoDados, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.ProcessandoDados] = [SessaoStatus.Treinando, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Treinando] = [SessaoStatus.Comparando, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Comparando] = [SessaoStatus.Concluida, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Concluida] = [],
        [SessaoStatus.Inviavel] = [SessaoStatus.AguardandoDados],  // reenviar outro ZIP
        [SessaoStatus.Falha] = [SessaoStatus.AguardandoDados],
    };

    public static bool PodeTransicionar(SessaoStatus de, SessaoStatus para) =>
        Permitidas.TryGetValue(de, out var destinos) && destinos.Contains(para);
}

public enum SessaoStatus
{
    AguardandoDados = 0,
    ProcessandoDados = 1,
    Treinando = 2,
    Comparando = 3,
    Concluida = 4,
    Inviavel = 5,
    Falha = 6,
}
```

```csharp
namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Detalhe por item da comparação. Tabela, e não JSON, porque o import faz
/// DELETE ... WHERE RedeId — o Stage da rede é apagado a cada ZIP novo, então o
/// resultado tem de ser materializado para uma sessão antiga continuar legível. E
/// porque esta é a tabela que o comprador ordena e pagina para conferir contra a
/// memória dele, o que exige paginação server-side.
/// </summary>
public sealed class ComparacaoSessaoItem
{
    public Guid SessaoId { get; set; }
    public int LojaId { get; set; }
    public required string Sku { get; set; }
    public string? NomeProduto { get; set; }
    public string? Curva { get; set; }

    public decimal CompraSugeridaPbs { get; set; }
    public decimal CompraSugeridaMl { get; set; }
    public decimal VendidoNaJanela { get; set; }

    public decimal DemandaDiaPbs { get; set; }
    public decimal DemandaDiaMl { get; set; }
    public decimal DemandaDiaReal { get; set; }

    public decimal SobraPbsUnidades { get; set; }
    public decimal SobraMlUnidades { get; set; }
    public decimal SobraPbsValor { get; set; }
    public decimal SobraMlValor { get; set; }
}
```

- [ ] **Step 4: Mapeamento em `EngineDbContext`** — `ComparacaoSessao` com FK para `Rede`
      (`Restrict`), `Status` como string de 20, índices `(RedeId, CriadoEm)` para o painel e
      `(Status, AtualizadoEm)` para o polling **cross-rede** do worker (RedeId não entra
      neste, mesma razão da F10). `ComparacaoSessaoItem` com PK `(SessaoId, LojaId, Sku)` e
      FK cascade para a sessão.

- [ ] **Step 5: Migration**

```bash
ConnectionStrings__engine="Server=localhost;Database=engine;User Id=sa;Password=x;TrustServerCertificate=true" \
  dotnet ef migrations add AddComparacaoSessoes \
  --project CosmosPro.ML.DemandForCast.Engine \
  --startup-project CosmosPro.ML.DemandForCast.ApiService
```

Não criar `IDesignTimeDbContextFactory` no projeto Engine — o tool do EF o prefere ao
service provider e aplica no banco errado.

- [ ] **Step 6: Verde e commit**

---

## Task 6: Painel e página da sessão (estados sem resultado)

Entrega navegável antes da F13 existir: criar sessão, ver `AguardandoDados`, subir ZIP,
acompanhar as fases.

**Files:**
- Create: `CosmosPro.ML.DemandForCast.ApiService/Comparacoes/ComparacoesEndpoints.cs`
- Create: `CosmosPro.ML.DemandForCast.Web/ComparacoesApiClient.cs`
- Create: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Comparacoes.razor` (`/`)
- Create: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Sessao.razor` (`/comparacoes/{Id:guid}`)
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Imports.razor` → `@page "/tecnico/importar"`
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Layout/NavMenu.razor`
- Modify: `CosmosPro.ML.DemandForCast.Web/Program.cs`
- Modify: `CosmosPro.ML.DemandForCast.ApiService/Program.cs`

**Interfaces:**
- Produz: `POST /api/comparacoes` (body `{ nome }`, query `redeId`) → `201 SessaoView`;
  `GET /api/comparacoes?redeId&take` → `SessaoView[]`;
  `GET /api/comparacoes/{id}?redeId` → `SessaoView`;
  `POST /api/comparacoes/{id}/dados?redeId` (multipart) → `202`
- Produz: `record SessaoView(Guid Id, string? Nome, string Status, DateTimeOffset CriadoEm, long? SugestaoId, string? SugestaoDescricao, DateTime? SugestaoDataHora, byte? SugestaoTipoCalculo, string? MotivoInviabilidade, string? MensagemErro)`

- [ ] **Step 1:** endpoints com a guarda `RedesEndpoints.ValidateRedeAsync` (F11), e toda
      leitura filtrada por `RedeId`.
- [ ] **Step 2:** `ComparacoesApiClient` obtendo `redeId` do `IRedeContext`, como os
      quatro clients existentes.
- [ ] **Step 3:** `Comparacoes.razor` em `/` com `@attribute [Authorize]`: grid de sessões
      (nome, sugestão, método, estado, resumo), botão "Nova comparação", estado vazio
      explicativo.
- [ ] **Step 4:** `Sessao.razor` com `@attribute [Authorize]`, renderizando por estado.
      Em `AguardandoDados`: instruções numeradas + upload. Estados intermediários com
      polling de 3s. `Inviavel`/`Falha` mostram o texto e a próxima ação.
- [ ] **Step 5:** mover a home antiga para `/tecnico/importar`; menu com três blocos
      (Comparações, Administração, Técnico recolhido com `/tecnico/importar`, `/dados`,
      `/treinamento`, `/sugestao-compra`).

- [ ] **Step 5b: Ajustar o E2E existente que a mudança de home quebra.**
      `tests/CosmosPro.ML.DemandForCast.Web.E2ETests/ImportsE2ETests.cs` navega para `/`
      e procura `input[type=file]#hidden-zip-input`. Depois desta task `/` é o painel de
      comparações, então o cenário passa a apontar para `/tecnico/importar`:

```csharp
// era: fixture.WebfrontendUrl.TrimEnd('/') + "/"
await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/tecnico/importar");
```

      Rodar `dotnet test tests/CosmosPro.ML.DemandForCast.Web.E2ETests` e confirmar que
      os 5 cenários seguem verdes antes de continuar.
- [ ] **Step 6: E2E**

```csharp
[Fact]
public async Task Cria_sessao_e_ve_instrucoes_de_extracao()
{
    var page = await fixture.NovaPaginaLogadaAsync();
    await page.GotoAsync(fixture.WebfrontendUrl.TrimEnd('/') + "/");

    await page.GetByText("Nova comparação").First.ClickAsync();

    var corpo = await page.TextContentAsync("body") ?? "";
    corpo.Should().Contain("extrator",
        $"a sessao nova deve instruir a extracao. Conteudo real: <<<{corpo.Trim()}>>>");
}
```

Assertiva sobre o texto, não `WaitForAsync` em seletor: quando falha, a mensagem mostra
o que a página renderizou em vez de um timeout opaco.

- [ ] **Step 7: Verde e commit**

---

## Task 7: `SessaoWorker` — orquestração das fases

**Depende da F13** (precisa criar `ComparacaoPbs`).

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Worker/Comparacoes/SessaoWorker.cs`
- Create: `CosmosPro.ML.DemandForCast.Worker/Comparacoes/SessaoAvancador.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Worker.Tests/SessaoAvancadorTests.cs`

**Interfaces:**
- Consome: `ComparacaoSessao`, `SessaoStatus` (Task 5); `TreinoJob`, `ComparacaoPbs`
- Produz: `SessaoAvancador.ProximoEstado(SessaoStatus atual, JobResultado resultado)` → `SessaoStatus`; `enum JobResultado { EmAndamento, Concluido, Falhou }`

- [ ] **Step 1: Teste da decisão de avanço** — lógica pura, sem banco.

```csharp
[Theory]
[InlineData(SessaoStatus.ProcessandoDados, JobResultado.Concluido, SessaoStatus.Treinando)]
[InlineData(SessaoStatus.Treinando, JobResultado.Concluido, SessaoStatus.Comparando)]
[InlineData(SessaoStatus.Comparando, JobResultado.Concluido, SessaoStatus.Concluida)]
[InlineData(SessaoStatus.Treinando, JobResultado.Falhou, SessaoStatus.Falha)]
[InlineData(SessaoStatus.Treinando, JobResultado.EmAndamento, SessaoStatus.Treinando)]
public void Proximo_estado(SessaoStatus atual, JobResultado r, SessaoStatus esperado)
    => SessaoAvancador.ProximoEstado(atual, r).Should().Be(esperado);
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar `SessaoAvancador`** — `switch` puro sobre `(atual, resultado)`.
- [ ] **Step 4: `SessaoWorker`** — `BackgroundService` com polling de 5s, mesmo padrão
      `WITH (UPDLOCK, READPAST)` dos três workers existentes. Para cada sessão em estado
      intermediário: consulta o job da fase, decide via `SessaoAvancador`, e ao avançar
      **cria o job da fase seguinte** (`TreinoJob` após import, `ComparacaoPbs` após
      treino), gravando o id na sessão.
- [ ] **Step 5:** registrar em `Worker/Program.cs`.
- [ ] **Step 6: Verde e commit**

---

## Task 8: Vínculo do ZIP à sessão e detecção de inviabilidade

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Worker/CargaProcessor.cs`
- Create: `CosmosPro.ML.DemandForCast.Worker/Comparacoes/ManifestoLeitor.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Worker.Tests/ManifestoLeitorTests.cs`

- [ ] **Step 1: Teste** — ZIP sem `manifesto.json` numa carga de sessão → inviável com
      motivo "não contém sugestão do PBS"; ZIP com manifesto → devolve `SugestaoId`.
- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3:** `ManifestoLeitor` lê `manifesto.json` do diretório extraído.
- [ ] **Step 4:** `CargaProcessor` grava na sessão o `SugestaoId` e o retrato quando a
      carga pertence a uma sessão; sem manifesto, marca `Inviavel`.
- [ ] **Step 5: Verde e commit**

---

## Task 9: Cálculo da manchete

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Purchasing/Comparison/SobraCalculator.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Purchasing.Tests/SobraCalculatorTests.cs`

**Interfaces:**
- Produz: `SobraCalculator.Calcular(decimal comprado, decimal estoqueInicial, decimal vendido, decimal precoCompra)` → `record Sobra(decimal Unidades, decimal Valor)`

- [ ] **Step 1: Testes com entradas conhecidas**

```csharp
[Fact]
public void Sobra_e_o_que_comprou_mais_estoque_menos_o_que_vendeu()
{
    var s = SobraCalculator.Calcular(comprado: 100, estoqueInicial: 20, vendido: 80, precoCompra: 3.50m);

    s.Unidades.Should().Be(40);
    s.Valor.Should().Be(140m);
}

[Fact]
public void Nao_existe_sobra_negativa()
{
    var s = SobraCalculator.Calcular(comprado: 10, estoqueInicial: 0, vendido: 50, precoCompra: 2m);

    s.Unidades.Should().Be(0, "vender mais que o disponivel nao gera sobra negativa — " +
                              "gera ruptura, medida separadamente");
    s.Valor.Should().Be(0);
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar** — `Math.Max(0, comprado + estoqueInicial - vendido)`, valor
      = unidades × preço.
- [ ] **Step 4: Verde e commit**

---

## Task 10: Tela de resultado

**Depende das Tasks 7 e 9 e da F13.**

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Sessao.razor`
- Create: `CosmosPro.ML.DemandForCast.Web/Components/Shared/ManchetesComparacao.razor`
- Create: `CosmosPro.ML.DemandForCast.Web/Components/Shared/TabelaItensComparacao.razor`
- Modify: `CosmosPro.ML.DemandForCast.ApiService/Comparacoes/ComparacoesEndpoints.cs`

- [ ] **Step 1:** endpoint paginado `GET /api/comparacoes/{id}/itens?redeId&skip&take&orderBy&desc`
      lendo de `ComparacaoSessaoItens`, com whitelist de colunas de ordenação (mesma
      defesa contra injection do `StageBrowser`).
- [ ] **Step 2:** `ManchetesComparacao` — duas colunas ("Pelo PBS", "Como teria sido pelo
      ML") com capital além do vendido em R$ e dias/itens em ruptura, e a frase de
      veredito acima. **Venda perdida em R$ não aparece aqui** (§5 do spec).
- [ ] **Step 3:** `TabelaItensComparacao` — grid paginado e ordenável server-side, com
      badge de quem ficou mais perto.
- [ ] **Step 4:** bloco fixo "onde o ML foi pior", não em aba.
- [ ] **Step 5:** aba técnica com previsão contra previsão (MAE/WAPE), reaproveitando o
      drill-down da F7, e a venda perdida estimada **rotulada como estimativa com a
      premissa ao lado**.
- [ ] **Step 6: E2E** — sessão semeada em `Concluida` com itens; abre e confere que a
      manchete e a tabela aparecem, assertivas sobre texto do corpo.
- [ ] **Step 7: Verde e commit**

---

## Task 11: Download do extrator via MinIO

**Files:**
- Create: `CosmosPro.ML.DemandForCast.ApiService/Extrator/ExtratorEndpoints.cs`
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Pages/Sessao.razor`

- [ ] **Step 1:** `GET /api/extrator/download` faz stream do bucket `extrator` no MinIO;
      `GET /api/extrator/versao` devolve versão e checksum. 404 claro quando não publicado.
- [ ] **Step 2:** botão na sessão em `AguardandoDados`, com versão e checksum ao lado.
- [ ] **Step 3:** documentar em `README.md` como publicar o `.exe` no MinIO — é passo
      operacional a cada release.
- [ ] **Step 4: Commit**

---

## Task 12: Documentação

- [ ] **Step 1:** `README.md` §6 com F14 e a reorganização da navegação.
- [ ] **Step 2:** `CLAUDE.md` §4 com as duas tabelas novas e a regra de que o resultado
      da sessão é materializado por causa do `DELETE ... WHERE RedeId`.
- [ ] **Step 3:** marcar no plano da F12 que as Tasks 3–4 foram substituídas por este.
- [ ] **Step 4: `dotnet test` verde nos 13 projetos. Commit.**

---

## Ordem de execução

Tasks 1–6 (Extractor + sessão + painel) → **F13 inteira** → Tasks 7–12.

As Tasks 1–6 entregam software navegável sem a comparação existir: o usuário cria
sessão, extrai, sobe o ZIP e acompanha as fases. O resultado aparece quando a F13 e as
Tasks 7, 9 e 10 estiverem prontas.

## Fora de escopo

Comparação de demonstração com dado sintético (o gerador não produz sugestão do PBS);
IQVIA no mesmo upload; escrever de volta no PBS. Justificativas em §10 do spec.
