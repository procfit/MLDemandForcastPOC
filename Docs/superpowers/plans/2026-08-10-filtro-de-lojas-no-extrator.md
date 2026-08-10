# Filtro de lojas no extrator — plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** o comprador escolhe, a cada extração, quais lojas da sugestão entram no ZIP — porque a rede autorizou exportar só algumas, e o dado das demais não pode sair.

**Architecture:** o recorte é uma função pura sobre os pares (loja, SKU) que `escopo_sugestao.sql` devolve. Filtrar lojas reduz os pares, e o conjunto de SKUs é recalculado a partir dos pares filtrados — assim `{{LOJAS}}` e `@skus` encolhem juntos e nenhum arquivo do ZIP cita loja descartada. A escolha acontece num diálogo separado, o manifesto declara o recorte, e o CLI ganha `--stores`.

**Tech Stack:** .NET 10 (`net10.0-windows`, WinForms, `WinExe`), FluentResults 4.0.0, Microsoft.Data.SqlClient 7.0.2, xUnit v3 + FluentAssertions.

**Spec:** [Docs/superpowers/specs/2026-08-10-filtro-de-lojas-no-extrator-design.md](../specs/2026-08-10-filtro-de-lojas-no-extrator-design.md)

## Global Constraints

- **Escopo de projetos:** `CosmosPro.ML.DemandForCast.Extractor` e seu projeto de teste. **Exceção única:** a Task 7 acrescenta dois campos ao record `ManifestoDaSugestao` do Worker, porque `ZipManifestTests` compara campo a campo os dois lados e falha se divergirem. Nenhum outro projeto pode ser tocado.
- **`Result` nunca representa cancelamento.** `OperationCanceledException` continua sendo o veículo; a tradução de falha chama `ThrowIfCancellationRequested` **antes** de classificar.
- **Retry só em leitura curta e idempotente**, nunca na extração.
- **A extração mantém `CommandTimeout = 0`.**
- **Nenhum `.sql` existente muda de SQL** exceto `sugestoes_compra_itens.sql`, que ganha uma cláusula, e o arquivo novo `lojas_da_sugestao.sql`. Os headers do `StageContract` e a ordem das colunas são contrato do import — não mudam.
- **Teto de 2.100 parâmetros do SQL Server.** `{{LOJAS}}` vira um parâmetro por id; uma sugestão de 100 lojas dá 100 parâmetros, o que cabe. SKUs continuam num parâmetro só (`@skus` + `STRING_SPLIT`), e é por isso que eles não podem virar parâmetros individuais.
- **Sem comentário narrando o que o código faz.** Comentário só onde o *porquê* não é óbvio.
- **Sem emoji.** Sem arquivo `.md` novo.
- Conventional Commits, subject em inglês, corpo em pt-BR.
- **Branch:** `feat/extrator-filtro-de-lojas` (já criada, com o spec e a correção da barra mergeada).
- **Comando de teste:** `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`. Hoje passam **233**.

---

### Task 1: Recorte de lojas e os dois erros novos

O coração. Função pura, sem banco, e é ela que garante que nenhum arquivo cite loja descartada.

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/RecorteDeLojas.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtratorErros.cs` (dois erros novos)
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractorCli.cs` (`CliExitCodeMap`)
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionModels.cs` (`ExtractionRequest.LojaIds`)
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/RecorteDeLojasTests.cs`

**Interfaces:**
- Consumes: `ExtratorErro` e `Result<T>` (já existem).
- Produces:
  - `sealed record ParLojaSku(int LojaId, string Sku)`
  - `sealed record EscopoRecortado(IReadOnlyList<int> LojaIds, IReadOnlySet<string> Skus, int LojasNaSugestao)`
  - `static class RecorteDeLojas` com
    `Result<EscopoRecortado> Aplicar(IReadOnlyCollection<ParLojaSku> paresDaSugestao, IReadOnlyList<int>? escolhidas)`
  - `sealed class LojasNaoSelecionadasErro : ExtratorErro`
  - `sealed class LojaForaDaSugestaoErro(IReadOnlyList<int> ids) : ExtratorErro`

**A regra que não é óbvia:** `null` significa "todas as lojas da sugestão" (é o default do CLI, que preserva o comportamento atual). Lista **vazia** é erro, não "todas" — na interface gráfica o operador não consegue chegar aqui sem escolher, e no CLI `--stores ""` é engano de digitação, não intenção.

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/RecorteDeLojasTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O recorte é o que sustenta a promessa de confidencialidade: se ele deixar passar
/// uma loja, ela sai no ZIP e chega a quem a rede não autorizou.
/// </summary>
public sealed class RecorteDeLojasTests
{
    private static readonly ParLojaSku[] Pares =
    [
        new(10, "111"), new(10, "222"),
        new(20, "222"), new(20, "333"),
        new(30, "444"),
    ];

    [Fact]
    public void Sem_escolha_traz_todas_as_lojas_da_sugestao()
    {
        var recorte = RecorteDeLojas.Aplicar(Pares, escolhidas: null).Value;

        recorte.LojaIds.Should().Equal(10, 20, 30);
        recorte.Skus.Should().BeEquivalentTo(["111", "222", "333", "444"]);
        recorte.LojasNaSugestao.Should().Be(3);
    }

    [Fact]
    public void Escolha_reduz_as_lojas()
    {
        RecorteDeLojas.Aplicar(Pares, [10, 20]).Value.LojaIds.Should().Equal(10, 20);
    }

    [Fact]
    public void Sku_que_so_existia_na_loja_descartada_sai_do_conjunto()
    {
        // O 444 só aparece na loja 30. Sem recalcular os SKUs a partir dos pares
        // filtrados, @skus continuaria pedindo o histórico dele -- dado de uma loja
        // que o comprador acabou de excluir.
        var recorte = RecorteDeLojas.Aplicar(Pares, [10, 20]).Value;

        recorte.Skus.Should().BeEquivalentTo(["111", "222", "333"]);
        recorte.Skus.Should().NotContain("444");
    }

    [Fact]
    public void Sku_compartilhado_sobrevive_se_ao_menos_uma_loja_ficou()
    {
        RecorteDeLojas.Aplicar(Pares, [20]).Value.Skus.Should().BeEquivalentTo(["222", "333"]);
    }

    [Fact]
    public void Total_de_lojas_da_sugestao_nao_muda_com_o_recorte()
    {
        // O manifesto declara "3 de 98": o denominador é a sugestão inteira, sempre.
        RecorteDeLojas.Aplicar(Pares, [10]).Value.LojasNaSugestao.Should().Be(3);
    }

    [Fact]
    public void Lojas_saem_ordenadas_para_o_comando_ser_estavel()
    {
        RecorteDeLojas.Aplicar(Pares, [30, 10]).Value.LojaIds.Should().Equal(10, 30);
    }

    [Fact]
    public void Id_repetido_na_escolha_nao_duplica_parametro()
    {
        RecorteDeLojas.Aplicar(Pares, [10, 10, 20]).Value.LojaIds.Should().Equal(10, 20);
    }

    [Fact]
    public void Lista_vazia_e_recusa_e_nao_significa_todas()
    {
        var resultado = RecorteDeLojas.Aplicar(Pares, []);

        resultado.IsFailed.Should().BeTrue();
        resultado.Errors.Single().Should().BeOfType<LojasNaoSelecionadasErro>();
    }

    [Fact]
    public void Loja_fora_da_sugestao_e_recusada_e_a_mensagem_nomeia_os_ids()
    {
        var resultado = RecorteDeLojas.Aplicar(Pares, [10, 99, 77]);

        resultado.IsFailed.Should().BeTrue();
        var erro = resultado.Errors.Single();
        erro.Should().BeOfType<LojaForaDaSugestaoErro>();
        erro.Message.Should().Contain("99").And.Contain("77");
        erro.Message.Should().NotContain("10");
    }

    [Fact]
    public void Sugestao_sem_par_nenhum_e_recusa()
    {
        RecorteDeLojas.Aplicar([], escolhidas: null).IsFailed.Should().BeTrue();
    }

    [Fact]
    public void Erros_do_recorte_nao_sao_transitorios()
    {
        new LojasNaoSelecionadasErro().Transitorio.Should().BeFalse();
        new LojaForaDaSugestaoErro([1]).Transitorio.Should().BeFalse();
    }
}
```

E acrescente a `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CliExitCodeMapTests.cs`:

```csharp
    [Fact]
    public void Recorte_de_lojas_invalido_e_erro_de_argumento()
    {
        // Entrada errada do operador, não falha de infraestrutura: o remédio é digitar
        // outro número, e um script que veja 6 tentaria de novo à toa.
        CliExitCodeMap.De(new LojasNaoSelecionadasErro()).Should().Be(CliExitCode.ArgumentosInvalidos);
        CliExitCodeMap.De(new LojaForaDaSugestaoErro([99])).Should().Be(CliExitCode.ArgumentosInvalidos);
    }
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter "FullyQualifiedName~RecorteDeLojasTests|FullyQualifiedName~CliExitCodeMapTests"`
Expected: erro de compilação — `RecorteDeLojas`, `ParLojaSku`, `LojasNaoSelecionadasErro` e `LojaForaDaSugestaoErro` não existem.

- [ ] **Step 3: Acrescentar os dois erros**

Em `ExtratorErros.cs`, junto dos demais erros concretos:

```csharp
internal sealed class LojasNaoSelecionadasErro() : ExtratorErro(
    "Nenhuma loja selecionada — não há o que extrair. Escolha ao menos uma loja da sugestão.");

internal sealed class LojaForaDaSugestaoErro(IReadOnlyList<int> ids) : ExtratorErro(
    $"Esta sugestão não tem a(s) loja(s) {string.Join(", ", ids)}. "
    + "Só é possível extrair lojas que a própria sugestão cita.");
```

- [ ] **Step 4: Implementar `RecorteDeLojas.cs`**

Crie `CosmosPro.ML.DemandForCast.Extractor/RecorteDeLojas.cs`:

```csharp
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>Par (loja, SKU) como <c>escopo_sugestao.sql</c> devolve.</summary>
internal sealed record ParLojaSku(int LojaId, string Sku);

/// <summary>
/// O escopo depois do recorte. <see cref="LojasNaSugestao"/> é o denominador que o
/// manifesto e a tela declaram ("3 de 98") e não muda com a escolha.
/// </summary>
internal sealed record EscopoRecortado(
    IReadOnlyList<int> LojaIds, IReadOnlySet<string> Skus, int LojasNaSugestao);

/// <summary>
/// Recorta o escopo da sugestão às lojas escolhidas.
/// <para>
/// Os SKUs são recalculados a partir dos pares que sobraram, e não filtrados do
/// conjunto original: um SKU que só existia numa loja descartada tem de sair, senão
/// <c>@skus</c> continuaria pedindo o histórico dele — dado de uma loja que o
/// comprador acabou de excluir.
/// </para>
/// </summary>
internal static class RecorteDeLojas
{
    public static Result<EscopoRecortado> Aplicar(
        IReadOnlyCollection<ParLojaSku> paresDaSugestao, IReadOnlyList<int>? escolhidas)
    {
        var daSugestao = paresDaSugestao.Select(p => p.LojaId).ToHashSet();
        if (daSugestao.Count == 0) return Result.Fail<EscopoRecortado>(new LojasNaoSelecionadasErro());

        // null = todas (default do modo linha de comando). Lista vazia é engano de
        // digitação, não intenção -- e tratá-la como "todas" exportaria o oposto do pedido.
        if (escolhidas is null)
        {
            return Result.Ok(Montar(paresDaSugestao, daSugestao, daSugestao.Count));
        }

        if (escolhidas.Count == 0) return Result.Fail<EscopoRecortado>(new LojasNaoSelecionadasErro());

        var forasteiras = escolhidas.Distinct().Where(id => !daSugestao.Contains(id)).Order().ToArray();
        if (forasteiras.Length > 0)
        {
            return Result.Fail<EscopoRecortado>(new LojaForaDaSugestaoErro(forasteiras));
        }

        return Result.Ok(Montar(paresDaSugestao, escolhidas.ToHashSet(), daSugestao.Count));
    }

    private static EscopoRecortado Montar(
        IReadOnlyCollection<ParLojaSku> pares, HashSet<int> manter, int lojasNaSugestao)
    {
        var sobreviventes = pares.Where(p => manter.Contains(p.LojaId)).ToArray();

        return new EscopoRecortado(
            [.. sobreviventes.Select(p => p.LojaId).Distinct().Order()],
            sobreviventes.Select(p => p.Sku).ToHashSet(StringComparer.Ordinal),
            lojasNaSugestao);
    }
}
```

- [ ] **Step 5: Acrescentar `LojaIds` ao request e mapear os exit codes**

Em `ExtractionModels.cs`, dentro de `ExtractionRequest`:

```csharp
    /// <summary>
    /// Recorte de lojas DENTRO da sugestão -- não escolha livre de lojas, que é o que a
    /// F14 removeu. <c>null</c> significa todas as lojas que a sugestão cita.
    /// </summary>
    public IReadOnlyList<int>? LojaIds { get; init; }
```

Em `ExtractorCli.cs`, no switch de `CliExitCodeMap.De`, antes do `_ =>`:

```csharp
        LojasNaoSelecionadasErro or LojaForaDaSugestaoErro => CliExitCode.ArgumentosInvalidos,
```

- [ ] **Step 6: Rodar os testes**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: tudo verde. 233 + 12 novos = 245.

- [ ] **Step 7: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/RecorteDeLojas.cs \
        CosmosPro.ML.DemandForCast.Extractor/ExtratorErros.cs \
        CosmosPro.ML.DemandForCast.Extractor/ExtractionModels.cs \
        CosmosPro.ML.DemandForCast.Extractor/ExtractorCli.cs \
        tests/CosmosPro.ML.DemandForCast.Extractor.Tests/RecorteDeLojasTests.cs \
        tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CliExitCodeMapTests.cs
git commit -m "feat(extractor): add the store cut that also narrows the SKU set

Recortar loja recalcula os SKUs a partir dos pares que sobraram: um SKU que so
existia numa loja descartada precisa sair, senao @skus continuaria pedindo o
historico dele -- dado de uma loja que o comprador acabou de excluir."
```

---

### Task 2: A extração honra o recorte

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/Queries/sugestoes_compra_itens.sql`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/QueryTypingTests.cs` (acrescenta um caso)

**Interfaces:**
- Consumes: `RecorteDeLojas.Aplicar`, `ParLojaSku`, `EscopoRecortado` (Task 1).
- Produces: `ExtractionResult` ganha `LojasExportadas` e `LojasNaSugestao`, que a Task 6 escreve no manifesto:
  `sealed record ExtractionResult(string ZipPath, long ZipBytes, IReadOnlyDictionary<string, long> RowsByFile, IReadOnlyList<string> Warnings, IReadOnlyList<int> LojasExportadas, int LojasNaSugestao)`

- [ ] **Step 1: Acrescentar a cláusula de loja à query dos itens**

Em `Queries/sugestoes_compra_itens.sql`, troque a linha do `WHERE` (hoje `WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}}`) por:

```sql
-- ESCOPO POR LOJA: o comprador pode exportar so parte das lojas da sugestao (a rede
-- autoriza algumas). Sem esta clausula o arquivo citaria loja que nao esta em
-- lojas.csv, e o SqlBulkCopy do Worker quebraria na FK composta (RedeId, LojaId) --
-- alem de vazar demanda, estoque de seguranca e PRECO_COMPRA de loja nao autorizada.
WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}}
  AND R.FILIAL IN ({{LOJAS}})
ORDER BY R.FILIAL, R.PRODUTO;
```

Não mexa em nenhuma outra linha do arquivo — as 17 colunas e a ordem são contrato do import.

- [ ] **Step 2: Fazer `LoadEscopoSugestao` devolver os pares**

Em `ExtractionService.cs`, troque o corpo de `LoadEscopoSugestao` para devolver `IReadOnlyList<ParLojaSku>` em vez da tupla, e mover a recusa de sugestão vazia para o recorte:

```csharp
    private static IReadOnlyList<ParLojaSku> LoadEscopoSugestao(
        SqlConnection connection, long sugestaoId, CancellationToken ct)
    {
        var pares = Step(new Etapa("escopo da sugestão", "escopo_sugestao.sql"), () =>
        {
            using var command = CreateSugestaoCommand(connection, SqlResources.Load("escopo_sugestao.sql"), sugestaoId);
            using var cancelRegistration = ct.Register(command.Cancel);
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();

            var lidos = new List<ParLojaSku>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                lidos.Add(new ParLojaSku(reader.GetInt32(0), reader.GetString(1)));
            }
            return (IReadOnlyList<ParLojaSku>)lidos;
        });

        if (pares.Count == 0)
        {
            throw new FalhaDeDominioException(new SugestaoSemItensErro(sugestaoId));
        }

        return pares;
    }
```

- [ ] **Step 3: Aplicar o recorte no `Run`**

Em `Run`, troque a linha que hoje faz `var (lojaIds, skusDaSugestao) = LoadEscopoSugestao(...)` por:

```csharp
                var pares = LoadEscopoSugestao(connection, request.SugestaoId, ct);
                var recorte = RecorteDeLojas.Aplicar(pares, request.LojaIds);
                if (recorte.IsFailed) throw new FalhaDeDominioException(recorte.ErroOuFallback());

                var lojaIds = recorte.Value.LojaIds;
                var skusDaSugestao = recorte.Value.Skus;
```

O resto do corpo continua igual: `lojaIds` e `skusDaSugestao` já são o que as chamadas seguintes consomem.

- [ ] **Step 4: Passar as lojas para a query dos itens**

A sobrecarga de `CopyQuery` escopada por sugestão precisa dos ids. Acrescente o parâmetro e crie o comando com os dois placeholders:

```csharp
    private static long CopyQuery(
        SqlConnection connection,
        string queryFile,
        string entryName,
        CsvZipWriter zip,
        long sugestaoId,
        IReadOnlyList<int> lojaIds,
        int fileIndex,
        int fileCount,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
    {
        using var command = CreateSugestaoCommand(connection, SqlResources.Load(queryFile), sugestaoId, lojaIds);
        return CopyQueryCore(entryName, queryFile, zip, command, fileIndex, fileCount, progress, ct, inspect: null);
    }
```

e estenda a fábrica, mantendo o comportamento atual quando não há lojas a substituir:

```csharp
    private static SqlCommand CreateSugestaoCommand(
        SqlConnection connection, string sql, long sugestaoId, IReadOnlyList<int>? lojaIds = null)
    {
        var texto = sql.Replace("{{SUGESTAO}}", "@sugestao");

        var placeholders = lojaIds is null
            ? []
            : lojaIds.Select((_, i) => "@loja" + i.ToString(CultureInfo.InvariantCulture)).ToArray();

        if (lojaIds is not null) texto = texto.Replace("{{LOJAS}}", string.Join(',', placeholders));

        var command = new SqlCommand(texto, connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.Add("@sugestao", SqlDbType.BigInt).Value = sugestaoId;

        for (var i = 0; lojaIds is not null && i < lojaIds.Count; i++)
        {
            command.Parameters.Add(placeholders[i], SqlDbType.Int).Value = lojaIds[i];
        }

        return command;
    }
```

Na chamada de `sugestoes_compra_itens.sql` dentro do `Run`, passe `lojaIds`:

```csharp
                rows[StageContract.SugestoesCompraItens] = CopyQuery(connection, "sugestoes_compra_itens.sql", StageContract.SugestoesCompraItens, zip, request.SugestaoId, lojaIds, 9, total, progress, ct);
```

`sugestoes_compra.sql` (o cabeçalho) e `sugestoes_compra_diagnostico.sql` continuam chamando `CreateSugestaoCommand` sem lojas — não têm `{{LOJAS}}` e não devem ganhar.

- [ ] **Step 5: Devolver o recorte no resultado**

Em `ExtractionModels.cs`, acrescente os dois campos a `ExtractionResult` (ver **Interfaces** acima). No fim de `Run`, monte com eles:

```csharp
        return Result.Ok(new ExtractionResult(
            zipPath, zipBytes, rows, warnings, recorte.Value.LojaIds, recorte.Value.LojasNaSugestao));
```

`recorte` precisa estar em escopo no fim do método — declare-o fora do `using` do ZIP, junto de `zipPath`.

- [ ] **Step 6: Acrescentar o caso ao teste de tipagem das queries**

`QueryTypingTests` já confere que toda coluna consumida de forma tipada tem `CONVERT`. Acrescente um caso que garanta que a query dos itens declara os dois placeholders:

```csharp
    [Fact]
    public void Itens_da_sugestao_declaram_escopo_de_sugestao_e_de_loja()
    {
        // Sem {{LOJAS}} o arquivo mais sensivel do ZIP sairia com todas as lojas da
        // sugestao, e a FK (RedeId, LojaId) quebraria o import.
        var sql = SqlResources.Load("sugestoes_compra_itens.sql");

        sql.Should().Contain("{{SUGESTAO}}");
        sql.Should().Contain("{{LOJAS}}");
    }
```

- [ ] **Step 7: Compilar e rodar a suíte**

Run: `dotnet build CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj -c Debug --nologo && dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`

Expected: build limpo, suíte verde. `MainForm.ExtrairAsync` e `ExtractorCli.Extrair` não passam `LojaIds` ainda — o default `null` mantém o comportamento atual, então nada quebra.

- [ ] **Step 8: Commit**

```bash
git add -A CosmosPro.ML.DemandForCast.Extractor tests/CosmosPro.ML.DemandForCast.Extractor.Tests
git commit -m "feat(extractor): honour the store cut in every file of the ZIP

sugestoes_compra_itens.sql ganha AND R.FILIAL IN ({{LOJAS}}): e o arquivo mais
sensivel do ZIP (demanda, estoque de seguranca, PRECO_COMPRA por loja) e sem a
clausula ele citaria loja ausente de lojas.csv, quebrando a FK no import."
```

---

### Task 3: Ler as lojas de uma sugestão, com nome e peso

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/Queries/lojas_da_sugestao.sql`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/CatalogoService.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionModels.cs` (`LojaDaSugestao`)
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CatalogoServiceTests.cs` (acrescenta casos)

**Interfaces:**
- Consumes: `CatalogoService.Consultar`, `LojaOption` (já existem).
- Produces:
  - `sealed record LojaDaSugestao(int LojaId, string Nome, int Itens)`
  - `CatalogoService.LojasDaSugestao(string connectionString, long sugestaoId, CancellationToken ct)` → `Result<IReadOnlyList<LojaDaSugestao>>`
  - `internal static IReadOnlyList<LojaDaSugestao> Casar(IReadOnlyList<(int LojaId, int Itens)> daSugestao, IReadOnlyList<LojaOption> cadastro)`

- [ ] **Step 1: Escrever os testes que falham**

Acrescente a `CatalogoServiceTests.cs`:

```csharp
    [Fact]
    public void Loja_da_sugestao_recebe_o_nome_do_cadastro()
    {
        var casadas = CatalogoService.Casar(
            [(10, 40), (20, 2)],
            [new LojaOption(10, "MATRIZ"), new LojaOption(20, "FILIAL CENTRO"), new LojaOption(99, "OUTRA")]);

        casadas.Should().HaveCount(2);
        casadas[0].Should().Be(new LojaDaSugestao(10, "MATRIZ", 40));
        casadas[1].Should().Be(new LojaDaSugestao(20, "FILIAL CENTRO", 2));
    }

    [Fact]
    public void Loja_sem_cadastro_ativo_continua_na_lista()
    {
        // lojas_disponiveis.sql filtra ATIVO = 'S'. Sumir da lista seria pior: a loja
        // esta na sugestao e o comprador precisa saber para decidir sobre ela.
        var casadas = CatalogoService.Casar([(86, 7)], [new LojaOption(10, "MATRIZ")]);

        casadas.Should().ContainSingle();
        casadas[0].LojaId.Should().Be(86);
        casadas[0].Itens.Should().Be(7);
        casadas[0].Nome.Should().Contain("sem cadastro");
    }

    [Fact]
    public void Lojas_saem_ordenadas_por_id()
    {
        var casadas = CatalogoService.Casar(
            [(30, 1), (10, 1), (20, 1)],
            [new LojaOption(10, "A"), new LojaOption(20, "B"), new LojaOption(30, "C")]);

        casadas.Select(l => l.LojaId).Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Leitura_de_lojas_da_sugestao_le_id_e_contagem_na_ordem_da_query()
    {
        var tabela = new DataTable();
        tabela.Columns.Add("LojaId", typeof(int));
        tabela.Columns.Add("Itens", typeof(int));
        tabela.Rows.Add(86, 7);
        using var reader = tabela.CreateDataReader();

        var lidas = CatalogoService.LerLojasDaSugestao(reader).ToArray();

        lidas.Should().ContainSingle();
        lidas[0].Should().Be((86, 7));
    }
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~CatalogoServiceTests`
Expected: erro de compilação — `CatalogoService.Casar`, `LerLojasDaSugestao` e `LojaDaSugestao` não existem.

- [ ] **Step 3: Criar a query**

Crie `CosmosPro.ML.DemandForCast.Extractor/Queries/lojas_da_sugestao.sql`:

```sql
-- Lojas que UMA sugestao cita, com quantos itens caem em cada uma, para o comprador
-- escolher quais exportar. Barata pelo mesmo motivo das contagens: o filtro por
-- SUGESTAO_COMPRA e uma busca pelo indice, e o agrupamento acontece sobre o punhado
-- de linhas daquela sugestao -- nao sobre a tabela inteira.
--
-- FILIAL e numeric(5,0) nesta instalacao e o driver entrega numeric como
-- System.Decimal; sem o CONVERT, GetInt32 estoura InvalidCastException.
SELECT
    LojaId = CONVERT(int, R.FILIAL),
    Itens  = CONVERT(int, COUNT(*))
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}}
GROUP BY R.FILIAL
ORDER BY R.FILIAL;
```

O `.csproj` já embarca `Queries\*.sql` como `EmbeddedResource` — nenhuma mudança lá.

- [ ] **Step 4: Implementar no `CatalogoService`**

Em `ExtractionModels.cs`:

```csharp
/// <summary>Uma loja citada pela sugestão, com o peso que ela tem nela.</summary>
internal sealed record LojaDaSugestao(int LojaId, string Nome, int Itens);
```

Em `CatalogoService.cs`:

```csharp
    public Result<IReadOnlyList<LojaDaSugestao>> LojasDaSugestao(
        string connectionString, long sugestaoId, CancellationToken ct)
    {
        var etapa = new Etapa("lojas da sugestão", "lojas_da_sugestao.sql");

        var daSugestao = ComRetentativa(() => Consultar(connectionString, etapa, TimeoutContagem, ct,
            comando => comando.Parameters.Add("@sugestao", SqlDbType.BigInt).Value = sugestaoId,
            reader => (IReadOnlyList<(int, int)>)[.. LerLojasDaSugestao(reader)],
            "{{SUGESTAO}}", "@sugestao"), ct);

        if (daSugestao.IsFailed) return Result.Fail<IReadOnlyList<LojaDaSugestao>>(daSugestao.Errors);

        var cadastro = Lojas(connectionString, ct);
        if (cadastro.IsFailed) return Result.Fail<IReadOnlyList<LojaDaSugestao>>(cadastro.Errors);

        return Result.Ok(Casar(daSugestao.Value, cadastro.Value));
    }

    internal static IEnumerable<(int LojaId, int Itens)> LerLojasDaSugestao(IDataReader reader)
    {
        while (reader.Read()) yield return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Junta as lojas da sugestão com os nomes do cadastro ativo. Loja que a sugestão
    /// cita e o cadastro não tem (desativada, por exemplo) **fica** na lista: sumir
    /// esconderia do comprador uma loja que vai ser exportada se ele não disser nada.
    /// </summary>
    internal static IReadOnlyList<LojaDaSugestao> Casar(
        IReadOnlyList<(int LojaId, int Itens)> daSugestao, IReadOnlyList<LojaOption> cadastro)
    {
        var nomes = cadastro.ToDictionary(l => l.LojaId, l => l.Nome);

        return [.. daSugestao
            .OrderBy(l => l.LojaId)
            .Select(l => new LojaDaSugestao(
                l.LojaId,
                nomes.TryGetValue(l.LojaId, out var nome) ? nome : "(sem cadastro)",
                l.Itens))];
    }
```

Se a assinatura de `Consultar` ou de `ComRetentativa` no arquivo divergir do que está escrito acima (parâmetros de token, ordem), **use a do arquivo** — o ponto é reusar o mesmo caminho de tradução de erro e retry das demais leituras, não introduzir um novo.

- [ ] **Step 5: Rodar a suíte**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: verde, com os 4 casos novos.

- [ ] **Step 6: Commit**

```bash
git add -A CosmosPro.ML.DemandForCast.Extractor tests/CosmosPro.ML.DemandForCast.Extractor.Tests
git commit -m "feat(extractor): list a suggestion's stores with names and item counts

Loja que a sugestao cita e o cadastro ativo nao tem continua na lista, marcada:
sumir esconderia do comprador uma loja que sai no ZIP se ele nao disser nada."
```

---

### Task 4: Diálogo de seleção de lojas

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/SelecaoDeLojasDialog.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/SelecaoDeLojasDialogTests.cs`

**Interfaces:**
- Consumes: `LojaDaSugestao` (Task 3).
- Produces:
  - `sealed class SelecaoDeLojasDialog : Form` com
    `static IReadOnlyList<int>? Escolher(IWin32Window dono, IReadOnlyList<LojaDaSugestao> lojas, IReadOnlyList<int> jaEscolhidas)` — devolve `null` quando o operador cancela.
  - `internal static IReadOnlyList<LojaDaSugestao> Filtrar(IReadOnlyList<LojaDaSugestao> lojas, string? termo)`

**Por que diálogo e não lista embutida:** uma sugestão pode ter 100 lojas, que não cabem no form de 660×760 sem espremer o grid. E `MainForm` não tem cobertura automatizada — não mexer no layout dele é redução de risco deliberada.

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/SelecaoDeLojasDialogTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Só o filtro é testado: o resto do diálogo mexe em Control e depende de bomba de
/// mensagens, que este projeto não tem. É também o motivo de o diálogo não carregar
/// regra nenhuma além de escolher ids.
/// </summary>
public sealed class SelecaoDeLojasDialogTests
{
    private static readonly LojaDaSugestao[] Lojas =
    [
        new(10, "MATRIZ", 40),
        new(86, "(sem cadastro)", 7),
        new(120, "FILIAL CENTRO", 3),
    ];

    [Fact]
    public void Filtro_vazio_devolve_todas()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "   ").Should().HaveCount(3);
        SelecaoDeLojasDialog.Filtrar(Lojas, null).Should().HaveCount(3);
    }

    [Fact]
    public void Filtro_acha_por_pedaco_do_nome_ignorando_caixa()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "centro").Should().ContainSingle()
            .Which.LojaId.Should().Be(120);
    }

    [Fact]
    public void Filtro_acha_por_id()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "86").Should().ContainSingle()
            .Which.LojaId.Should().Be(86);
    }

    [Fact]
    public void Filtro_por_id_nao_casa_pedaco_do_meio_de_outro_id()
    {
        // "12" nao pode trazer a 120 junto com uma eventual loja 12: o comprador digita
        // o codigo inteiro quando sabe qual quer.
        SelecaoDeLojasDialog.Filtrar(Lojas, "120").Select(l => l.LojaId).Should().Equal(120);
    }

    [Fact]
    public void Filtro_preserva_a_ordem()
    {
        SelecaoDeLojasDialog.Filtrar(Lojas, "a").Select(l => l.LojaId).Should().BeInAscendingOrder();
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~SelecaoDeLojasDialogTests`
Expected: erro de compilação — `SelecaoDeLojasDialog` não existe.

- [ ] **Step 3: Implementar o diálogo**

Crie `CosmosPro.ML.DemandForCast.Extractor/SelecaoDeLojasDialog.cs`:

```csharp
using System.Globalization;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Escolha das lojas que entram no ZIP. Diálogo separado porque uma sugestão pode ter
/// uma centena de lojas, que não cabem no form principal sem espremer o grid.
/// <para>
/// Nada vem marcado por padrão: marcar três é menos trabalho que desmarcar noventa e
/// cinco, e o padrão que erra tem de errar para o lado de não exportar.
/// </para>
/// </summary>
internal sealed class SelecaoDeLojasDialog : Form
{
    private readonly CheckedListBox _lista = new() { Width = 460, Height = 300, CheckOnClick = true };
    private readonly TextBox _filtro = new() { Width = 300, PlaceholderText = "filtrar por id ou nome" };
    private readonly Button _todas = new() { Text = "Marcar todas", Width = 110 };
    private readonly Button _nenhuma = new() { Text = "Desmarcar", Width = 90 };
    private readonly Button _ok = new() { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
    private readonly Button _cancelar = new() { Text = "Cancelar", Width = 90, DialogResult = DialogResult.Cancel };
    private readonly Label _resumo = new() { Width = 460, AutoSize = false };

    private readonly IReadOnlyList<LojaDaSugestao> _lojas;
    private readonly HashSet<int> _marcadas;

    private SelecaoDeLojasDialog(IReadOnlyList<LojaDaSugestao> lojas, IReadOnlyList<int> jaEscolhidas)
    {
        _lojas = lojas;
        _marcadas = [.. jaEscolhidas];

        Text = "Escolher lojas";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(500, 420);
        AcceptButton = _ok;
        CancelButton = _cancelar;

        _filtro.Location = new Point(12, 12);
        _todas.Location = new Point(320, 11);
        _nenhuma.Location = new Point(436, 11);
        _lista.Location = new Point(12, 44);
        _resumo.Location = new Point(12, 352);
        _ok.Location = new Point(296, 380);
        _cancelar.Location = new Point(396, 380);
        Controls.AddRange([_filtro, _todas, _nenhuma, _lista, _resumo, _ok, _cancelar]);

        _filtro.TextChanged += (_, _) => Popular();
        _lista.ItemCheck += (_, e) => AoMarcar(e);
        _todas.Click += (_, _) => MarcarVisiveis(true);
        _nenhuma.Click += (_, _) => MarcarVisiveis(false);

        Popular();
    }

    /// <summary>Devolve <c>null</c> quando o operador cancela — diferente de lista vazia.</summary>
    public static IReadOnlyList<int>? Escolher(
        IWin32Window dono, IReadOnlyList<LojaDaSugestao> lojas, IReadOnlyList<int> jaEscolhidas)
    {
        using var dialogo = new SelecaoDeLojasDialog(lojas, jaEscolhidas);
        return dialogo.ShowDialog(dono) == DialogResult.OK ? [.. dialogo._marcadas.Order()] : null;
    }

    internal static IReadOnlyList<LojaDaSugestao> Filtrar(IReadOnlyList<LojaDaSugestao> lojas, string? termo)
    {
        if (string.IsNullOrWhiteSpace(termo)) return lojas;

        var busca = termo.Trim();
        return [.. lojas.Where(l =>
            l.LojaId.ToString(CultureInfo.InvariantCulture) == busca
            || l.Nome.Contains(busca, StringComparison.OrdinalIgnoreCase))];
    }

    private void Popular()
    {
        _lista.BeginUpdate();
        _lista.Items.Clear();
        foreach (var loja in Filtrar(_lojas, _filtro.Text))
        {
            _lista.Items.Add(loja, _marcadas.Contains(loja.LojaId));
        }
        _lista.DisplayMember = nameof(LojaDaSugestao.Nome);
        _lista.EndUpdate();
        AtualizarResumo();
    }

    private void AoMarcar(ItemCheckEventArgs e)
    {
        if (_lista.Items[e.Index] is not LojaDaSugestao loja) return;

        if (e.NewValue == CheckState.Checked) _marcadas.Add(loja.LojaId);
        else _marcadas.Remove(loja.LojaId);

        // O ItemCheck roda ANTES de o item mudar de estado, então o resumo precisa ser
        // recalculado depois que a fila de mensagens aplicar a mudança.
        BeginInvoke(AtualizarResumo);
    }

    private void MarcarVisiveis(bool marcar)
    {
        for (var i = 0; i < _lista.Items.Count; i++) _lista.SetItemChecked(i, marcar);
    }

    private void AtualizarResumo()
    {
        _resumo.Text = $"{_marcadas.Count} de {_lojas.Count} loja(s) selecionada(s).";
        _ok.Enabled = _marcadas.Count > 0;
    }
}
```

`CheckedListBox.DisplayMember` mostra só o nome; para o operador ver id e peso, sobrescreva `ToString()` em `LojaDaSugestao` (Task 3) **ou** remova o `DisplayMember` e deixe o `ToString()` do record decidir. Escolha a segunda: acrescente ao record em `ExtractionModels.cs`

```csharp
    public override string ToString() => $"{LojaId} · {Nome} · {Itens:N0} itens";
```

e apague a linha `_lista.DisplayMember = ...`.

- [ ] **Step 4: Rodar a suíte**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: verde, com os 5 casos novos.

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/SelecaoDeLojasDialog.cs \
        CosmosPro.ML.DemandForCast.Extractor/ExtractionModels.cs \
        tests/CosmosPro.ML.DemandForCast.Extractor.Tests/SelecaoDeLojasDialogTests.cs
git commit -m "feat(extractor): add the store picker dialog

Nada vem marcado por padrao: marcar tres e menos trabalho que desmarcar noventa e
cinco, e o padrao que erra tem de errar para o lado de nao exportar."
```

---

### Task 5: Cablagem no `MainForm`

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/MainForm.cs`

**Interfaces:**
- Consumes: `CatalogoService.LojasDaSugestao` (Task 3), `SelecaoDeLojasDialog.Escolher` (Task 4), `ExtractionRequest.LojaIds` (Task 1).

**Cuidados que valem mais que o código:** `MainForm` não tem teste automatizado, e três defeitos recentes vieram dele. Não mexa no que já funciona — em particular, não toque em `ExecutarAsync`, em `AtualizarJanela` nem no gatilho da contagem.

- [ ] **Step 1: Campos e botão**

```csharp
    private readonly Button _escolherLojas = new() { Text = "Escolher lojas…", Width = 130, Enabled = false };

    private IReadOnlyList<LojaDaSugestao> _lojasDaSugestao = [];
    private IReadOnlyList<int> _lojasEscolhidas = [];
```

No `BuildLayout`, ponha `_escolherLojas` no grupo "Sugestão de compra", na linha de `_janelaInfo` (à direita dela); reduza a largura de `_janelaInfo` para não sobrepor. No construtor:

```csharp
        _escolherLojas.Click += async (_, _) => await EscolherLojasAsync();
```

- [ ] **Step 2: Carregar as lojas quando a seleção muda**

A contagem já dispara em `ContarSelecao`. Acrescente ali, **depois** de disparar a contagem:

```csharp
        _lojasDaSugestao = [];
        _lojasEscolhidas = [];
        _escolherLojas.Enabled = true;
```

e limpe nos dois ramos de `AtualizarJanela` que hoje desabilitam `_extrair`:

```csharp
            _escolherLojas.Enabled = false;
            _lojasDaSugestao = [];
            _lojasEscolhidas = [];
```

- [ ] **Step 3: Abrir o diálogo**

```csharp
    /// <summary>
    /// As lojas só são buscadas quando o comprador pede para escolher: é uma ida ao
    /// banco por sugestão, e a maioria das seleções não termina em extração.
    /// </summary>
    private async Task EscolherLojasAsync()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada) return;

        if (_lojasDaSugestao.Count == 0)
        {
            var connectionString = BuildConnectionString();
            var lidas = await Task.Run(() => _catalogoService.LojasDaSugestao(connectionString, selecionada.SugestaoId, CancellationToken.None));

            if (lidas.IsFailed)
            {
                var erro = lidas.ErroOuFallback();
                _log.Escrever($"ERRO ao listar as lojas da sugestão {selecionada.SugestaoId}: {erro.Message}");
                MessageBox.Show(this, erro.Message, "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _lojasDaSugestao = lidas.Value;
        }

        if (SelecaoDeLojasDialog.Escolher(this, _lojasDaSugestao, _lojasEscolhidas) is not { } escolhidas) return;

        _lojasEscolhidas = escolhidas;
        _log.Escrever($"Lojas escolhidas ({escolhidas.Count} de {_lojasDaSugestao.Count}): "
            + string.Join(", ", _lojasDaSugestao.Where(l => escolhidas.Contains(l.LojaId)).Select(l => $"{l.LojaId} {l.Nome}")));
        AtualizarResumoDaSelecao();
    }

    private void AtualizarResumoDaSelecao()
    {
        if (_lojasEscolhidas.Count == 0 || _lojasDaSugestao.Count == 0) return;

        _janelaInfo.Text = $"{_lojasEscolhidas.Count} de {_lojasDaSugestao.Count} loja(s) · {_janelaInfo.Text}";
    }
```

- [ ] **Step 4: Passar o recorte na extração**

Em `ExtrairAsync`, no `ExtractionRequest`, acrescente:

```csharp
            LojaIds = _lojasEscolhidas.Count > 0 ? _lojasEscolhidas : null,
```

E, no `aoConcluir` da extração, registre o que saiu:

```csharp
                _log.Escrever($"Lojas exportadas: {resultado.LojasExportadas.Count} de {resultado.LojasNaSugestao}.");
```

- [ ] **Step 5: Compilar e rodar a suíte**

Run: `dotnet build CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj -c Debug --nologo && dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: build limpo, suíte verde (esta task não acrescenta teste — o form não tem harness).

- [ ] **Step 6: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/MainForm.cs
git commit -m "feat(extractor): let the buyer pick which stores go into the ZIP"
```

---

### Task 6: `--stores` no modo linha de comando

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/CliParser.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractorCli.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CliParserTests.cs`

**Interfaces:**
- Consumes: `ExtractionRequest.LojaIds` (Task 1).
- Produces: `CliOptions.LojaIds` (`IReadOnlyList<int>?`, `null` = todas).

- [ ] **Step 1: Escrever os testes que falham**

Acrescente a `CliParserTests.cs`:

```csharp
    [Fact]
    public void Sem_stores_significa_todas_as_lojas_da_sugestao()
    {
        var options = CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x"]).Options!;

        options.LojaIds.Should().BeNull();
    }

    [Fact]
    public void Stores_le_a_lista_de_ids()
    {
        var options = CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", "12,45,78"]).Options!;

        options.LojaIds.Should().Equal(12, 45, 78);
    }

    [Fact]
    public void Stores_tolera_espaco_e_id_repetido()
    {
        var options = CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", " 12 , 45 , 12 "]).Options!;

        options.LojaIds.Should().Equal(12, 45);
    }

    [Fact]
    public void Stores_com_id_nao_numerico_e_argumento_invalido()
    {
        CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", "12,abc"]).Erro
            .Should().NotBeNull().And.Subject.As<string>().Should().Contain("abc");
    }

    [Fact]
    public void Stores_vazio_e_argumento_invalido()
    {
        // Lista vazia nao significa "todas": e engano de digitacao, e tratar como todas
        // exportaria o oposto do pedido.
        CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", "  "]).Erro
            .Should().NotBeNull();
    }
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~CliParserTests`
Expected: erro de compilação — `CliOptions.LojaIds` não existe.

- [ ] **Step 3: Implementar no parser**

Em `CliOptions`:

```csharp
    /// <summary>Recorte de lojas dentro da sugestão. <c>null</c> = todas as que ela cita.</summary>
    public IReadOnlyList<int>? LojaIds { get; init; }
```

Na variável local do `Parse` (junto de `meses`, `output`, etc.):

```csharp
        IReadOnlyList<int>? lojaIds = null;
```

No `switch`, ao lado de `--output`:

```csharp
                case "--stores":
                {
                    if (LerValor(args, ref i) is not { } valor) return Falha(ValorFaltando(arg));

                    var ids = new List<int>();
                    foreach (var pedaco in valor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!int.TryParse(pedaco, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                        {
                            return Falha($"'{arg}' espera ids de loja separados por vírgula; '{pedaco}' não é um id válido.");
                        }
                        if (!ids.Contains(id)) ids.Add(id);
                    }

                    if (ids.Count == 0) return Falha($"'{arg}' não recebeu nenhum id de loja.");
                    lojaIds = ids;
                    break;
                }
```

E no `CliOptions` montado no fim do `Parse`: `LojaIds = lojaIds,`.

No `HelpText`, junto das outras flags:

```
              --stores <ids>          Lojas a exportar, separadas por vírgula (ex.: 12,45,78).
                                      Só vale para --extract. Ausente: todas as lojas que a
                                      sugestão cita. A interface gráfica exige escolher; aqui
                                      o padrão é exportar tudo, para não quebrar automação.
```

- [ ] **Step 4: Passar adiante no `Extrair`**

Em `ExtractorCli.Extrair`, no `ExtractionRequest`:

```csharp
            LojaIds = options.LojaIds,
```

E, depois do sucesso, junto das linhas de contagem:

```csharp
        Console.WriteLine($"Lojas exportadas: {resultado.LojasExportadas.Count} de {resultado.LojasNaSugestao}.");
```

- [ ] **Step 5: Rodar a suíte**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: verde, com os 5 casos novos.

- [ ] **Step 6: Commit**

```bash
git add -A CosmosPro.ML.DemandForCast.Extractor tests/CosmosPro.ML.DemandForCast.Extractor.Tests
git commit -m "feat(extractor): add --stores to the command-line mode

Ausente significa todas as lojas da sugestao, o que preserva a automacao
existente. A assimetria com a interface grafica (que exige escolher) e
deliberada e esta no --help."
```

---

### Task 7: O manifesto declara o recorte

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ZipManifest.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs` (montagem do manifesto)
- Modify: `CosmosPro.ML.DemandForCast.Worker/Sessoes/ManifestoLeitor.cs` (record espelho)
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ZipManifestTests.cs`

**Interfaces:**
- Consumes: `ExtractionResult.LojasExportadas` e `LojasNaSugestao` (Task 2).
- Produces: `ZipManifest` com dois campos novos no fim: `IReadOnlyList<int> LojasExportadas, int LojasNaSugestao`.

**Por que o Worker entra:** `ZipManifestTests` compara campo a campo `ZipManifest` e `ManifestoDaSugestao` e falha se divergirem. O teste existe para impedir que os dois lados se afastem em silêncio. O Worker **não usa** os campos ainda — eles ficam desserializados esperando a fase que puser a restrição na tela.

- [ ] **Step 1: Escrever os testes que falham**

Acrescente a `ZipManifestTests.cs`:

```csharp
    [Fact]
    public void Manifesto_declara_as_lojas_exportadas_e_o_total_da_sugestao()
    {
        var m = new ZipManifest(21217, "MATTEL", new DateTime(2026, 3, 10, 10, 27, 0), 2,
            new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "0.15.0", 0, [10, 20], 98);

        var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

        volta.LojasExportadas.Should().Equal(10, 20);
        volta.LojasNaSugestao.Should().Be(98);
    }

    [Fact]
    public void Manifesto_sem_recorte_declara_todas_as_lojas_da_sugestao()
    {
        // Extracao sem --stores: as duas listas coincidem, e o consumidor consegue
        // distinguir "exportou tudo" de "exportou parte" sem heuristica.
        var m = new ZipManifest(1, null, new DateTime(2026, 1, 1), 1,
            new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 8), "0.15.0", 0, [7], 1);

        var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

        volta.LojasExportadas.Should().Equal(7);
        volta.LojasNaSugestao.Should().Be(1);
    }
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~ZipManifestTests`
Expected: erro de compilação — `ZipManifest` tem 8 parâmetros, não 10. O teste de contrato entre os dois records também vai falhar assim que o extrator ganhar os campos e o Worker não.

- [ ] **Step 3: Acrescentar os campos nos dois lados**

Em `ZipManifest.cs`, ao fim da lista de parâmetros do record:

```csharp
    int SkusSemCadastro,
    // Quais lojas da sugestão entraram no ZIP, e quantas ela tinha. A comparação
    // pontua a sugestão do ERP RESTRITA a estas lojas; sem os dois números, um
    // resultado de 3 lojas é indistinguível de um da rede inteira.
    IReadOnlyList<int> LojasExportadas,
    int LojasNaSugestao)
```

Em `CosmosPro.ML.DemandForCast.Worker/Sessoes/ManifestoLeitor.cs`, no record `ManifestoDaSugestao`, os mesmos dois campos, na mesma ordem e com os mesmos nomes:

```csharp
    int SkusSemCadastro,
    IReadOnlyList<int> LojasExportadas,
    int LojasNaSugestao);
```

- [ ] **Step 4: Preencher na extração**

Em `ExtractionService.Run`, na construção do `ZipManifest`, acrescente os dois argumentos ao fim:

```csharp
                    skusFabricados,
                    recorte.Value.LojaIds,
                    recorte.Value.LojasNaSugestao)));
```

- [ ] **Step 5: Rodar a suíte inteira, incluindo o Worker**

Run:
```
dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj
dotnet test tests/CosmosPro.ML.DemandForCast.Worker.Tests/CosmosPro.ML.DemandForCast.Worker.Tests.csproj
```
Expected: as duas verdes. Se `ManifestoLeitorTests` construir `ManifestoDaSugestao` posicionalmente, atualize as chamadas — **não** relaxe o teste de contrato.

- [ ] **Step 6: Commit**

```bash
git add -A CosmosPro.ML.DemandForCast.Extractor CosmosPro.ML.DemandForCast.Worker tests
git commit -m "feat(extractor): declare the store cut in the ZIP manifest

O Worker ganha os mesmos dois campos porque o teste de contrato compara os dois
records campo a campo -- ele existe para impedir que os lados divirjam em
silencio. O Worker ainda nao usa os campos; eles ficam desserializados esperando
a fase que puser a restricao na tela do comprador."
```

---

### Task 8: Verificação contra o PBS real e documentação

O único task que exige banco, e o único que fecha o requisito de confidencialidade de ponta a ponta.

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj` (`<Version>`)
- Modify: `README.md`

**Credenciais:** variáveis `NATUSFARMA_PBS_PROD_MSSQL_*` desta máquina, com `--env-prefix NATUSFARMA_PBS_PROD_` e **`--port 1435`** (não existe variável de porta; o default 1433 responde e falha no logon). **Nunca imprima a senha.** O ZIP tem dado comercial real — escreva **fora** do repositório, no diretório de scratch da sessão, e nunca faça commit dele.

- [ ] **Step 1: Achar uma sugestão que sirva**

Ela precisa de três coisas ao mesmo tempo: mais de uma loja, itens em `SUGESTOES_COMPRAS_RESULTADO`, e cobertura dentro dos 7 dias que a guarda de horizonte aceita. **Nenhum id está cravado** — a sugestão 22136, candidata óbvia por ter 98 lojas, tem cobertura 0 e é recusada.

```powershell
$exe = "CosmosPro.ML.DemandForCast.Extractor\bin\Release\net10.0-windows\CosmosPro.ML.DemandForCast.Extractor.exe"
Start-Process -FilePath $exe -ArgumentList "--list","--env-prefix","NATUSFARMA_PBS_PROD_","--port","1435","--months-back","2","--tsv" `
  -Wait -NoNewWindow -RedirectStandardOutput "lista.tsv" -RedirectStandardError "lista.err"
```

Tente `--extract` sem `--stores` nas candidatas até uma passar (exit 0). Exit 5 significa que a guarda recusou — vá para a próxima.

**Se nenhuma passar, a verificação de ponta a ponta não pode ser feita.** Registre isso no relatório como resultado, não como desculpa, e siga para o Step 4.

- [ ] **Step 2: Extrair duas vezes**

Com a sugestão escolhida (chame de `<ID>`) e duas das lojas dela (`<A>` e `<B>`):

```powershell
Start-Process -FilePath $exe -ArgumentList "--extract","--suggestion-id","<ID>","--output",".\inteiro","--env-prefix","NATUSFARMA_PBS_PROD_","--port","1435" -Wait -NoNewWindow
Start-Process -FilePath $exe -ArgumentList "--extract","--suggestion-id","<ID>","--output",".\recorte","--env-prefix","NATUSFARMA_PBS_PROD_","--port","1435","--stores","<A>,<B>" -Wait -NoNewWindow
```

- [ ] **Step 3: Conferir que o recorte foi respeitado**

Extraia os dois ZIPs e verifique, **nos sete CSVs** do recorte:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory((Get-ChildItem .\recorte\*.zip)[0].FullName, ".\recorte\csv")
foreach ($f in Get-ChildItem .\recorte\csv\*.csv) {
  $cab = (Get-Content $f.FullName -TotalCount 1) -split ','
  $i = [Array]::IndexOf($cab, 'LojaId')
  if ($i -lt 0) { "$($f.Name): sem LojaId"; continue }
  $lojas = Get-Content $f.FullName | Select-Object -Skip 1 | ForEach-Object { ($_ -split ',')[$i] } | Sort-Object -Unique
  "$($f.Name): $($lojas -join ', ')"
}
```

Barras de aprovação:
- **nenhum** CSV mostra loja fora de `<A>` e `<B>`;
- `lojas.csv` tem exatamente duas linhas de dado;
- `produtos.csv` do recorte é subconjunto do produtos do ZIP inteiro (restringir loja restringe SKU — §2 do spec);
- `manifesto.json` do recorte declara `LojasExportadas: [<A>,<B>]` e `LojasNaSugestao` igual ao total;
- `manifesto.json` do ZIP inteiro tem `LojasExportadas` com todas as lojas da sugestão.

**Qualquer loja fora do recorte em qualquer CSV é falha de confidencialidade e reprova a task.**

- [ ] **Step 4: Subir a versão e documentar**

`<Version>0.16.0</Version>` no `.csproj` do extrator — o `manifesto.json` publicado lê daqui e a página da sessão mostra a versão ao operador.

No `README.md`, na seção "Como o extrator se comporta quando algo demora ou falha", acrescente ao final:

```markdown
**Escolha de lojas.** A sugestão do PBS pode cobrir uma centena de lojas, e a rede pode
ter autorizado exportar só parte delas. O botão **Escolher lojas…** abre a lista das lojas
que a sugestão cita, com nome e quantos itens caem em cada uma; nada vem marcado, e
**Extrair** só habilita depois de haver ao menos uma. O recorte vale para todos os
arquivos do ZIP — inclusive `sugestoes_compra_itens.csv`, que leva demanda, estoque de
segurança e preço de compra por loja — e o `manifesto.json` declara quais lojas saíram e
quantas a sugestão tinha, para o resultado da comparação não ser confundido com um da rede
inteira. No modo linha de comando é `--stores 12,45,78`; ausente, exporta todas.
```

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj README.md
git commit -m "docs(extractor): document the store picker, bump to 0.16.0"
```

---

## Auto-revisão

**Cobertura do spec:**

| seção do spec | task |
|---|---|
| §2 `sugestoes_compra_itens` ganha filtro de loja | 2 |
| §2 efeito colateral: recorte de loja encolhe os SKUs | 1 (`RecorteDeLojas`), verificado na 8 |
| §4 query nova + nomes + loja sem cadastro | 3 |
| §4 diálogo, nada marcado, Extrair desabilitado | 4, 5 |
| §5 manifesto declara o recorte; Worker acompanha | 7 |
| §6 `--stores`, ausente = todas | 6 |
| §7 `ExtractionRequest.LojaIds`, recusa de id estranho | 1, 2 |
| §8 dois erros novos, mapeados para argumento inválido | 1 |
| §9 testes sem banco | 1, 3, 4, 6, 7 |
| §9 verificação com banco | 8 |

**Consistência de tipos:** `ParLojaSku` e `EscopoRecortado` nascem na Task 1 e são consumidos na 2; `LojaDaSugestao` nasce na 3 e é consumido na 4 e 5; `ExtractionResult` ganha os dois campos na Task 2 e são lidos na 5, 6 e 7; `CliOptions.LojaIds` nasce na 6 e alimenta `ExtractionRequest.LojaIds`, que nasce na 1.

**Riscos que a execução deve vigiar:**

- **Task 2 é a que pode quebrar o import em silêncio.** Se `{{LOJAS}}` não for substituído na query dos itens, o SQL sai com `IN ({{LOJAS}})` e falha em tempo de execução — nenhum teste sem banco pega isso. O caso acrescentado ao `QueryTypingTests` cobre a presença do placeholder; a substituição em si só a Task 8 prova.
- **Task 5 mexe no arquivo mais frágil do projeto.** `MainForm` não tem harness e concentrou três defeitos recentes. Não tocar em `ExecutarAsync`, `AtualizarJanela` nem no gatilho da contagem.
- **Task 7 atravessa projetos.** Se `ManifestoLeitorTests` construir o record posicionalmente, as chamadas mudam — atualizar as chamadas, nunca relaxar o teste de contrato.

**Fora do plano, de propósito** (§10 do spec): restringir `produtos.csv` além do que o recorte já faz; mostrar a restrição na tela da comparação; teto configurado de lojas autorizadas; estimativa de volume; e a limitação de horizonte, que é maior que este trabalho.
