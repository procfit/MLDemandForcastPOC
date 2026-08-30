# F16 parte C, grupo B — Alertas de mercado na tabela de decisão

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** O comprador passa a ver, ao lado da decisão PBS × ML de cada item, se a rede está vendendo abaixo do mercado naquele bairro e qual é a suspeita (ruptura, sem causa, ou não apurado).

**Architecture:** O sinal de mercado é calculado no Worker, no fim da fase de comparação, e **materializado** em colunas novas de `ComparacaoSessaoItens` — pelo mesmo motivo que o resto do resultado já é materializado: o Stage da rede é apagado a cada import, então um cálculo feito na abertura da tela devolveria o dado do envio seguinte. Duas funções puras (`MercadoMesResolver`, `MercadoAlertaCalculador`) carregam a regra; um loader (`MercadoSinalLoader`) faz as duas pontes com o banco (CNPJ→brick, SKU→EAN). A API e a tela só exibem e filtram.

**Tech Stack:** .NET 10, EF Core (banco `engine`), `Microsoft.Data.SqlClient` (banco `Stage`), Blazor SSR, xUnit + FluentAssertions, Playwright (E2E).

**Spec:** [2026-08-30-iqvia-alertas-ao-comprador-design.md](../specs/2026-08-30-iqvia-alertas-ao-comprador-design.md)

**Branch:** `feat/f16c-alertas-de-mercado` (já criada, baseada em `origin/main`).

**Escopo deste plano:** apenas o **grupo B** (regras B1, B2, B3, B6). O grupo A (tela de
oportunidades, regras A1–A3) tem plano próprio e espera o `catalogo_eans.csv`.

**Correção de 2026-08-30, vinda da Task 1:** o grupo B **também** depende de um extrator
novo. Os ZIPs de hoje não trazem `Cnpj` em `lojas.csv`, e sem ele nenhuma loja se prende
a brick nenhum. O código deste plano é escrito e testado sem esperar nada; a tela só
mostra número depois de um build novo do extrator e de uma sessão nova.

## Global Constraints

- **Nulo nunca é zero.** Toda coluna nova é anulável e nulo significa "não foi possível calcular". Zero é medição legítima da IQVIA. Vale no montador, no `DataTable` do bulk, no DTO e no Razor. Contrato já existente na classe `ComparacaoSessaoItem` — ler o XML doc dela antes de codar.
- **Precisão de decimal é declarada sempre.** O default do EF Core é `decimal(18,2)` e trunca em silêncio.
- **Nenhuma fase nova na máquina de estados** e nenhuma fila nova. O cálculo entra no caminho que `SessaoResultadoMaterializador` já percorre.
- **`RedeId` nunca vem de rota, query ou arquivo** — sai do `IRedeContext`. Endpoint responde **404, não 403**, para sessão de outra rede.
- **Uma cláusula só de filtro.** Página, contagem, totalizadores e Excel passam por `ComparacoesEndpoints.AplicarFiltros`. Filtro novo entra lá, não em cópia.
- **Idioma:** identificadores em inglês ou no padrão já usado no arquivo; comentários e XML docs em pt-BR quando carregam contexto de negócio. Comentário só onde o *porquê* não é óbvio.
- **Limiar de alerta:** `0,5` — a leitura literal de "mais de 50% abaixo" do documento de controle. Constante nomeada, nunca literal espalhado.
- **Vocabulário de alerta (exato, sem sinônimos):** `SemAlerta`, `Ruptura`, `SemCausa`, `NaoApurado`, e `null` para não avaliado.
- **Commits:** Conventional Commits em inglês no *subject*, corpo em pt-BR quando explicar negócio. Terminar com `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- **Rodar os testes:** `dotnet test` na raiz. Os testes de integração sobem o AppHost real via `Aspire.Hosting.Testing` e precisam do Docker de pé.

---

## File Structure

**Criar:**

| arquivo | responsabilidade |
|---|---|
| `CosmosPro.ML.DemandForCast.Engine/Mercado/MercadoAlertas.cs` | as quatro constantes de texto do alerta; compartilhado entre Worker e ApiService |
| `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoMesResolver.cs` | função pura: escolhe o mês da IQVIA a usar |
| `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoAlertaCalculador.cs` | função pura: índice de desempenho e classificação do alerta |
| `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoSinalLoader.cs` | as duas pontes com o banco; devolve o sinal por (loja, sku) |
| `tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoMesResolverTests.cs` | testes da escolha do mês |
| `tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoAlertaCalculadorTests.cs` | testes do índice e da classificação |
| `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/MercadoSinalIntegrationTests.cs` | testes do loader e da materialização contra banco real |

**Modificar:**

| arquivo | mudança |
|---|---|
| `CosmosPro.ML.DemandForCast.Engine/Entities/ComparacaoSessaoItem.cs` | 7 propriedades novas |
| `CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs` | precisão e tamanho das 7 |
| `CosmosPro.ML.DemandForCast.Worker/Sessoes/SessaoResultadoMontador.cs` | `Montar` recebe o sinal e preenche as colunas |
| `CosmosPro.ML.DemandForCast.Worker/Sessoes/SessaoResultadoMaterializador.cs:75` | carrega o sinal e o passa ao montador; `DataTable` do bulk ganha as colunas |
| `CosmosPro.ML.DemandForCast.ApiService/Comparacoes/ComparacoesEndpoints.cs:632` | DTO do item + filtro `somenteComAlerta` em `AplicarFiltros` |
| `CosmosPro.ML.DemandForCast.Web/ComparacoesApiClient.cs:314` | espelho do DTO |
| `CosmosPro.ML.DemandForCast.Web/Components/Shared/TabelaItensComparacao.razor` | colunas e checkbox de filtro |
| `CosmosPro.ML.DemandForCast.Web/ComparacaoItensExcelExporter.cs` | colunas novas e o mês na aba de capa |

---

## Task 1: Portão — medir o casamento de EAN ✅ FEITO em 2026-08-30

Medido fora do banco, a partir do ZIP `extracao-pbs_20260818-1745.zip` (rede Retiro,
sugestão 125527 "EXELTIS - IQVIA", extrator 0.17.0) cruzado com
`IQVIA_MES_junho2026.XLSX`. **Não foi preciso consultar banco nenhum** — o ZIP traz
`produtos.csv` com `Sku` e `Ean`, e o XLSX traz os EANs da IQVIA.

### Resultado 1 — a regra de normalização, resolvida

| regra | cadastro que casa |
|---|---|
| comparação exata, string como veio | **0,0%** |
| sem zeros à esquerda nos dois lados | 60,2% |
| ambos preenchidos até 14 dígitos | 60,2% |

**A comparação exata casa zero.** O PBS grava o EAN com **14 caracteres e zero à
esquerda** (`07896094928060`); a IQVIA grava 13 (`7891721201806`). Sem normalizar, o
`MercadoSinalLoader` produziria dicionário vazio em toda sessão, sem erro nenhum e sem
nada na tela denunciando.

**Regra adotada no loader (Task 5): tirar zeros à esquerda dos dois lados antes de
comparar.** Equivale a preencher ambos até 14. Não usar comparação exata, e não
preencher até 13 (o valor de 14 do PBS não é truncado por `rjust(13)`, e o resultado é
0% de casamento).

### Resultado 2 — a taxa, na rede certa

| medida | valor |
|---|---|
| itens (loja × sku) na sugestão | 422 |
| lojas na sugestão | 10 |
| SKUs distintos | 43 |
| sem EAN no cadastro | 0 (0,0%) |
| com EAN | 43 (100,0%) |
| **com EAN que a IQVIA reportou** | **21 (48,8%)** |

**48,8%**, na banda intermediária da regra de decisão (30%–60%). **Seguir, e a tela tem
de declarar quantos itens ficaram sem dado de mercado.**

**O que os 22 sem casamento realmente são.** A lista de EANs do relatório da IQVIA não é
um catálogo — é *o que teve movimento nos bricks pedidos*. EAN ausente do arquivo, num
brick e mês cobertos, significa **o mercado daqueles bairros não vendeu esse item**, e
não "falha de join". Para o comprador isso é informação, não erro. O
`MercadoAlertaCalculador` já trata o caso corretamente: mercado zero e rede zero devolve
nulo, e a coluna mostra travessão.

A amostra é pequena e de um fornecedor só (Exeltis, 43 SKUs). Refazer a medição num
ZIP de sugestão ampla quando houver, mas não bloqueia: a regra de normalização, que era
o risco real, está resolvida.

### Resultado 3 — a outra ponte não existe nos ZIPs de hoje 🔴

`lojas.csv` do ZIP de 18/08 **não tem a coluna `Cnpj`**. O extrator que o comprador tem
é anterior à F16, então **toda loja entraria com CNPJ nulo e nenhum item receberia dado
de mercado**, mesmo com o EAN casando.

`origin/main` já emite `Cnpj` (`Queries/lojas.sql` lê `ENTIDADES.CGC`, e `StageContract`
declara a coluna), mas **o número de versão continua `0.17.0`** — o mesmo do build que o
comprador usa. Dois executáveis com a mesma versão e comportamento diferente.

**Duas consequências para o cronograma:**

1. **O grupo B não é "desbloqueado" como este plano afirmava.** O código pode ser escrito
   e testado inteiro, mas só produz dado depois de um extrator novo chegar ao comprador e
   uma sessão nova ser enviada. Nenhum ZIP existente serve.
2. **Subir a versão do extrator é pré-requisito**, não capricho: sem isso, ninguém
   distingue o build que traz CNPJ do que não traz, e a sessão falha em silêncio.

- [x] **Step 1: Escrever a consulta** — dispensada. A medição saiu do ZIP, sem banco.
- [x] **Step 2: Rodar** — feito em 2026-08-30.
- [x] **Step 3: Aplicar a regra de decisão** — 48,8%, banda intermediária: seguir com a
      declaração na tela.
- [ ] **Step 4: Subir a versão do extrator e publicar um build novo** (novo, veio da
      medição). Bump em `CosmosPro.ML.DemandForCast.Extractor.csproj` para `0.18.0` e
      entrega ao comprador, para os ZIPs passarem a trazer `Cnpj`.

---

## Task 2: Vocabulário do alerta e cálculo do índice

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Engine/Mercado/MercadoAlertas.cs`
- Create: `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoAlertaCalculador.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoAlertaCalculadorTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `MercadoAlertas.SemAlerta` / `.Ruptura` / `.SemCausa` / `.NaoApurado` — `const string`, valores literais iguais aos nomes.
  - `MercadoAlertaCalculador.LimiarDeAlerta` — `const decimal` = `0.5m`.
  - `record struct SinalBruto(decimal UnidadesRede, decimal UnidadesConcorrentes, decimal FatiaAgregadaDaRede, int? DiasSemEstoque)`.
  - `record struct AlertaCalculado(decimal Indice, string Alerta)`.
  - `MercadoAlertaCalculador.Calcular(SinalBruto) -> AlertaCalculado?` — nulo quando não há como calcular índice.

- [ ] **Step 1: Escrever o teste que falha**

Criar `tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoAlertaCalculadorTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Engine.Mercado;
using CosmosPro.ML.DemandForCast.Worker.Mercado;
using FluentAssertions;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

public class MercadoAlertaCalculadorTests
{
    // Fatia agregada de 20%: é a referência interna da rede naquele brick e mês.
    private const decimal FatiaAgregada = 0.20m;

    [Fact]
    public void Item_no_mesmo_patamar_da_rede_tem_indice_um_e_nao_alerta()
    {
        // 20 nossas de 100 no brick = 20% = exatamente a fatia agregada.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(UnidadesRede: 20m, UnidadesConcorrentes: 80m,
                           FatiaAgregadaDaRede: FatiaAgregada, DiasSemEstoque: 0));

        r.Should().NotBeNull();
        r!.Value.Indice.Should().BeApproximately(1m, 0.0001m);
        r.Value.Alerta.Should().Be(MercadoAlertas.SemAlerta);
    }

    [Fact]
    public void Item_com_metade_do_patamar_dispara_e_a_ruptura_explica()
    {
        // 10 nossas de 100 = 10% = metade da fatia agregada -> indice 0,5.
        // O limiar é estrito: 0,5 exato NÃO dispara.
        var noLimiar = MercadoAlertaCalculador.Calcular(
            new SinalBruto(10m, 90m, FatiaAgregada, 0));
        noLimiar!.Value.Alerta.Should().Be(MercadoAlertas.SemAlerta);

        // 5 nossas de 100 = 5% -> indice 0,25, abaixo do limiar.
        var abaixo = MercadoAlertaCalculador.Calcular(
            new SinalBruto(5m, 95m, FatiaAgregada, DiasSemEstoque: 3));
        abaixo!.Value.Indice.Should().BeApproximately(0.25m, 0.0001m);
        abaixo.Value.Alerta.Should().Be(MercadoAlertas.Ruptura);
    }

    [Fact]
    public void Sem_ruptura_no_mes_a_baixa_participacao_fica_sem_causa()
    {
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(5m, 95m, FatiaAgregada, DiasSemEstoque: 0));

        r!.Value.Alerta.Should().Be(MercadoAlertas.SemCausa);
    }

    [Fact]
    public void Estoque_nao_apurado_nao_vira_sem_causa()
    {
        // DiasSemEstoque nulo = o mês comparado não está no histórico importado.
        // Dizer "sem causa" afirmaria que não houve ruptura, o que ninguém checou.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(5m, 95m, FatiaAgregada, DiasSemEstoque: null));

        r!.Value.Alerta.Should().Be(MercadoAlertas.NaoApurado);
    }

    [Fact]
    public void Venda_nossa_zero_com_mercado_vendendo_e_o_alerta_mais_forte()
    {
        // Zero é medição, não ausência: o item está no cadastro, o bairro vende,
        // nós vendemos nada. Índice 0 e alerta, nunca nulo.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(0m, 500m, FatiaAgregada, DiasSemEstoque: 0));

        r.Should().NotBeNull();
        r!.Value.Indice.Should().Be(0m);
        r.Value.Alerta.Should().Be(MercadoAlertas.SemCausa);
    }

    [Fact]
    public void Sem_ninguem_vendendo_nao_ha_indice()
    {
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(0m, 0m, FatiaAgregada, DiasSemEstoque: 0));

        r.Should().BeNull();
    }

    [Fact]
    public void Fatia_agregada_zero_nao_ha_indice()
    {
        // Rede sem venda nenhuma no brick: dividir por zero produziria infinito,
        // e "infinitamente acima do normal" não é afirmação que a tela possa fazer.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(10m, 90m, FatiaAgregadaDaRede: 0m, DiasSemEstoque: 0));

        r.Should().BeNull();
    }
}
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Worker.Tests --filter MercadoAlertaCalculadorTests`
Expected: FAIL na compilação — `MercadoAlertas` e `MercadoAlertaCalculador` não existem.

- [ ] **Step 3: Escrever as constantes**

Criar `CosmosPro.ML.DemandForCast.Engine/Mercado/MercadoAlertas.cs`:

```csharp
namespace CosmosPro.ML.DemandForCast.Engine.Mercado;

/// <summary>
/// Os valores que <c>ComparacaoSessaoItem.MercadoAlerta</c> aceita. Vive no Engine, e
/// não no Worker, porque quem escreve (Worker) e quem filtra (ApiService) precisam do
/// mesmo texto — divergir aqui produziria um filtro que nunca casa, sem erro nenhum.
///
/// <para>
/// <b>Nulo não está nesta lista de propósito:</b> ele significa "não avaliado" (falta
/// dado de mercado para o item), enquanto <see cref="SemAlerta"/> significa "avaliado e
/// dentro do esperado". A coluna diz sozinha se houve avaliação.
/// </para>
/// </summary>
public static class MercadoAlertas
{
    public const string SemAlerta = nameof(SemAlerta);
    public const string Ruptura = nameof(Ruptura);
    public const string SemCausa = nameof(SemCausa);

    /// <summary>
    /// Participação abaixo do limiar, mas o mês comparado não está no histórico de
    /// estoque importado. Não é <see cref="SemCausa"/>: aquele afirma que não houve
    /// ruptura, e aqui ninguém verificou.
    /// </summary>
    public const string NaoApurado = nameof(NaoApurado);

    /// <summary>Maior tamanho de texto entre os valores, para a coluna do banco.</summary>
    public const int TamanhoMaximo = 20;
}
```

- [ ] **Step 4: Escrever o calculador**

Criar `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoAlertaCalculador.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Engine.Mercado;

namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

/// <param name="FatiaAgregadaDaRede">
/// Fatia de unidades que a rede tem no brick e mês somando <b>todos</b> os EANs. É a
/// referência interna: o índice mede o item contra ela, não contra o número de lojas.
/// A régua por número de lojas exigiria o contador de PDVs concorrentes, que o
/// relatório da IQVIA só publica numa área de tabela dinâmica que o parser ignora.
/// </param>
/// <param name="DiasSemEstoque">
/// Dias em que a loja ficou sem estoque do SKU <b>no mês comparado</b>. Nulo quando
/// aquele mês não está no histórico de estoque importado — que é diferente de zero.
/// </param>
internal readonly record struct SinalBruto(
    decimal UnidadesRede,
    decimal UnidadesConcorrentes,
    decimal FatiaAgregadaDaRede,
    int? DiasSemEstoque);

internal readonly record struct AlertaCalculado(decimal Indice, string Alerta);

/// <summary>
/// Regra B2/B3/B6 do documento de controle da IQVIA, em forma pura. Recebe medidas
/// prontas e devolve o índice e a classificação; quem busca no banco é o
/// <see cref="MercadoSinalLoader"/>.
/// </summary>
internal static class MercadoAlertaCalculador
{
    /// <summary>
    /// "Mais de 50% abaixo do mercado" do documento de controle, lido literalmente:
    /// dispara com índice <b>estritamente</b> menor que 0,5.
    /// </summary>
    public const decimal LimiarDeAlerta = 0.5m;

    public static AlertaCalculado? Calcular(SinalBruto sinal)
    {
        var totalDoBrick = sinal.UnidadesRede + sinal.UnidadesConcorrentes;

        // Ninguém vendeu, ou a rede não vendeu nada no brick inteiro: não há índice.
        // Devolver zero afirmaria desempenho péssimo onde não houve medição.
        if (totalDoBrick <= 0m || sinal.FatiaAgregadaDaRede <= 0m) return null;

        var fatiaDoItem = sinal.UnidadesRede / totalDoBrick;
        var indice = fatiaDoItem / sinal.FatiaAgregadaDaRede;

        var alerta = indice >= LimiarDeAlerta
            ? MercadoAlertas.SemAlerta
            : sinal.DiasSemEstoque switch
            {
                null => MercadoAlertas.NaoApurado,
                > 0 => MercadoAlertas.Ruptura,
                _ => MercadoAlertas.SemCausa,
            };

        return new AlertaCalculado(indice, alerta);
    }
}
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Worker.Tests --filter MercadoAlertaCalculadorTests`
Expected: PASS, 7 testes.

- [ ] **Step 6: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Engine/Mercado/MercadoAlertas.cs \
        CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoAlertaCalculador.cs \
        tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoAlertaCalculadorTests.cs
git commit -m "feat(mercado): compute the market performance index and its alert

Regra B2/B3/B6 em forma pura. O índice é a fatia do item no brick dividida
pela fatia agregada da rede no mesmo brick -- a régua por número de lojas
exigiria o contador de PDVs, que o parser não captura.

Quatro estados, e o quarto não é enfeite: NaoApurado cobre participação baixa
com mês fora do histórico de estoque. Colapsá-lo em SemCausa afirmaria que
não houve ruptura, o que ninguém checou.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 3: Escolher o mês da IQVIA

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoMesResolver.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoMesResolverTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `MercadoMesResolver.Resolver(IEnumerable<DateOnly> mesesCobertos, DateOnly diaDaSugestao) -> DateOnly?`

- [ ] **Step 1: Escrever o teste que falha**

Criar `tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoMesResolverTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Worker.Mercado;
using FluentAssertions;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

public class MercadoMesResolverTests
{
    private static DateOnly Mes(int ano, int mes) => new(ano, mes, 1);

    [Fact]
    public void Com_um_arquivo_so_cai_no_espelho_do_ano_anterior()
    {
        // O arquivo mensal da IQVIA traz o mês corrente e o mesmo mês do ano
        // anterior. Sugestão de junho/2026 com só esse arquivo carregado tem de
        // usar junho/2025: junho/2026 conteria as consequências da própria sugestão.
        var cobertos = new[] { Mes(2025, 6), Mes(2026, 6) };

        MercadoMesResolver.Resolver(cobertos, new DateOnly(2026, 6, 10))
            .Should().Be(Mes(2025, 6));
    }

    [Fact]
    public void Com_a_serie_empilhada_usa_o_mes_imediatamente_anterior()
    {
        var cobertos = new[]
        {
            Mes(2025, 6), Mes(2026, 4), Mes(2026, 5), Mes(2026, 6),
        };

        MercadoMesResolver.Resolver(cobertos, new DateOnly(2026, 6, 10))
            .Should().Be(Mes(2026, 5));
    }

    [Fact]
    public void O_mes_da_propria_sugestao_nunca_e_escolhido()
    {
        MercadoMesResolver.Resolver([Mes(2026, 6)], new DateOnly(2026, 6, 1))
            .Should().BeNull();
    }

    [Fact]
    public void Mes_posterior_a_sugestao_nunca_e_escolhido()
    {
        MercadoMesResolver.Resolver([Mes(2026, 7), Mes(2026, 8)], new DateOnly(2026, 6, 30))
            .Should().BeNull();
    }

    [Fact]
    public void Sem_cobertura_nenhuma_devolve_nulo()
    {
        MercadoMesResolver.Resolver([], new DateOnly(2026, 6, 10)).Should().BeNull();
    }

    [Fact]
    public void O_primeiro_dia_do_mes_da_sugestao_e_o_corte()
    {
        // Sugestão em 01/06/2026: maio/2026 serve, junho/2026 não.
        var cobertos = new[] { Mes(2026, 5), Mes(2026, 6) };

        MercadoMesResolver.Resolver(cobertos, new DateOnly(2026, 6, 1))
            .Should().Be(Mes(2026, 5));
    }
}
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Worker.Tests --filter MercadoMesResolverTests`
Expected: FAIL na compilação — `MercadoMesResolver` não existe.

- [ ] **Step 3: Escrever o resolvedor**

Criar `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoMesResolver.cs`:

```csharp
namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

/// <summary>
/// Escolhe qual mês da IQVIA a sessão compara: o <b>último mês coberto estritamente
/// anterior ao mês da sugestão</b>. Uma regra, sem caso especial.
///
/// <para>
/// <b>Por que estritamente anterior.</b> O mês da sugestão contém as consequências da
/// própria sugestão. Para diagnóstico retrospectivo isso passaria; para a afirmação que
/// a dissertação sustenta — "o alerta da IQVIA teria avisado o comprador" — é circular.
/// O atraso da fonte confirma: o relatório de junho/2026 chegou em agosto/2026.
/// </para>
///
/// <para>
/// Com um arquivo só carregado a regra cai no espelho do ano anterior, que todo
/// relatório traz e que é sazonalmente casado. Conforme a rede empilha arquivos, ela
/// passa ao mês imediatamente anterior sem mudança de código.
/// </para>
/// </summary>
internal static class MercadoMesResolver
{
    /// <param name="mesesCobertos">
    /// Meses efetivamente cobertos pelas cargas da rede — vem da cobertura declarada,
    /// nunca da existência de linhas em <c>MercadoObservacoes</c>: célula zerada não
    /// gera linha, e inferir cobertura da ausência confundiria "vendeu zero" com "nunca
    /// enviado".
    /// </param>
    /// <param name="diaDaSugestao">Dia da sugestão do ERP; só o mês dele é usado.</param>
    public static DateOnly? Resolver(IEnumerable<DateOnly> mesesCobertos, DateOnly diaDaSugestao)
    {
        var corte = new DateOnly(diaDaSugestao.Year, diaDaSugestao.Month, 1);

        DateOnly? escolhido = null;
        foreach (var mes in mesesCobertos)
        {
            if (mes >= corte) continue;
            if (escolhido is null || mes > escolhido.Value) escolhido = mes;
        }

        return escolhido;
    }
}
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Worker.Tests --filter MercadoMesResolverTests`
Expected: PASS, 6 testes.

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoMesResolver.cs \
        tests/CosmosPro.ML.DemandForCast.Worker.Tests/MercadoMesResolverTests.cs
git commit -m "feat(mercado): pick the last covered month before the suggestion

Uma regra, sem caso especial. Com um arquivo só carregado ela cai no espelho
do ano anterior; com a série empilhada, no mês imediatamente anterior.

Estritamente anterior porque o mês da sugestão contém as consequências dela
-- comparar contra ele tornaria circular a afirmação de que o alerta teria
avisado o comprador.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 4: Colunas novas no item da sessão

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Engine/Entities/ComparacaoSessaoItem.cs`
- Modify: `CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs` (bloco `modelBuilder.Entity<ComparacaoSessaoItem>`, junto às chamadas `HasPrecision` existentes por volta da linha 201)
- Create: migration `AddMercadoNoItemDaSessao` (gerada)
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/MercadoSinalIntegrationTests.cs`

**Interfaces:**
- Consumes: `MercadoAlertas.TamanhoMaximo` (Task 2).
- Produces: as 7 propriedades em `ComparacaoSessaoItem` — `MercadoMes` (`DateOnly?`), `MercadoBrick` (`string?`), `MercadoUnidadesRede` (`decimal?`), `MercadoUnidadesConcorrentes` (`decimal?`), `MercadoIndiceDesempenho` (`decimal?`), `MercadoDiasSemEstoque` (`int?`), `MercadoAlerta` (`string?`).

- [ ] **Step 1: Escrever o teste que falha**

Criar `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/MercadoSinalIntegrationTests.cs`. Seguir o padrão de fixture dos testes de integração vizinhos (`MercadoCoberturaExclusaoIntegrationTests.cs` é o exemplo mais próximo — copiar dele a montagem de `AppHostFixture`, o `EngineDbContext` e a criação de rede/sessão).

```csharp
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Engine.Mercado;
using FluentAssertions;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

[Collection(AppHostCollection.Name)]
public class MercadoSinalIntegrationTests(AppHostFixture fixture)
{
    [Fact]
    public async Task As_colunas_de_mercado_fazem_round_trip_no_banco()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        var (redeId, sessaoId) = await SemearSessaoAsync(db);

        db.ComparacaoSessaoItens.Add(new ComparacaoSessaoItem
        {
            SessaoId = sessaoId,
            LojaId = 12,
            Sku = "401882",
            CompraSugeridaPbs = 48m,
            VendidoNaJanela = 10m,
            DemandaDiaPbs = 1.5m,
            SobraPbsUnidades = 3m,
            MercadoMes = new DateOnly(2025, 6, 1),
            MercadoBrick = "528-RJ VOLTA REDONDA RETIRO",
            MercadoUnidadesRede = 1.234m,
            MercadoUnidadesConcorrentes = 5000.500m,
            MercadoIndiceDesempenho = 0.1234m,
            MercadoDiasSemEstoque = 3,
            MercadoAlerta = MercadoAlertas.Ruptura,
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var lido = await db.ComparacaoSessaoItens
            .SingleAsync(i => i.SessaoId == sessaoId && i.LojaId == 12 && i.Sku == "401882");

        lido.MercadoMes.Should().Be(new DateOnly(2025, 6, 1));
        lido.MercadoBrick.Should().Be("528-RJ VOLTA REDONDA RETIRO");
        // As três casas dos milhares de unidades e as quatro do índice têm de
        // sobreviver: o default do EF Core é decimal(18,2) e truncaria em silêncio.
        lido.MercadoUnidadesRede.Should().Be(1.234m);
        lido.MercadoUnidadesConcorrentes.Should().Be(5000.500m);
        lido.MercadoIndiceDesempenho.Should().Be(0.1234m);
        lido.MercadoDiasSemEstoque.Should().Be(3);
        lido.MercadoAlerta.Should().Be(MercadoAlertas.Ruptura);
    }

    [Fact]
    public async Task Item_sem_dado_de_mercado_grava_nulo_e_nao_zero()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        var (_, sessaoId) = await SemearSessaoAsync(db);

        db.ComparacaoSessaoItens.Add(new ComparacaoSessaoItem
        {
            SessaoId = sessaoId,
            LojaId = 7,
            Sku = "118902",
            CompraSugeridaPbs = 0m,
            VendidoNaJanela = 0m,
            DemandaDiaPbs = 0m,
            SobraPbsUnidades = 0m,
            // Nenhuma coluna de mercado atribuída: loja sem CNPJ, SKU sem EAN,
            // mês não coberto -- todos legítimos, todos nulo e nunca zero.
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var lido = await db.ComparacaoSessaoItens
            .SingleAsync(i => i.SessaoId == sessaoId && i.LojaId == 7);

        lido.MercadoMes.Should().BeNull();
        lido.MercadoBrick.Should().BeNull();
        lido.MercadoUnidadesRede.Should().BeNull();
        lido.MercadoUnidadesConcorrentes.Should().BeNull();
        lido.MercadoIndiceDesempenho.Should().BeNull();
        lido.MercadoDiasSemEstoque.Should().BeNull();
        lido.MercadoAlerta.Should().BeNull();
    }
}
```

O helper `SemearSessaoAsync` cria uma `Rede` e uma `ComparacaoSessao` mínimas e devolve os ids. Copiar a forma exata do arquivo de integração vizinho, para não divergir na criação de inquilino.

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter MercadoSinalIntegrationTests`
Expected: FAIL na compilação — as 7 propriedades não existem.

- [ ] **Step 3: Adicionar as propriedades na entidade**

Em `CosmosPro.ML.DemandForCast.Engine/Entities/ComparacaoSessaoItem.cs`, **depois** de `JanelaAlemDoHistorico`:

```csharp
    // --- Sinal de mercado da IQVIA (F16 parte C, grupo B) --------------------------
    //
    // Nulo em todas as sete significa "não foi possível calcular", nunca zero — o
    // mesmo contrato das colunas do braço de ML, e pelo mesmo motivo. Cinco causas
    // legítimas: loja sem Cnpj no Stage, CNPJ fora do painel da IQVIA, SKU sem Ean,
    // EAN que a IQVIA não reportou, e nenhum mês coberto antes do mês da sugestão.

    /// <summary>
    /// Mês da IQVIA que este item comparou (primeiro dia do mês). Gravado por linha
    /// porque a tela precisa dizer contra o que comparou: a cobertura da rede muda
    /// conforme novos relatórios entram, e uma sessão antiga tem de continuar
    /// declarando o mês que ela realmente usou.
    /// </summary>
    public DateOnly? MercadoMes { get; set; }

    /// <summary>Brick da loja, resolvido pelo CNPJ dela no painel da IQVIA.</summary>
    public string? MercadoBrick { get; set; }

    /// <summary>
    /// Unidades que a IQVIA atribuiu às lojas da rede, neste EAN, neste brick e mês.
    /// <b>Zero é medição</b>, e zero aqui com concorrentes vendendo é o alerta mais
    /// forte que existe: o item está no cadastro, está na sugestão, o bairro vende, e
    /// a rede vendeu nada.
    /// </summary>
    public decimal? MercadoUnidadesRede { get; set; }

    /// <summary>Unidades do agregado de concorrentes, no mesmo recorte.</summary>
    public decimal? MercadoUnidadesConcorrentes { get; set; }

    /// <summary>
    /// Fatia da rede neste item dividida pela fatia agregada da rede no mesmo brick e
    /// mês. 1,0 = o item vai tão bem quanto a rede vai naquele bairro; abaixo de 0,5
    /// dispara alerta. Não é fatia de mercado — é desempenho relativo à própria rede,
    /// justamente para o tamanho dela no bairro não contaminar a leitura.
    /// </summary>
    public decimal? MercadoIndiceDesempenho { get; set; }

    /// <summary>
    /// Dias em que a loja ficou sem estoque deste SKU <b>no mês comparado</b> (não na
    /// janela de cobertura). É a evidência da regra B3. Nulo quando aquele mês não
    /// está no histórico de estoque importado — diferente de zero, que afirma que
    /// havia estoque todos os dias.
    /// </summary>
    public int? MercadoDiasSemEstoque { get; set; }

    /// <summary>
    /// Classificação do alerta: um dos valores de
    /// <c>CosmosPro.ML.DemandForCast.Engine.Mercado.MercadoAlertas</c>. Nulo significa
    /// <b>não avaliado</b> (sem dado de mercado), e não "está tudo bem" — para isso
    /// existe <c>MercadoAlertas.SemAlerta</c>.
    /// </summary>
    public string? MercadoAlerta { get; set; }
```

- [ ] **Step 4: Configurar precisão e tamanho**

Em `CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs`, no bloco `modelBuilder.Entity<ComparacaoSessaoItem>`, logo após `b.Property(x => x.SobraMlValor).HasPrecision(14, 4);`:

```csharp
            // Unidades espelham MercadoObservacao.Unidades (15,3). O índice ganha
            // (9,4): o teto teórico é 1 / fatia agregada, e fatia de 1% já daria 100.
            b.Property(x => x.MercadoUnidadesRede).HasPrecision(15, 3);
            b.Property(x => x.MercadoUnidadesConcorrentes).HasPrecision(15, 3);
            b.Property(x => x.MercadoIndiceDesempenho).HasPrecision(9, 4);
            b.Property(x => x.MercadoBrick).HasMaxLength(80);
            b.Property(x => x.MercadoAlerta).HasMaxLength(MercadoAlertas.TamanhoMaximo);

            // Índice para o filtro "só itens com alerta" da tela de itens não varrer a
            // tabela inteira. SessaoId primeiro porque a consulta sempre fixa a sessão.
            b.HasIndex(x => new { x.SessaoId, x.MercadoAlerta });
```

Adicionar o `using CosmosPro.ML.DemandForCast.Engine.Mercado;` no topo do arquivo se ainda não existir.

- [ ] **Step 5: Gerar a migration**

```bash
dotnet ef migrations add AddMercadoNoItemDaSessao \
  --project CosmosPro.ML.DemandForCast.Engine \
  --startup-project CosmosPro.ML.DemandForCast.ApiService
```

Conferir no arquivo gerado: as 7 colunas são `nullable: true`, e **não há `defaultValue`**. Default zero faria o banco afirmar "a IQVIA mediu zero" em toda linha antiga.

Sem backfill, de propósito: o dado de mercado de uma sessão antiga descreveria o Stage do envio atual — o mesmo motivo pelo qual `Categoria` entrou sem backfill.

**Atenção:** não criar `IDesignTimeDbContextFactory` no projeto Engine. A ferramenta do EF prefere o factory ao service provider do Aspire e aplicaria a migration no banco errado.

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter MercadoSinalIntegrationTests`
Expected: PASS, 2 testes.

- [ ] **Step 7: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Engine/Entities/ComparacaoSessaoItem.cs \
        CosmosPro.ML.DemandForCast.Engine/EngineDbContext.cs \
        CosmosPro.ML.DemandForCast.Engine/Migrations/ \
        tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/MercadoSinalIntegrationTests.cs
git commit -m "feat(mercado): materialize the market signal on the session item

Sete colunas anuláveis em ComparacaoSessaoItens. Materializadas, e não
consultadas na abertura da tela, porque o Stage da rede é apagado a cada
import: o cálculo ao vivo devolveria o mercado do envio seguinte com a cara
do anterior.

Nulo é 'não deu para calcular', nunca zero -- zero é medição da IQVIA, e
zero nosso com o bairro vendendo é o alerta mais forte que existe. Migration
sem backfill nem defaultValue, pelo mesmo motivo de Categoria.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Antes das Tasks 5 a 8: leia o arquivo antes de editar

As Tasks 1 a 4 vêm com o código completo — pode digitar como está.

**As Tasks 5 a 8 vêm com as interfaces exatas e o código de teste completo, mas não com o
corpo da implementação.** Isso é deliberado: elas editam arquivos grandes e já existentes
(`MercadoSinalLoader` é uma classe nova de quatro consultas; `GravarAsync` monta um
`DataTable` com dezenas de colunas; o Razor da tabela de itens tem filtros e totalizadores
já montados). Código inventado contra esses arquivos não compilaria, e um trecho que não
compila atrasa mais do que uma instrução precisa.

**Regra para essas quatro tarefas:** abra o arquivo, encontre o padrão que os vizinhos já
usam, e siga. Os testes deste plano são o contrato — eles dizem exatamente o que a
implementação tem de fazer. Onde o teste chama um helper que ainda não existe
(`SemearCenarioDeMercadoAsync`, `SemearQuatroItensComAlertasAsync`, `ItemComMercado`),
escreva-o copiando a forma do arquivo de teste vizinho citado na tarefa.

---

## Task 5: Carregar o sinal de mercado do banco

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoSinalLoader.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/MercadoSinalIntegrationTests.cs` (adicionar testes ao arquivo da Task 4)

**Interfaces:**
- Consumes: `MercadoMesResolver.Resolver` (Task 3), `MercadoAlertaCalculador.Calcular` e `SinalBruto` (Task 2).
- Produces:
  - `record SinalDoItem(DateOnly Mes, string Brick, decimal UnidadesRede, decimal UnidadesConcorrentes, decimal Indice, int? DiasSemEstoque, string Alerta)`
  - `MercadoSinalLoader.CarregarAsync(int redeId, DateOnly diaDaSugestao, DateOnly janelaInicio, IReadOnlyCollection<(int LojaId, string Sku)> itens, CancellationToken ct) -> Task<IReadOnlyDictionary<(int LojaId, string Sku), SinalDoItem>>`

Chaves ausentes do dicionário são itens sem dado de mercado — o montador grava nulo neles.

- [ ] **Step 1: Escrever os testes que falham**

Adicionar a `MercadoSinalIntegrationTests.cs`:

```csharp
    [Fact]
    public async Task Liga_a_loja_ao_brick_pelo_cnpj_e_o_sku_ao_ean()
    {
        // Cenário: 1 loja (CNPJ no painel do brick 528), 1 SKU com EAN que a IQVIA
        // reportou. Rede com 100 unidades e concorrentes com 900 no brick inteiro
        // (fatia agregada 10%); neste EAN a rede tem 1 de 100 (fatia 1%), então o
        // índice é 0,1 e o alerta dispara.
        var cenario = await SemearCenarioDeMercadoAsync(
            cnpjDaLoja: "07381852002835",
            brick: "528-RJ VOLTA REDONDA RETIRO",
            ean: "7891721201806",
            mes: new DateOnly(2025, 6, 1));

        var sinais = await cenario.Loader.CarregarAsync(
            cenario.RedeId,
            diaDaSugestao: new DateOnly(2026, 6, 10),
            janelaInicio: new DateOnly(2025, 6, 1),
            itens: [(cenario.LojaId, cenario.Sku)],
            ct: TestContext.Current.CancellationToken);

        sinais.Should().ContainKey((cenario.LojaId, cenario.Sku));
        var s = sinais[(cenario.LojaId, cenario.Sku)];
        s.Mes.Should().Be(new DateOnly(2025, 6, 1));
        s.Brick.Should().Be("528-RJ VOLTA REDONDA RETIRO");
        s.Indice.Should().BeApproximately(0.1m, 0.001m);
        s.Alerta.Should().Be(MercadoAlertas.SemCausa);
    }

    [Fact]
    public async Task Loja_sem_cnpj_fica_fora_do_sinal()
    {
        var cenario = await SemearCenarioDeMercadoAsync(
            cnpjDaLoja: null,
            brick: "528-RJ VOLTA REDONDA RETIRO",
            ean: "7891721201806",
            mes: new DateOnly(2025, 6, 1));

        var sinais = await cenario.Loader.CarregarAsync(
            cenario.RedeId, new DateOnly(2026, 6, 10), new DateOnly(2025, 6, 1),
            [(cenario.LojaId, cenario.Sku)], TestContext.Current.CancellationToken);

        sinais.Should().BeEmpty();
    }

    [Fact]
    public async Task Sku_sem_ean_fica_fora_do_sinal()
    {
        var cenario = await SemearCenarioDeMercadoAsync(
            cnpjDaLoja: "07381852002835",
            brick: "528-RJ VOLTA REDONDA RETIRO",
            ean: null,
            mes: new DateOnly(2025, 6, 1));

        var sinais = await cenario.Loader.CarregarAsync(
            cenario.RedeId, new DateOnly(2026, 6, 10), new DateOnly(2025, 6, 1),
            [(cenario.LojaId, cenario.Sku)], TestContext.Current.CancellationToken);

        sinais.Should().BeEmpty();
    }

    [Fact]
    public async Task Sem_mes_coberto_antes_da_sugestao_nao_ha_sinal()
    {
        // Único mês coberto é o próprio mês da sugestão.
        var cenario = await SemearCenarioDeMercadoAsync(
            cnpjDaLoja: "07381852002835",
            brick: "528-RJ VOLTA REDONDA RETIRO",
            ean: "7891721201806",
            mes: new DateOnly(2026, 6, 1));

        var sinais = await cenario.Loader.CarregarAsync(
            cenario.RedeId, new DateOnly(2026, 6, 10), new DateOnly(2025, 6, 1),
            [(cenario.LojaId, cenario.Sku)], TestContext.Current.CancellationToken);

        sinais.Should().BeEmpty();
    }

    [Fact]
    public async Task Mes_fora_do_historico_de_estoque_vira_nao_apurado()
    {
        // Mês comparado (junho/2025) anterior ao início da janela importada
        // (janeiro/2026): não há snapshot de estoque para checar ruptura.
        var cenario = await SemearCenarioDeMercadoAsync(
            cnpjDaLoja: "07381852002835",
            brick: "528-RJ VOLTA REDONDA RETIRO",
            ean: "7891721201806",
            mes: new DateOnly(2025, 6, 1));

        var sinais = await cenario.Loader.CarregarAsync(
            cenario.RedeId, new DateOnly(2026, 6, 10),
            janelaInicio: new DateOnly(2026, 1, 1),
            [(cenario.LojaId, cenario.Sku)], TestContext.Current.CancellationToken);

        sinais[(cenario.LojaId, cenario.Sku)].DiasSemEstoque.Should().BeNull();
        sinais[(cenario.LojaId, cenario.Sku)].Alerta.Should().Be(MercadoAlertas.NaoApurado);
    }
```

`SemearCenarioDeMercadoAsync` é um helper novo neste arquivo de teste. Ele precisa criar, nos dois bancos:

- `engine`: `Rede`, `MercadoCarga` concluída com `ResumoJson` declarando o mês e o brick, `MercadoBrickPdvs` (a loja e o agregado `00000000000000`), `MercadoObservacoes` — quatro linhas: a rede e os concorrentes no EAN do teste, e mais um EAN qualquer para a fatia agregada não ser igual à fatia do item.
- `Stage`: `Lojas` com o `Cnpj`, `Produtos` com o `Ean`, e `EstoquesDiarios` cobrindo o mês quando o teste espera ruptura apurada.

Usar `IqviaXlsxBuilder` de `tests/CosmosPro.ML.DemandForCast.Tests.Shared/Xlsx/` para gerar o arquivo e passar pelo import real, em vez de inserir em `MercadoObservacoes` na mão. Assim o teste também cobre a cobertura declarada, que é o que `MercadoMesResolver` consome.

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter MercadoSinalIntegrationTests`
Expected: FAIL na compilação — `MercadoSinalLoader` não existe.

- [ ] **Step 3: Escrever o loader**

Criar `CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoSinalLoader.cs`. Estrutura obrigatória, em quatro consultas:

1. **Meses cobertos** — ler a cobertura das `MercadoCargas` concluídas da rede (o mesmo agregado que `GET /api/mercado/cobertura` devolve; reaproveitar a consulta, não recriá-la). Passar a `MercadoMesResolver.Resolver` junto com `diaDaSugestao`. **Se vier nulo, devolver dicionário vazio e sair** — sem mês, não há sinal.
2. **Loja → brick** (banco `Stage` + `engine`): `SELECT LojaId, Cnpj FROM Lojas WHERE RedeId = @redeId AND Cnpj IS NOT NULL`, cruzado com `MercadoBrickPdvs` por CNPJ. Loja sem CNPJ ou fora do painel não entra.
3. **SKU → EAN** (banco `Stage`): `SELECT Sku, Ean FROM Produtos WHERE RedeId = @redeId AND Ean IS NOT NULL` — restrito aos SKUs de `itens` por join com tabela temporária, **nunca** `Sku IN (@s0…@sN)`: o teto de 2.100 parâmetros do SQL Server estoura numa sugestão grande. Seguir o padrão de `Worker/Training/EscopoDeSkus.cs`, que existe exatamente para isso. Normalizar o EAN conforme a decisão da Task 1.
4. **Observações e ruptura**:
   - `MercadoObservacoes` da rede no mês escolhido, nos bricks das lojas envolvidas. Somar por `(Brick, Ean)` separando bandeira `CONCORRENTES` de qualquer outra (as demais são bandeiras próprias, e a rede é a soma delas).
   - Fatia agregada por brick: somar **todas** as linhas daquele brick e mês, não só os EANs dos itens.
   - `Stage.EstoquesDiarios`: contar dias com `QuantidadeEmEstoque <= 0` por `(LojaId, Sku)` dentro do mês escolhido. **Se o mês escolhido começar antes de `janelaInicio`, devolver `null`** em `DiasSemEstoque` — não há snapshot, e zero afirmaria que havia estoque todos os dias.

Montar `SinalBruto` por item e chamar `MercadoAlertaCalculador.Calcular`. Item cujo cálculo devolve nulo **não entra no dicionário**.

Assinatura exata:

```csharp
internal sealed record SinalDoItem(
    DateOnly Mes,
    string Brick,
    decimal UnidadesRede,
    decimal UnidadesConcorrentes,
    decimal Indice,
    int? DiasSemEstoque,
    string Alerta);

internal sealed class MercadoSinalLoader(
    string stageConnectionString,
    IServiceProvider services,
    ILogger logger)   // ILogger e nao ILogger<T>: o materializador passa o proprio logger,
                      // e resolver um tipado exigiria um scope so para isso
{
    public Task<IReadOnlyDictionary<(int LojaId, string Sku), SinalDoItem>> CarregarAsync(
        int redeId,
        DateOnly diaDaSugestao,
        DateOnly janelaInicio,
        IReadOnlyCollection<(int LojaId, string Sku)> itens,
        CancellationToken ct);
}
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter MercadoSinalIntegrationTests`
Expected: PASS, 7 testes (2 da Task 4 + 5 desta).

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Worker/Mercado/MercadoSinalLoader.cs \
        tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/MercadoSinalIntegrationTests.cs
git commit -m "feat(mercado): bridge store to brick by CNPJ and SKU to EAN

As duas pontes que ligam a sessão ao dado de mercado. Item que não atravessa
qualquer uma delas fica fora do dicionário, e o montador grava nulo nele --
loja sem CNPJ, CNPJ fora do painel, SKU sem EAN, EAN não reportado, ou nenhum
mês coberto antes da sugestão.

O escopo de SKUs vai por tabela temporária, como em EscopoDeSkus: o
Sku IN (@s0..@sN) estouraria o teto de 2.100 parâmetros numa sugestão grande.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 6: Montador e materializador gravam o sinal

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Worker/Sessoes/SessaoResultadoMontador.cs:189` (assinatura de `Montar`) e o laço `foreach (var linha in populacao)`
- Modify: `CosmosPro.ML.DemandForCast.Worker/Sessoes/SessaoResultadoMaterializador.cs:75` (chamada de `Montar`) e o `DataTable` do `SqlBulkCopy` dentro de `GravarAsync`
- Test: `tests/CosmosPro.ML.DemandForCast.Worker.Tests/SessaoResultadoMontadorTests.cs` (arquivo existente)

**Interfaces:**
- Consumes: `SinalDoItem` e o dicionário de `MercadoSinalLoader.CarregarAsync` (Task 5); as 7 propriedades (Task 4).
- Produces: `Montar` com um parâmetro novo no fim — `IReadOnlyDictionary<(int LojaId, string Sku), SinalDoItem> sinaisDeMercado`.

- [ ] **Step 1: Escrever o teste que falha**

Adicionar a `SessaoResultadoMontadorTests.cs`. O arquivo já tem um wrapper privado
`Montar(...)` (linha ~350) que centraliza a chamada, e um `Linha(...)` que monta
`ItemDoStage`. **Estender o wrapper** para receber o dicionário com default vazio — assim
os testes existentes continuam compilando sem tocar em nenhum deles:

```csharp
    // No wrapper privado existente, adicionar o parâmetro no fim:
    private static Materializacao Montar(
        IReadOnlyList<ItemDoStage> populacao,
        int? skusSemCadastro = null,
        ComparisonResult? previsao = null,
        DecisionComparisonResult? decisao = null,
        IReadOnlyDictionary<(int LojaId, string Sku), SinalDoItem>? sinaisDeMercado = null)
        => SessaoResultadoMontador.Montar(
            sessaoId: SessaoId,
            skusSemCadastro: skusSemCadastro,
            comparacaoPbsId: ComparacaoPbsId,
            sugestaoDataHora: SugestaoDataHora,
            comparacao: Saida(
                previsao ?? Previsao([Pares()]),
                decisao ?? Decisao(UtilidadeComparacao.ForaDoHorizonteMl)),
            populacao: populacao,
            agora: Agora,
            sinaisDeMercado: sinaisDeMercado
                ?? new Dictionary<(int LojaId, string Sku), SinalDoItem>());
```

Manter os parâmetros opcionais que o wrapper já tiver; o importante é `sinaisDeMercado`
entrar por último e com default. Os dois testes novos:

```csharp
    [Fact]
    public void Item_com_sinal_de_mercado_recebe_as_sete_colunas()
    {
        var sinais = new Dictionary<(int LojaId, string Sku), SinalDoItem>
        {
            [(LojaId, Sku)] = new(
                Mes: new DateOnly(2025, 6, 1),
                Brick: "528-RJ VOLTA REDONDA RETIRO",
                UnidadesRede: 12m,
                UnidadesConcorrentes: 988m,
                Indice: 0.1234m,
                DiasSemEstoque: 3,
                Alerta: MercadoAlertas.Ruptura),
        };

        var m = Montar([Linha()], sinaisDeMercado: sinais);

        var item = m.Itens.Single(i => i.LojaId == LojaId && i.Sku == Sku);
        item.MercadoMes.Should().Be(new DateOnly(2025, 6, 1));
        item.MercadoBrick.Should().Be("528-RJ VOLTA REDONDA RETIRO");
        item.MercadoUnidadesRede.Should().Be(12m);
        item.MercadoUnidadesConcorrentes.Should().Be(988m);
        item.MercadoIndiceDesempenho.Should().Be(0.1234m);
        item.MercadoDiasSemEstoque.Should().Be(3);
        item.MercadoAlerta.Should().Be(MercadoAlertas.Ruptura);
    }

    [Fact]
    public void Item_sem_sinal_de_mercado_fica_com_as_sete_nulas()
    {
        // Dicionário vazio: nenhuma das duas pontes fechou para este item.
        var m = Montar([Linha()]);

        var item = m.Itens.Single(i => i.LojaId == LojaId && i.Sku == Sku);
        item.MercadoMes.Should().BeNull();
        item.MercadoBrick.Should().BeNull();
        item.MercadoIndiceDesempenho.Should().BeNull();
        item.MercadoDiasSemEstoque.Should().BeNull();
        item.MercadoAlerta.Should().BeNull();
        // Zero aqui diria ao comprador que o item vende zero no bairro -- e isso é uma
        // medição, que ninguém fez.
        item.MercadoUnidadesRede.Should().BeNull();
        item.MercadoUnidadesConcorrentes.Should().BeNull();
    }
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Worker.Tests --filter SessaoResultadoMontadorTests`
Expected: FAIL na compilação — `Montar` não tem o parâmetro `sinaisDeMercado`.

- [ ] **Step 3: Estender o montador**

Adicionar o parâmetro no fim da assinatura de `Montar`:

```csharp
    public static Materializacao Montar(
        Guid sessaoId,
        int? skusSemCadastro,
        Guid comparacaoPbsId,
        DateTime? sugestaoDataHora,
        ComparacaoOutput comparacao,
        IReadOnlyList<ItemDoStage> populacao,
        DateTimeOffset agora,
        IReadOnlyDictionary<(int LojaId, string Sku), SinalDoItem> sinaisDeMercado)
```

Dentro do laço `foreach (var linha in populacao)`, onde o `ComparacaoSessaoItem` é construído, atribuir as sete a partir do dicionário. Ausência da chave deixa as sete nulas — o `TryGetValue` já faz isso:

```csharp
            var sinal = sinaisDeMercado.TryGetValue((linha.Item.LojaId, linha.Item.Sku), out var s)
                ? s
                : null;
```

e no objeto:

```csharp
                MercadoMes = sinal?.Mes,
                MercadoBrick = sinal?.Brick,
                MercadoUnidadesRede = sinal?.UnidadesRede,
                MercadoUnidadesConcorrentes = sinal?.UnidadesConcorrentes,
                MercadoIndiceDesempenho = sinal?.Indice,
                MercadoDiasSemEstoque = sinal?.DiasSemEstoque,
                MercadoAlerta = sinal?.Alerta,
```

`SinalDoItem` é `record` (tipo de referência), então `sinal?.X` propaga nulo corretamente. Não trocar por `record struct`: com struct, `sinal?.UnidadesRede` sobre um `Nullable<SinalDoItem>` ainda funciona, mas `DiasSemEstoque` — que já é `int?` — passaria a `int??` e o compilador acusaria.

- [ ] **Step 4: Passar o sinal no materializador**

Em `SessaoResultadoMaterializador.cs`, antes da chamada de `Montar` (linha ~75), carregar o sinal. `populacao` já está montada, e é dela que saem as chaves:

```csharp
        var sinaisDeMercado = sessao.SugestaoDataHora is { } dataHora
            ? await new MercadoSinalLoader(stageConnStr, services, logger).CarregarAsync(
                  sessao.RedeId,
                  diaDaSugestao: DateOnly.FromDateTime(dataHora),
                  janelaInicio: DateOnly.FromDateTime(job.JanelaInicio),
                  itens: [.. populacao.Select(p => (p.Item.LojaId, p.Item.Sku))],
                  ct)
            : new Dictionary<(int, string), SinalDoItem>();
```

Sessão sem `SugestaoDataHora` não tem mês de corte, então não tem sinal — dicionário vazio, e as sete colunas ficam nulas. Passar `sinaisDeMercado` como último argumento de `Montar`.

Em `GravarAsync`, adicionar as sete colunas ao `DataTable` do `SqlBulkCopy`, **na mesma ordem** das `ColumnMappings`. Valor nulo vai como `DBNull.Value`, nunca `0`.

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Worker.Tests --filter SessaoResultadoMontadorTests`
Expected: PASS.

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter Sessao`
Expected: PASS — os testes de sessão existentes precisam continuar verdes; a chamada de `Montar` mudou de forma e eles a exercitam de ponta a ponta.

- [ ] **Step 6: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Worker/Sessoes/SessaoResultadoMontador.cs \
        CosmosPro.ML.DemandForCast.Worker/Sessoes/SessaoResultadoMaterializador.cs \
        tests/CosmosPro.ML.DemandForCast.Worker.Tests/SessaoResultadoMontadorTests.cs
git commit -m "feat(mercado): write the market signal during materialization

O sinal entra pelo caminho que o materializador já percorre, na mesma
transação do DELETE + bulk + UPDATE. Nenhuma fase nova na máquina de estados,
nenhuma fila nova.

Item sem sinal fica com as sete colunas nulas, e o DataTable do bulk manda
DBNull -- zero diria ao comprador que o item vende zero no bairro, o que é
medição, e ninguém mediu.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 7: API expõe as colunas e o filtro de alerta

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.ApiService/Comparacoes/ComparacoesEndpoints.cs` — DTO do item, `AplicarFiltros` (linha 632) e as duas chamadas dela (linhas 427 e 497)
- Test: `tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/FiltrosDosItensIntegrationTests.cs` (arquivo existente)

**Interfaces:**
- Consumes: as 7 propriedades (Task 4), `MercadoAlertas` (Task 2).
- Produces: parâmetro de query `somenteComAlerta` (`bool?`) nos endpoints de itens e de totais; 7 campos novos no DTO do item.

- [ ] **Step 1: Escrever o teste que falha**

Adicionar a `FiltrosDosItensIntegrationTests.cs`, seguindo a forma dos testes de filtro que já existem lá:

```csharp
    [Fact]
    public async Task Filtro_de_alerta_devolve_so_os_itens_com_alerta_de_verdade()
    {
        // Semear 4 itens: Ruptura, SemCausa, SemAlerta e sem dado (MercadoAlerta nulo).
        // O filtro tem de trazer 2 -- SemAlerta é "avaliado e está bem", e nulo é
        // "não avaliado"; nenhum dos dois é alerta.
        var (sessaoId, client) = await SemearQuatroItensComAlertasAsync();

        var pagina = await client.ItensDaSessaoAsync(sessaoId, somenteComAlerta: true);

        pagina.Itens.Should().HaveCount(2);
        pagina.Itens.Select(i => i.MercadoAlerta)
              .Should().BeEquivalentTo([MercadoAlertas.Ruptura, MercadoAlertas.SemCausa]);
    }

    [Fact]
    public async Task Os_totais_respeitam_o_filtro_de_alerta()
    {
        // Mesma cláusula para página e totais: se divergissem, o comprador veria
        // 2 itens na tela e um total apurado sobre 4.
        var (sessaoId, client) = await SemearQuatroItensComAlertasAsync();

        var comFiltro = await client.TotaisDaSessaoAsync(sessaoId, somenteComAlerta: true);
        var semFiltro = await client.TotaisDaSessaoAsync(sessaoId, somenteComAlerta: null);

        comFiltro.Itens.Should().Be(2);
        semFiltro.Itens.Should().Be(4);
    }

    [Fact]
    public async Task Sessao_de_outra_rede_responde_404_e_nao_403()
    {
        var (sessaoId, clientDeOutraRede) = await SemearSessaoDeOutraRedeAsync();

        var resposta = await clientDeOutraRede.ItensDaSessaoRawAsync(sessaoId);

        // 403 confirmaria a quem sondasse que a sessão existe em outro inquilino.
        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```

Adicionar `somenteComAlerta` à interface Refit `IComparacoesApi`.

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter FiltrosDosItens`
Expected: FAIL na compilação — `somenteComAlerta` não existe.

- [ ] **Step 3: Estender o filtro**

Em `AplicarFiltros` (linha 632), adicionar o parâmetro e a cláusula:

```csharp
    private static IQueryable<ComparacaoSessaoItem> AplicarFiltros(
        IQueryable<ComparacaoSessaoItem> q,
        int? lojaId,
        string? categoria,
        string? curva,
        bool? somenteComAlerta)
    {
        // ... filtros existentes ...

        // "Com alerta" exclui os dois casos que não são alerta: SemAlerta (avaliado e
        // dentro do esperado) e nulo (não avaliado, sem dado de mercado). Comparar
        // contra a lista de alertas de verdade, e não com "!= SemAlerta", porque
        // nulo não sobrevive a comparação de desigualdade em SQL.
        if (somenteComAlerta == true)
        {
            q = q.Where(i => i.MercadoAlerta == MercadoAlertas.Ruptura
                          || i.MercadoAlerta == MercadoAlertas.SemCausa
                          || i.MercadoAlerta == MercadoAlertas.NaoApurado);
        }

        return q;
    }
```

Propagar o parâmetro nas duas chamadas (linhas 427 e 497) e na assinatura dos dois handlers.

**Armadilha:** não usar `[FromQuery] bool somenteComAlerta = false`. `bool` com default literal funciona, mas para manter o padrão do arquivo e evitar a armadilha de `Guid`, usar `bool?`.

- [ ] **Step 4: Adicionar os campos ao DTO**

No record de projeção do item, adicionar os sete campos, mantendo `decimal?`/`int?`/`string?`/`DateOnly?` — nunca desanulando com `?? 0`.

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests --filter FiltrosDosItens`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add CosmosPro.ML.DemandForCast.ApiService/Comparacoes/ComparacoesEndpoints.cs \
        tests/CosmosPro.ML.DemandForCast.ApiService.IntegrationTests/
git commit -m "feat(comparacoes): expose the market columns and filter by alert

O filtro entra em AplicarFiltros, a cláusula única por onde página, contagem,
totais e Excel já passam.

'Com alerta' lista os três alertas de verdade em vez de negar SemAlerta:
nulo não sobrevive a comparação de desigualdade em SQL, e o item sem dado de
mercado sumiria ou entraria dependendo do tradutor.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 8: Tela e Excel

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Web/ComparacoesApiClient.cs:314` (espelho do DTO)
- Modify: `CosmosPro.ML.DemandForCast.Web/Components/Shared/TabelaItensComparacao.razor`
- Modify: `CosmosPro.ML.DemandForCast.Web/ComparacaoItensExcelExporter.cs:40` (`Build`)
- Test: `tests/CosmosPro.ML.DemandForCast.Web.Tests/ComparacaoItensExcelExporterTests.cs` (existente)
- Test: `tests/CosmosPro.ML.DemandForCast.Web.E2ETests/SessaoResultadoE2ETests.cs` (existente)

**Interfaces:**
- Consumes: DTO da Task 7.
- Produces: nada que outra tarefa deste plano consuma.

- [ ] **Step 1: Escrever os testes que falham**

Em `ComparacaoItensExcelExporterTests.cs`:

```csharp
    [Fact]
    public void A_planilha_leva_as_colunas_de_mercado_e_o_mes_na_capa()
    {
        var itens = new[]
        {
            ItemComMercado(alerta: MercadoAlertas.Ruptura, indice: 0.1234m,
                           mes: new DateOnly(2025, 6, 1)),
            ItemSemMercado(),
        };

        // ComparacaoItensExcelExporter.Build(...) -- copiar os argumentos de recorte do
        // teste vizinho neste mesmo arquivo (linha ~119).
        using var wb = AbrirPlanilha(ComparacaoItensExcelExporter.Build(itens, recorte));
        var aba = wb.Worksheet("Itens");

        aba.Row(1).Cells().Select(c => c.GetString())
           .Should().Contain(["Índice vs mercado", "Alerta de mercado", "Dias sem estoque"]);

        // Item sem dado de mercado sai em branco, não como 0 -- a planilha é ordenada
        // pelo comprador, e 0 o colocaria junto dos piores.
        aba.Cell(3, ColunaDoIndice).GetString().Should().BeEmpty();

        wb.Worksheet("Recorte").CellsUsed().Select(c => c.GetString())
          .Should().Contain(s => s.Contains("06/2025"));
    }
```

Em `SessaoResultadoE2ETests.cs`:

```csharp
    [Fact]
    public async Task O_comprador_filtra_a_tabela_por_alerta_de_mercado()
    {
        await AbrirSessaoConcluidaComAlertasAsync();

        var linhasAntes = await Page.Locator("[data-test='linha-item']").CountAsync();

        await Page.Locator("[data-test='filtro-somente-com-alerta']").CheckAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var linhasDepois = await Page.Locator("[data-test='linha-item']").CountAsync();
        linhasDepois.Should().BeLessThan(linhasAntes);
        linhasDepois.Should().BeGreaterThan(0);

        // O totalizador tem de acompanhar o filtro.
        var total = await Page.Locator("[data-test='total-itens']").InnerTextAsync();
        total.Should().Contain(linhasDepois.ToString());
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Web.Tests --filter ComparacaoItensExcelExporter`
Expected: FAIL — colunas não existem.

- [ ] **Step 3: Espelhar o DTO no cliente da Web**

Em `ComparacoesApiClient.cs`, adicionar os sete campos ao record espelho, com os mesmos nomes e tipos anuláveis do DTO da API.

- [ ] **Step 4: Adicionar as colunas na tabela**

Em `TabelaItensComparacao.razor`:

- Três colunas novas: **Índice vs mercado**, **Alerta de mercado**, **Dias sem estoque**. Unidades da rede e dos concorrentes vão no *title*/tooltip da célula do índice, para a tabela não ficar larga demais.
- Célula sem dado mostra `—`, nunca `0`. Tooltip explica por quê: "sem dado de mercado para este item".
- Alerta vira *pill* com texto de comprador, não o nome interno:
  - `Ruptura` → "Possível perda por ruptura"
  - `SemCausa` → "Abaixo do bairro, sem causa aparente"
  - `NaoApurado` → "Abaixo do bairro, estoque não apurado"
  - `SemAlerta` → "—"
  - `null` → "sem dado"
- Checkbox `data-test="filtro-somente-com-alerta"`, que passa `somenteComAlerta` à API junto com os filtros que já existem.
- Cabeçalho da tabela declara o mês da IQVIA usado, lido do primeiro item que tiver `MercadoMes`. Se nenhum tiver, a tela diz "sem dado de mercado nesta sessão" em vez de esconder as colunas.
- Manter `data-test` em cada linha (`linha-item`) e no totalizador (`total-itens`).

- [ ] **Step 5: Adicionar as colunas no Excel**

Em `ComparacaoItensExcelExporter.cs` (método `Build`), adicionar as três colunas na aba de itens e o mês da IQVIA na aba de capa. Célula nula fica **vazia**, não zero.

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Web.Tests --filter ComparacaoItensExcelExporter`
Expected: PASS.

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Web.E2ETests --filter SessaoResultado`
Expected: PASS.

- [ ] **Step 7: Rodar a suíte inteira**

Run: `dotnet test`
Expected: PASS. Nada verde-por-omissão: conferir que a contagem de testes subiu em relação ao `main`.

- [ ] **Step 8: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Web/ tests/CosmosPro.ML.DemandForCast.Web.Tests/ \
        tests/CosmosPro.ML.DemandForCast.Web.E2ETests/
git commit -m "feat(web): show the market alert beside the purchase decision

Três colunas na tabela de itens, filtro 'só com alerta', e as mesmas colunas
no Excel. O alerta aparece com texto de comprador, não com o nome interno.

Célula sem dado de mercado mostra travessão e sai vazia na planilha, nunca
zero: o comprador ordena a tabela por essas colunas, e zero colocaria o item
sem medição junto dos piores.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Lacuna conhecida entre a spec e este plano

A spec diz que B1 exibe "unidades, valor e participação". **Este plano não tem coluna de
R$**, e a omissão é consciente.

Motivo: para um EAN só, o valor em R$ da IQVIA é `unidades × preço de referência do mês`.
O preço é o mesmo para a rede e para os concorrentes (foi o que inviabilizou a regra B4),
então a coluna de R$ seria a coluna de unidades multiplicada por uma constante — nenhuma
informação nova, uma coluna a mais na tabela.

O que o comprador ganharia com dinheiro ali é **ordem de grandeza para priorizar**: uma
lacuna de 500 unidades num item de R$ 80 pesa mais que num item de R$ 2. Mas a métrica
certa para isso não é o valor de mercado — é a **lacuna em R$**, ou seja
`(unidades esperadas − unidades vendidas) × preço de referência`. Isso é uma métrica nova,
não é o que B1 pede, e merece decisão própria antes de virar coluna.

**Pendência:** decidir se entra a lacuna em R$ como coluna de priorização. Não bloqueia
nenhuma tarefa deste plano.

---

## Depois deste plano

- **Atualizar o README** (seção do roadmap, item F16) marcando o grupo B como feito e o grupo A como pendente. O README não pode mentir — ver CLAUDE.md §9.
- **Atualizar CLAUDE.md §4** com as invariantes novas: nulo ≠ zero nas colunas de mercado, o mês estritamente anterior, e o vocabulário de alerta.
- **Plano do grupo A**: bloqueado na versão nova do extrator (`catalogo_eans.csv`). Escrever quando o Claiton fechar o campo que falta.
- **Correção pendente na spec:** ela diz que o `CargaProcessor` carrega o catálogo "no Stage como os demais **e** faz o upsert em `RedeCatalogoEans`". Melhor não passar pelo Stage: uma tabela de Stage que ninguém lê é exatamente o defeito que a F16 corrigiu ao remover `MercadoIqvia`. O CSV deve ir **só** para `engine.RedeCatalogoEans`. Ajustar a spec junto com o plano do grupo A.
