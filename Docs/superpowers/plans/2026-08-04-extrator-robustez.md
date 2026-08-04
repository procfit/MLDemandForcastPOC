# Extrator — honestidade na UI e robustez — plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** fazer o extrator dizer a verdade sobre o que está executando, sobreviver a uma conexão que cai no meio da consulta, e trocar o catálogo de 20 minutos por um de 0,27 s.

**Architecture:** a camada de serviço passa a devolver `Result<T>` (FluentResults) com erros tipados que carregam etapa, query, número SQL e duração. `ExtractionService.cs` (720 linhas) é quebrado em `CatalogoService` (leituras curtas, com timeout e retry) e `ExtractionService` (a extração e o ZIP, sem retry). O form ganha um escopo `OperacaoUi` que trava inputs, liga Cancelar, roda cronômetro e barra, e um `ExtratorLog` que duplica o painel num arquivo diário ao lado do `.exe`.

**Tech Stack:** .NET 10 (`net10.0-windows`, WinForms, `WinExe`), FluentResults 4.0.0, Microsoft.Data.SqlClient 7.0.2, xUnit v3 + FluentAssertions.

**Spec:** [Docs/superpowers/specs/2026-08-04-extrator-robustez-design.md](../specs/2026-08-04-extrator-robustez-design.md)

## Global Constraints

- **Projeto único:** todo o trabalho é em `CosmosPro.ML.DemandForCast.Extractor` e no projeto de teste dele. Nenhum outro projeto da solução é tocado.
- **Contratos intocáveis:** `StageContract.Headers`, `ZipManifest`, `CsvZipWriter`, `StockCarryForward` e `ExtractionWindow` **não** mudam de comportamento. Os arquivos `.sql` só mudam se o passo disser explicitamente. Os 138 testes existentes precisam continuar passando — exceto os que o Task 4 remove nominalmente.
- **FluentResults 4.0.0** (MIT). Sem TFM `net10.0`; resolve os ativos `net9.0`, o que é esperado.
- **Idioma:** identificadores em inglês para conceitos técnicos já usados no projeto; os tipos novos de domínio seguem o padrão local em português (`ExtratorErro`, `CatalogoService`, `OperacaoUi`) — o repo já mistura assim (`SugestaoCatalogo`, `MesclarCatalogo`).
- **Sem comentário narrando o código.** Comentário só onde o *porquê* não é óbvio (CLAUDE.md §3).
- **Sem emoji** em código ou arquivos do repo.
- **`OperationCanceledException` nunca vira erro.** Cancelamento é desfecho: `Result` não é usado para representá-lo; a exceção sobe até a borda (form ou CLI), que já a distingue.
- **Nada de senha em log.** Toda mensagem que chega ao `ExtratorLog` passa por `Redigir`.
- **Branch:** `fix/extrator-robustez` (já criada, com os dois commits do spec).
- **Comando de teste:** `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`. Rode da raiz do repo.

---

### Task 1: Erros tipados e classificador de falha

O coração do plano. Todo o resto consome isto.

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj` (adiciona o pacote)
- Create: `CosmosPro.ML.DemandForCast.Extractor/ExtratorErros.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtratorErrosTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `sealed record Etapa(string Nome, string? QueryFile)` com `ToString()` = `"nome (arquivo.sql)"`.
  - `sealed record FalhaBruta(Type Tipo, string Mensagem, int? SqlNumber, bool ConexaoJaAberta, string DetalheCompleto)` + `static FalhaBruta De(Exception ex, bool conexaoJaAberta)`.
  - `abstract class ExtratorErro : FluentResults.Error` com `bool Transitorio { get; }` e as chaves de metadata como constantes públicas.
  - Concretos: `ConexaoErro`, `ConexaoPerdidaErro`, `LogonTriggerErro`, `TempoExcedidoErro`, `ConcorrenciaErro`, `EtapaErro`, `ContratoErro`, `SugestaoNaoEncontradaErro`, `SugestaoSemItensErro`, `JanelaInviavelErro`, `EscritaErro`, `InesperadoErro`.
  - `static class ClassificadorDeFalha` com `ExtratorErro Classificar(FalhaBruta falha, Etapa etapa, TimeSpan duracao)`.

**Por que `FalhaBruta` existe:** `SqlException` não tem construtor público, então um classificador que receba `Exception` é intestável. `FalhaBruta` é o recorte que a classificação de fato usa, e `FalhaBruta.De` é a única linha que toca `SqlException` — ela é exercitada de verdade contra o PBS no Task 9.

- [ ] **Step 1: Adicionar o pacote**

Em `CosmosPro.ML.DemandForCast.Extractor.csproj`, no `ItemGroup` que já tem o `Microsoft.Data.SqlClient`:

```xml
  <ItemGroup>
    <PackageReference Include="FluentResults" Version="4.0.0" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="7.0.2" />
  </ItemGroup>
```

- [ ] **Step 2: Verificar que restaura e compila**

Run: `dotnet build CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj -c Debug --nologo`
Expected: `Compilação com êxito` / `Build succeeded`. Se o restore falhar por TFM, **pare** — o plano assume que os ativos `net9.0` resolvem em `net10.0-windows`.

- [ ] **Step 3: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtratorErrosTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// A classificação de falha é o que decide o que o operador lê e o que o script
/// que chama o CLI recebe. Ela é testada sobre <see cref="FalhaBruta"/> e não
/// sobre <c>SqlException</c> porque essa exceção não tem construtor público.
/// </summary>
public sealed class ExtratorErrosTests
{
    private static readonly Etapa Qualquer = new("contagens do catálogo", "catalogo_sugestoes_contagens.sql");

    private static FalhaBruta Sql(int numero, bool conexaoJaAberta = true) =>
        new(typeof(InvalidOperationException), $"erro sql {numero}", numero, conexaoJaAberta, "detalhe completo");

    private static ExtratorErro Classificar(FalhaBruta falha) =>
        ClassificadorDeFalha.Classificar(falha, Qualquer, TimeSpan.FromSeconds(129));

    [Fact]
    public void Logon_trigger_tem_erro_proprio()
    {
        Classificar(Sql(17892, conexaoJaAberta: false)).Should().BeOfType<LogonTriggerErro>();
    }

    [Fact]
    public void Logon_trigger_cita_o_application_name_porque_a_mensagem_do_sql_server_nao_cita()
    {
        Classificar(Sql(17892, conexaoJaAberta: false)).Message.Should().Contain("ApplicationName");
    }

    [Fact]
    public void Timeout_de_comando_vira_tempo_excedido()
    {
        Classificar(Sql(-2)).Should().BeOfType<TempoExcedidoErro>();
    }

    [Fact]
    public void Queda_no_meio_da_consulta_nao_manda_conferir_servidor_e_porta()
    {
        // O erro de transporte visto na Natus em 2026-08-04: servidor e porta
        // estavam certos, a conexão caiu depois de 2min09. Mandar conferir os dois
        // joga o operador na direção errada.
        var erro = Classificar(Sql(-1, conexaoJaAberta: true));

        erro.Should().BeOfType<ConexaoPerdidaErro>();
        erro.Message.Should().NotContain("porta");
        erro.Transitorio.Should().BeTrue();
    }

    [Fact]
    public void Falha_ao_abrir_manda_conferir_servidor_porta_e_banco()
    {
        // 1433 vs 1435: algo responde na porta errada e o logon falha com uma
        // mensagem que não menciona porta nenhuma.
        var erro = Classificar(Sql(-1, conexaoJaAberta: false));

        erro.Should().BeOfType<ConexaoErro>();
        erro.Message.Should().Contain("porta");
    }

    [Theory]
    [InlineData(18456)]
    [InlineData(4060)]
    public void Credencial_e_banco_inacessivel_sao_falha_de_conexao_mesmo_com_conexao_aberta(int numero)
    {
        Classificar(Sql(numero, conexaoJaAberta: true)).Should().BeOfType<ConexaoErro>();
    }

    [Fact]
    public void Deadlock_e_transitorio()
    {
        var erro = Classificar(Sql(1205));

        erro.Should().BeOfType<ConcorrenciaErro>();
        erro.Transitorio.Should().BeTrue();
    }

    [Fact]
    public void Conversao_de_tipo_aponta_a_query_e_a_falta_do_convert()
    {
        // "Unable to cast object of type 'System.Decimal' to type 'System.Int32'"
        // não nomeia query nem coluna, e todo numérico do PBS é numeric(p,s).
        var falha = new FalhaBruta(typeof(InvalidCastException), "cast inválido", null, true, "detalhe");

        var erro = Classificar(falha);

        erro.Should().BeOfType<EtapaErro>();
        erro.Message.Should().Contain("catalogo_sugestoes_contagens.sql");
        erro.Message.Should().Contain("CONVERT");
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Falha_de_disco_vira_erro_de_escrita(Type tipo)
    {
        var falha = new FalhaBruta(tipo, "disco", null, false, "detalhe");

        Classificar(falha).Should().BeOfType<EscritaErro>();
    }

    [Fact]
    public void Falha_desconhecida_vira_inesperado_e_nao_engole_o_tipo()
    {
        var falha = new FalhaBruta(typeof(FormatException), "algo", null, false, "detalhe");

        var erro = Classificar(falha);

        erro.Should().BeOfType<InesperadoErro>();
        erro.Message.Should().Contain(nameof(FormatException));
    }

    [Fact]
    public void Todo_erro_classificado_carrega_etapa_query_e_duracao()
    {
        var erro = Classificar(Sql(-1));

        erro.Metadata[ExtratorErro.ChaveEtapa].Should().Be("contagens do catálogo");
        erro.Metadata[ExtratorErro.ChaveQuery].Should().Be("catalogo_sugestoes_contagens.sql");
        erro.Metadata[ExtratorErro.ChaveDuracao].Should().Be(129d);
        erro.Metadata[ExtratorErro.ChaveDetalhe].Should().Be("detalhe completo");
    }

    [Fact]
    public void Numero_sql_vai_para_a_metadata_quando_existe()
    {
        Classificar(Sql(1205)).Metadata[ExtratorErro.ChaveSqlNumber].Should().Be(1205);
    }

    [Fact]
    public void Falha_bruta_de_excecao_sem_sql_nao_inventa_numero()
    {
        var falha = FalhaBruta.De(new FormatException("x"), conexaoJaAberta: false);

        falha.SqlNumber.Should().BeNull();
        falha.Tipo.Should().Be(typeof(FormatException));
        falha.DetalheCompleto.Should().Contain(nameof(FormatException));
    }

    [Fact]
    public void Falha_bruta_desembrulha_a_causa_para_o_tipo_nao_ser_sempre_o_invólucro()
    {
        var falha = FalhaBruta.De(
            new InvalidOperationException("embrulho", new InvalidCastException("causa")),
            conexaoJaAberta: false);

        falha.Tipo.Should().Be(typeof(InvalidCastException));
    }

    [Fact]
    public void Erros_de_dominio_nao_sao_transitorios()
    {
        new SugestaoNaoEncontradaErro(4242).Transitorio.Should().BeFalse();
        new ContratoErro("vendas.csv", "coluna 3 é 'X', esperado 'Y'").Transitorio.Should().BeFalse();
        new JanelaInviavelErro("motivo").Transitorio.Should().BeFalse();
        new SugestaoSemItensErro(4242).Transitorio.Should().BeFalse();
    }

    [Fact]
    public void Sugestao_nao_encontrada_diz_o_id_e_como_conferir()
    {
        var erro = new SugestaoNaoEncontradaErro(4242);

        erro.Message.Should().Contain("4242");
        erro.Message.Should().Contain("--list");
    }
}
```

- [ ] **Step 4: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~ExtratorErrosTests`
Expected: erro de compilação — `Etapa`, `FalhaBruta`, `ClassificadorDeFalha` e os tipos de erro não existem.

- [ ] **Step 5: Implementar `ExtratorErros.cs`**

Crie `CosmosPro.ML.DemandForCast.Extractor/ExtratorErros.cs`:

```csharp
using System.Globalization;
using FluentResults;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Etapa nomeada de uma operação. O nome e o arquivo <c>.sql</c> são a informação
/// que faltava quando a extração morria com uma mensagem de conversão de tipo sem
/// dizer onde.
/// </summary>
internal sealed record Etapa(string Nome, string? QueryFile)
{
    public override string ToString() => QueryFile is null ? Nome : $"{Nome} ({QueryFile})";
}

/// <summary>
/// O recorte de uma exceção que a classificação de fato usa. Existe porque
/// <see cref="SqlException"/> não tem construtor público: sem este intermediário a
/// classificação só seria exercitável com um SQL Server vivo.
/// </summary>
internal sealed record FalhaBruta(
    Type Tipo,
    string Mensagem,
    int? SqlNumber,
    bool ConexaoJaAberta,
    string DetalheCompleto)
{
    public static FalhaBruta De(Exception ex, bool conexaoJaAberta)
    {
        // O tipo da causa, não o do invólucro: InvalidCastException aponta coluna
        // sem CONVERT na query, e é isso que precisa chegar ao operador.
        var raiz = ex.InnerException ?? ex;

        return new FalhaBruta(
            raiz.GetType(),
            raiz.Message,
            raiz is SqlException sql ? sql.Number : null,
            conexaoJaAberta,
            ex.ToString());
    }
}

internal abstract class ExtratorErro : Error
{
    public const string ChaveEtapa = "etapa";
    public const string ChaveQuery = "query";
    public const string ChaveSqlNumber = "sqlNumber";
    public const string ChaveDuracao = "duracaoSegundos";
    public const string ChaveDetalhe = "detalhe";

    protected ExtratorErro(string mensagem) : base(mensagem)
    {
    }

    /// <summary>
    /// Se repetir a mesma leitura tem chance de dar outro resultado. Só isto é
    /// retentado (ver <see cref="Retentativa"/>); repetir credencial errada ou
    /// contrato divergente só faz o operador esperar três vezes pela mesma resposta.
    /// </summary>
    public virtual bool Transitorio => false;
}

internal sealed class ConexaoErro(string detalheDoServidor) : ExtratorErro(
    $"Não foi possível conectar ao SQL Server. Confira servidor, porta e banco — "
    + $"uma porta errada costuma responder e falhar no logon, sem dizer que o problema é a porta. "
    + $"Detalhe: {detalheDoServidor}");

internal sealed class ConexaoPerdidaErro(Etapa etapa, TimeSpan duracao) : ExtratorErro(
    $"A conexão caiu durante a etapa '{etapa}', depois de "
    + $"{duracao.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)}s. "
    + "O servidor e as credenciais estavam certos: a rede desistiu no meio da consulta.")
{
    public override bool Transitorio => true;
}

internal sealed class LogonTriggerErro() : ExtratorErro(
    "O servidor tem um logon trigger que recusou a conexão — normalmente por causa do "
    + "nome da aplicação. Ajuste 'ApplicationName' em extrator.config.json (vazio usa o "
    + "padrão do provider) ou use --app-name no modo linha de comando.");

internal sealed class TempoExcedidoErro(Etapa etapa, TimeSpan duracao) : ExtratorErro(
    $"A etapa '{etapa}' passou do tempo limite "
    + $"({duracao.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)}s). "
    + "Os limites ficam em extrator.config.json.");

internal sealed class ConcorrenciaErro(Etapa etapa) : ExtratorErro(
    $"A etapa '{etapa}' foi escolhida como vítima de deadlock pelo SQL Server.")
{
    public override bool Transitorio => true;
}

internal sealed class EtapaErro(Etapa etapa, string causa) : ExtratorErro(
    $"Falha na etapa '{etapa}': {causa}");

internal sealed class ContratoErro(string arquivo, string divergencia) : ExtratorErro(
    $"A query de '{arquivo}' não bate com o contrato do Stage: {divergencia}. "
    + "O ZIP não foi gravado — subir com coluna trocada embaralharia o dado no import.");

internal sealed class SugestaoNaoEncontradaErro(long sugestaoId) : ExtratorErro(
    $"Sugestão {sugestaoId} não existe no PBS, ou não tem método de cálculo declarado. "
    + "Confira o id com --list.");

internal sealed class SugestaoSemItensErro(long sugestaoId) : ExtratorErro(
    $"Sugestão {sugestaoId} não tem itens no PBS — nada para extrair.");

internal sealed class JanelaInviavelErro(string motivo) : ExtratorErro(motivo);

internal sealed class EscritaErro(string caminho, string causa) : ExtratorErro(
    $"Não foi possível escrever em '{caminho}': {causa}. "
    + "Confira espaço em disco, permissão na pasta e antivírus travando o arquivo.");

internal sealed class InesperadoErro(Type tipo, string mensagem) : ExtratorErro(
    $"Falha inesperada ({tipo.Name}): {mensagem}. O detalhe completo está no arquivo de log.");

internal static class ClassificadorDeFalha
{
    /// <summary>Recusa a conexão por logon trigger. A mensagem crua do SQL Server não diz isso.</summary>
    private const int LogonTriggerRecusou = 17892;
    private const int TimeoutDeComando = -2;
    private const int VitimaDeDeadlock = 1205;
    private const int LogonFalhou = 18456;
    private const int BancoInacessivel = 4060;

    public static ExtratorErro Classificar(FalhaBruta falha, Etapa etapa, TimeSpan duracao)
    {
        var erro = Escolher(falha, etapa, duracao);

        erro.Metadata[ExtratorErro.ChaveEtapa] = etapa.Nome;
        erro.Metadata[ExtratorErro.ChaveDuracao] = Math.Round(duracao.TotalSeconds, 3);
        erro.Metadata[ExtratorErro.ChaveDetalhe] = falha.DetalheCompleto;
        if (etapa.QueryFile is { } query) erro.Metadata[ExtratorErro.ChaveQuery] = query;
        if (falha.SqlNumber is { } numero) erro.Metadata[ExtratorErro.ChaveSqlNumber] = numero;

        return erro;
    }

    private static ExtratorErro Escolher(FalhaBruta falha, Etapa etapa, TimeSpan duracao) =>
        falha.SqlNumber switch
        {
            LogonTriggerRecusou => new LogonTriggerErro(),
            TimeoutDeComando => new TempoExcedidoErro(etapa, duracao),
            VitimaDeDeadlock => new ConcorrenciaErro(etapa),
            LogonFalhou or BancoInacessivel => new ConexaoErro(falha.Mensagem),

            // Qualquer outro erro de SQL sobre uma conexão que já estava aberta é
            // queda no meio do caminho, não configuração errada. É o discriminador
            // que evita depender de uma lista de números de rede que nunca fecha.
            not null when falha.ConexaoJaAberta => new ConexaoPerdidaErro(etapa, duracao),
            not null => new ConexaoErro(falha.Mensagem),

            null => SemNumeroSql(falha, etapa),
        };

    private static ExtratorErro SemNumeroSql(FalhaBruta falha, Etapa etapa)
    {
        if (falha.Tipo == typeof(InvalidCastException))
        {
            return new EtapaErro(etapa,
                $"{falha.Mensagem} — provavelmente uma coluna sem CONVERT na query. "
                + "Todo numérico do PBS é numeric(p,s) e chega como System.Decimal.");
        }

        if (falha.Tipo == typeof(IOException)
            || falha.Tipo.IsSubclassOf(typeof(IOException))
            || falha.Tipo == typeof(UnauthorizedAccessException))
        {
            return new EscritaErro(etapa.Nome, falha.Mensagem);
        }

        return new InesperadoErro(falha.Tipo, falha.Mensagem);
    }
}
```

- [ ] **Step 6: Rodar os testes**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~ExtratorErrosTests`
Expected: todos passam (20 casos, contando os `Theory`).

- [ ] **Step 7: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj \
        CosmosPro.ML.DemandForCast.Extractor/ExtratorErros.cs \
        tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtratorErrosTests.cs
git commit -m "feat(extractor): add typed failures that name the step, query and SQL number

O discriminador de conexao-ja-aberta separa queda no meio da consulta de falha
ao abrir, que sao conselhos opostos para o operador."
```

---

### Task 2: Retentativa de leitura curta

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/Retentativa.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/RetentativaTests.cs`

**Interfaces:**
- Consumes: `ExtratorErro.Transitorio` (Task 1).
- Produces: `static class Retentativa` com
  `Result<T> Executar<T>(Func<Result<T>> tentativa, int tentativas, Action<string> log, Action<TimeSpan> dormir)`
  e `static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromSeconds(2)`.

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/RetentativaTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Retry silencioso é a mesma desonestidade que este trabalho existe para tirar,
/// com outro nome: o operador precisa ver que houve segunda tentativa.
/// </summary>
public sealed class RetentativaTests
{
    private static readonly Etapa Qualquer = new("catálogo", "catalogo_sugestoes.sql");

    private static ExtratorErro Transitorio() => new ConexaoPerdidaErro(Qualquer, TimeSpan.FromSeconds(3));

    private static ExtratorErro Definitivo() => new SugestaoNaoEncontradaErro(1);

    [Fact]
    public void Sucesso_de_primeira_nao_dorme_nem_registra_tentativa()
    {
        var dormidas = new List<TimeSpan>();
        var log = new List<string>();

        var resultado = Retentativa.Executar(() => Result.Ok(7), 3, log.Add, dormidas.Add);

        resultado.Value.Should().Be(7);
        dormidas.Should().BeEmpty();
        log.Should().BeEmpty();
    }

    [Fact]
    public void Falha_transitoria_seguida_de_sucesso_devolve_o_sucesso()
    {
        var chamadas = 0;
        var log = new List<string>();

        var resultado = Retentativa.Executar(
            () => ++chamadas == 1 ? Result.Fail<int>(Transitorio()) : Result.Ok(42),
            3, log.Add, _ => { });

        resultado.IsSuccess.Should().BeTrue();
        resultado.Value.Should().Be(42);
        chamadas.Should().Be(2);
    }

    [Fact]
    public void A_retentativa_aparece_no_log()
    {
        var log = new List<string>();

        Retentativa.Executar(() => Result.Fail<int>(Transitorio()), 3, log.Add, _ => { });

        log.Should().HaveCount(2);
        log[0].Should().Contain("tentativa 2 de 3");
        log[1].Should().Contain("tentativa 3 de 3");
    }

    [Fact]
    public void Falha_transitoria_persistente_devolve_o_ultimo_erro_depois_das_tentativas()
    {
        var chamadas = 0;

        var resultado = Retentativa.Executar(
            () => { chamadas++; return Result.Fail<int>(Transitorio()); }, 3, _ => { }, _ => { });

        chamadas.Should().Be(3);
        resultado.IsFailed.Should().BeTrue();
        resultado.Errors.Single().Should().BeOfType<ConexaoPerdidaErro>();
    }

    [Fact]
    public void Falha_definitiva_nao_e_retentada()
    {
        var chamadas = 0;

        var resultado = Retentativa.Executar(
            () => { chamadas++; return Result.Fail<int>(Definitivo()); }, 3, _ => { }, _ => { });

        chamadas.Should().Be(1);
        resultado.Errors.Single().Should().BeOfType<SugestaoNaoEncontradaErro>();
    }

    [Fact]
    public void Espera_entre_tentativas_e_a_declarada()
    {
        var dormidas = new List<TimeSpan>();

        Retentativa.Executar(() => Result.Fail<int>(Transitorio()), 3, _ => { }, dormidas.Add);

        dormidas.Should().Equal(Retentativa.EsperaEntreTentativas, Retentativa.EsperaEntreTentativas);
    }

    [Fact]
    public void Uma_tentativa_so_nunca_dorme()
    {
        var dormidas = new List<TimeSpan>();

        Retentativa.Executar(() => Result.Fail<int>(Transitorio()), 1, _ => { }, dormidas.Add);

        dormidas.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~RetentativaTests`
Expected: erro de compilação — `Retentativa` não existe.

- [ ] **Step 3: Implementar**

Crie `CosmosPro.ML.DemandForCast.Extractor/Retentativa.cs`:

```csharp
using System.Globalization;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Repete leitura curta e idempotente quando a falha é transitória. O <c>dormir</c>
/// é parâmetro para o teste não precisar esperar de verdade.
/// </summary>
internal static class Retentativa
{
    public const int TentativasPadrao = 3;
    public static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromSeconds(2);

    public static Result<T> Executar<T>(
        Func<Result<T>> tentativa, int tentativas, Action<string> log, Action<TimeSpan> dormir)
    {
        var resultado = tentativa();

        for (var numero = 2; numero <= tentativas; numero++)
        {
            if (resultado.IsSuccess) return resultado;
            if (resultado.Errors.FirstOrDefault() is not ExtratorErro { Transitorio: true } erro) return resultado;

            log($"{erro.Message} Retentando (tentativa {numero.ToString(CultureInfo.InvariantCulture)} "
                + $"de {tentativas.ToString(CultureInfo.InvariantCulture)}).");
            dormir(EsperaEntreTentativas);
            resultado = tentativa();
        }

        return resultado;
    }
}
```

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~RetentativaTests`
Expected: 7 passam.

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/Retentativa.cs \
        tests/CosmosPro.ML.DemandForCast.Extractor.Tests/RetentativaTests.cs
git commit -m "feat(extractor): retry transient reads and say so in the log"
```

---

### Task 3: Log em arquivo, com senha redigida

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/ExtratorLog.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtratorLogTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `sealed class ExtratorLog` com
  - ctor `ExtratorLog(string pasta, Action<string>? tela = null, Func<DateTime>? agora = null)`
  - `void Escrever(string mensagem)` — tela + arquivo
  - `void EscreverSoNoArquivo(string mensagem)` — usado para `ChaveDetalhe`
  - `static string Redigir(string texto)`
  - `static string NomeDoArquivo(DateTime dia)`
  - `string CaminhoDeHoje { get; }`

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtratorLogTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O operador roda o extrator num terminal do cliente e a única coisa que ele pode
/// reportar é o que sobrou escrito. E o que sobra escrito não pode conter a senha
/// do ERP de produção.
/// </summary>
public sealed class ExtratorLogTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "extrator-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    private ExtratorLog Log(Action<string>? tela = null, DateTime? dia = null) =>
        new(_pasta, tela, () => dia ?? new DateTime(2026, 8, 4, 11, 6, 36));

    [Fact]
    public void Redige_a_senha_da_connection_string()
    {
        var texto = ExtratorLog.Redigir(
            "Data Source=natusfarma.procfit.com.br,1435;Initial Catalog=PBS;User ID=dev;Password=SenhaSuperSecreta;Encrypt=True");

        texto.Should().NotContain("SenhaSuperSecreta");
        texto.Should().Contain("natusfarma.procfit.com.br,1435");
        texto.Should().Contain("User ID=dev");
    }

    [Theory]
    [InlineData("Password=x;")]
    [InlineData("password=x;")]
    [InlineData("PWD=x;")]
    [InlineData("Pwd = x ;")]
    public void Redige_todas_as_grafias_de_senha(string trecho)
    {
        ExtratorLog.Redigir("a;" + trecho + "b=1").Should().NotContain("x");
    }

    [Fact]
    public void Texto_sem_senha_passa_intacto()
    {
        ExtratorLog.Redigir("19.581 sugestões em 0,3s").Should().Be("19.581 sugestões em 0,3s");
    }

    [Fact]
    public void Escreve_na_tela_e_no_arquivo()
    {
        var tela = new List<string>();
        var log = Log(tela.Add);

        log.Escrever("Carregando sugestões...");

        tela.Should().ContainSingle().Which.Should().Contain("Carregando sugestões...");
        File.ReadAllText(log.CaminhoDeHoje).Should().Contain("Carregando sugestões...");
    }

    [Fact]
    public void A_linha_leva_a_hora()
    {
        var log = Log();

        log.Escrever("x");

        File.ReadAllText(log.CaminhoDeHoje).Should().Contain("11:06:36");
    }

    [Fact]
    public void Detalhe_completo_vai_so_para_o_arquivo()
    {
        var tela = new List<string>();
        var log = Log(tela.Add);

        log.EscreverSoNoArquivo("System.InvalidCastException: pilha inteira aqui");

        tela.Should().BeEmpty();
        File.ReadAllText(log.CaminhoDeHoje).Should().Contain("pilha inteira aqui");
    }

    [Fact]
    public void Linhas_sucessivas_sao_acrescentadas_e_nao_sobrescritas()
    {
        var log = Log();

        log.Escrever("primeira");
        log.Escrever("segunda");

        var conteudo = File.ReadAllText(log.CaminhoDeHoje);
        conteudo.Should().Contain("primeira");
        conteudo.Should().Contain("segunda");
    }

    [Fact]
    public void Um_arquivo_por_dia()
    {
        ExtratorLog.NomeDoArquivo(new DateTime(2026, 8, 4)).Should().Be("extrator-log-2026-08-04.txt");
        ExtratorLog.NomeDoArquivo(new DateTime(2026, 12, 31)).Should().Be("extrator-log-2026-12-31.txt");
    }

    [Fact]
    public void A_senha_e_redigida_antes_de_chegar_a_qualquer_um_dos_dois_destinos()
    {
        var tela = new List<string>();
        var log = Log(tela.Add);

        log.Escrever("conectando com Password=Secreta123;");

        tela.Single().Should().NotContain("Secreta123");
        File.ReadAllText(log.CaminhoDeHoje).Should().NotContain("Secreta123");
    }

    [Fact]
    public void Pasta_inacessivel_nao_derruba_a_operacao()
    {
        // Perder o log é ruim; perder a extração porque o log falhou é pior.
        var tela = new List<string>();
        var log = new ExtratorLog(Path.Combine("Z:", "nao", "existe"), tela.Add, () => DateTime.Now);

        var acao = () => log.Escrever("x");

        acao.Should().NotThrow();
        tela.Should().ContainSingle();
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~ExtratorLogTests`
Expected: erro de compilação — `ExtratorLog` não existe.

- [ ] **Step 3: Implementar**

Crie `CosmosPro.ML.DemandForCast.Extractor/ExtratorLog.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Sink duplo: o painel do form e um arquivo por dia ao lado do executável. O
/// arquivo existe porque o operador roda isto num terminal do cliente, e sem ele
/// toda falha chega até aqui como "não sei, deu erro".
/// </summary>
internal sealed partial class ExtratorLog(string pasta, Action<string>? tela = null, Func<DateTime>? agora = null)
{
    private readonly Func<DateTime> _agora = agora ?? (() => DateTime.Now);

    public string CaminhoDeHoje => Path.Combine(pasta, NomeDoArquivo(_agora()));

    public static string NomeDoArquivo(DateTime dia) =>
        $"extrator-log-{dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.txt";

    public void Escrever(string mensagem)
    {
        var linha = Formatar(mensagem);
        tela?.Invoke(linha);
        Gravar(linha);
    }

    public void EscreverSoNoArquivo(string mensagem) => Gravar(Formatar(mensagem));

    private string Formatar(string mensagem) =>
        $"{_agora().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {Redigir(mensagem)}";

    private void Gravar(string linha)
    {
        try
        {
            Directory.CreateDirectory(pasta);
            File.AppendAllText(CaminhoDeHoje, linha + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Pasta somente leitura ou caminho inválido: perder o log não justifica
            // derrubar a operação. Mesma política de AppConfig.Save().
        }
    }

    public static string Redigir(string texto) => SenhaNaConnectionString().Replace(texto, "Password=***");

    [GeneratedRegex(@"\b(password|pwd)\s*=\s*[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex SenhaNaConnectionString();
}
```

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~ExtratorLogTests`
Expected: 13 passam. Se `Pasta_inacessivel_nao_derruba_a_operacao` falhar com `DirectoryNotFoundException`, acrescente esse tipo ao filtro do `catch`.

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/ExtratorLog.cs \
        tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtratorLogTests.cs
git commit -m "feat(extractor): mirror the on-screen log to a daily file, password redacted"
```

---

### Task 4: `CatalogoService` — leituras em `Result`, contagem por sugestão

O maior recorte. Tira as leituras de `ExtractionService`, mata o lote de contagens, e deixa a leitura de linhas testável sem banco.

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/CatalogoService.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs` (remove o que migrou)
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionModels.cs` (remove `SugestaoCatalogo`)
- Modify: `CosmosPro.ML.DemandForCast.Extractor/AppConfig.cs` (timeouts, meses, resiliência)
- Delete: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CatalogoSugestoesTests.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CatalogoServiceTests.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ConnectionStringFactoryTests.cs`

**Interfaces:**
- Consumes: `Etapa`, `FalhaBruta`, `ClassificadorDeFalha`, os erros (Task 1); `Retentativa` (Task 2); `ExtratorLog` (Task 3).
- Produces:
  - `sealed class CatalogoService(AppConfig config, ExtratorLog log)` com
    - `Result<IReadOnlyList<SugestaoCatalogoCabecalho>> Carregar(string connectionString, DateOnly dataInicio, CancellationToken ct)`
    - `Result<SugestaoContagem> Contar(string connectionString, long sugestaoId, CancellationToken ct)`
    - `Result<SugestaoCatalogoCabecalho> PorId(string connectionString, long sugestaoId, CancellationToken ct)` — falha com `SugestaoNaoEncontradaErro`
    - `Result<IReadOnlyList<LojaOption>> Lojas(string connectionString, CancellationToken ct)`
    - `internal static SugestaoCatalogoCabecalho LerCabecalho(IDataRecord registro)`
    - `internal static SugestaoContagem LerContagem(long sugestaoId, IDataReader reader)`
    - `internal static IReadOnlyList<SugestaoCatalogoCabecalho> Filtrar(IReadOnlyList<SugestaoCatalogoCabecalho> catalogo, string? filtro)`
  - `AppConfig` ganha `TimeoutConexaoSegundos = 15`, `TimeoutConsultaSegundos = 30`, `TimeoutContagemSegundos = 15`, `MesesRetroativos = 12`.

**Removido de `ExtractionService`:** `LoadLojas`, `LoadCatalogoSugestoes`, `LoadSugestaoPorId`, `LerCabecalho`, `LoadCabecalhosDoCatalogo`, `LoadContagensDoCatalogo`, `CreateContagensCommand`, `LotesDeSugestoes`, `MesclarCatalogo`, `SugestoesPorLote`, `MaxParametrosPorComando`. `LoadEscopoSugestao`, `Step`, `EnsureShape` e todo o caminho de extração **ficam** (Task 5 os trata).

**Por que `CatalogoSugestoesTests.cs` é apagado:** os 12 casos cobrem `LotesDeSugestoes` e `MesclarCatalogo`, que deixam de existir — o lote de 500 ids era exatamente o que levava 20 minutos. A invariante que eles protegiam e que **continua valendo** (sugestão sem linha em `SUGESTOES_COMPRAS_RESULTADO` não pode desaparecer nem falhar; visto na instância real no id 17658) é reescrita em `CatalogoServiceTests.Contagem_ausente_vira_zero_e_nao_falha`.

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CatalogoServiceTests.cs`:

```csharp
using System.Data;
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// As peças puras da leitura do catálogo. As queries em si dependem de um SQL Server
/// vivo e são exercitadas pelo CLI contra o PBS.
/// <para>
/// A leitura de linha é testada com <see cref="DataTableReader"/>: as duas queries de
/// cabeçalho (catalogo_sugestoes.sql e sugestao_por_id.sql) devolvem a mesma forma, e
/// um erro de ordinal aqui só apareceria como conversão de tipo na frente do cliente.
/// </para>
/// </summary>
public sealed class CatalogoServiceTests
{
    private static DataTableReader LeitorDeCabecalho(
        long id = 18172, string? descricao = "ACHE RX", byte tipoCalculo = 1, object? diasCobertura = null)
    {
        var tabela = new DataTable();
        tabela.Columns.Add("SugestaoId", typeof(long));
        tabela.Columns.Add("Descricao", typeof(string));
        tabela.Columns.Add("DataHora", typeof(DateTime));
        tabela.Columns.Add("TipoCalculo", typeof(byte));
        tabela.Columns.Add("DiasCoberturaMax", typeof(int));
        tabela.Rows.Add(
            id,
            descricao ?? (object)DBNull.Value,
            new DateTime(2026, 6, 9, 14, 30, 0),
            tipoCalculo,
            diasCobertura ?? DBNull.Value);
        return tabela.CreateDataReader();
    }

    private static DataTableReader LeitorDeContagem(params (long Id, int Linhas, int Lojas)[] linhas)
    {
        var tabela = new DataTable();
        tabela.Columns.Add("SugestaoId", typeof(long));
        tabela.Columns.Add("QtdLinhas", typeof(int));
        tabela.Columns.Add("QtdLojas", typeof(int));
        foreach (var (id, qtdLinhas, qtdLojas) in linhas) tabela.Rows.Add(id, qtdLinhas, qtdLojas);
        return tabela.CreateDataReader();
    }

    private static SugestaoCatalogoCabecalho Cabecalho(long id, string? descricao) =>
        new(id, descricao, new DateTime(2026, 3, 1, 8, 0, 0), 1, 30);

    [Fact]
    public void Cabecalho_e_lido_na_ordem_dos_ordinais_da_query()
    {
        using var reader = LeitorDeCabecalho(diasCobertura: 5);
        reader.Read();

        var cabecalho = CatalogoService.LerCabecalho(reader);

        cabecalho.SugestaoId.Should().Be(18172);
        cabecalho.Descricao.Should().Be("ACHE RX");
        cabecalho.DataHora.Should().Be(new DateTime(2026, 6, 9, 14, 30, 0));
        cabecalho.TipoCalculo.Should().Be(1);
        cabecalho.DiasCoberturaMax.Should().Be(5);
    }

    [Fact]
    public void Descricao_nula_no_pbs_vira_nulo_e_nao_quebra()
    {
        using var reader = LeitorDeCabecalho(descricao: null, diasCobertura: 5);
        reader.Read();

        CatalogoService.LerCabecalho(reader).Descricao.Should().BeNull();
    }

    [Fact]
    public void Cobertura_nula_vira_zero_para_a_janela_ficar_degenerada_em_vez_de_o_catalogo_travar()
    {
        // Os cinco DIAS_CURVA_* podem ser todos NULL. Zero faz ExtractionWindow.Derive
        // devolver uma janela sem cobertura futura, que o comprador vê e descarta.
        using var reader = LeitorDeCabecalho(diasCobertura: null);
        reader.Read();

        CatalogoService.LerCabecalho(reader).DiasCoberturaMax.Should().Be(0);
    }

    [Fact]
    public void Contagem_da_sugestao_e_lida_da_linha_que_veio()
    {
        using var reader = LeitorDeContagem((18172, 365, 1));

        var contagem = CatalogoService.LerContagem(18172, reader);

        contagem.SugestaoId.Should().Be(18172);
        contagem.QtdLinhas.Should().Be(365);
        contagem.QtdLojas.Should().Be(1);
    }

    [Fact]
    public void Contagem_ausente_vira_zero_e_nao_falha()
    {
        // Visto na instância real: a sugestão 17658 existe em SUGESTOES_COMPRAS e não
        // tem nenhuma linha em SUGESTOES_COMPRAS_RESULTADO. Zero linhas é resposta
        // legítima, não erro — quem decide se dá para extrair é LoadEscopoSugestao.
        using var reader = LeitorDeContagem();

        var contagem = CatalogoService.LerContagem(17658, reader);

        contagem.Should().Be(new SugestaoContagem(17658, 0, 0));
    }

    [Fact]
    public void Filtro_vazio_devolve_o_catalogo_inteiro()
    {
        var catalogo = new[] { Cabecalho(1, "ACHE RX"), Cabecalho(2, "EMS GENERICO") };

        CatalogoService.Filtrar(catalogo, "  ").Should().HaveCount(2);
        CatalogoService.Filtrar(catalogo, null).Should().HaveCount(2);
    }

    [Fact]
    public void Filtro_acha_por_pedaco_da_descricao_ignorando_caixa()
    {
        var catalogo = new[] { Cabecalho(1, "ACHE RX"), Cabecalho(2, "EMS GENERICO") };

        var achados = CatalogoService.Filtrar(catalogo, "generico");

        achados.Should().ContainSingle().Which.SugestaoId.Should().Be(2);
    }

    [Fact]
    public void Filtro_acha_por_id()
    {
        var catalogo = new[] { Cabecalho(18172, "ACHE RX"), Cabecalho(17658, "EMS") };

        CatalogoService.Filtrar(catalogo, "18172").Should().ContainSingle()
            .Which.SugestaoId.Should().Be(18172);
    }

    [Fact]
    public void Filtro_nao_quebra_com_descricao_nula()
    {
        var catalogo = new[] { Cabecalho(1, null) };

        CatalogoService.Filtrar(catalogo, "ache").Should().BeEmpty();
        CatalogoService.Filtrar(catalogo, "1").Should().ContainSingle();
    }

    [Fact]
    public void Filtro_preserva_a_ordem_do_catalogo()
    {
        // A ordem é ORDER BY DATA_HORA DESC na query; filtrar não reordena.
        var catalogo = new[] { Cabecalho(30, "A x"), Cabecalho(10, "B x"), Cabecalho(20, "C x") };

        CatalogoService.Filtrar(catalogo, "x").Select(c => c.SugestaoId).Should().Equal(30L, 10L, 20L);
    }
}
```

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ConnectionStringFactoryTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class ConnectionStringFactoryTests
{
    private static AppConfig Config() => new()
    {
        Servidor = "natusfarma.procfit.com.br",
        Porta = 1435,
        Banco = "PBS_NATUSFARMA_DADOS",
        Usuario = "dev",
    };

    private static SqlConnectionStringBuilder Construir(AppConfig config, string senha = "s") =>
        new(ConnectionStringFactory.Build(config, senha));

    [Fact]
    public void Declara_resiliencia_de_conexao()
    {
        // Reconecta conexão ociosa quebrada. Não salva comando em execução -- para
        // isso existe Retentativa -- mas é barato e cobre a abertura.
        var builder = Construir(Config());

        builder.ConnectRetryCount.Should().Be(3);
        builder.ConnectRetryInterval.Should().Be(10);
    }

    [Fact]
    public void Porta_diferente_da_padrao_entra_no_data_source()
    {
        Construir(Config()).DataSource.Should().Be("natusfarma.procfit.com.br,1435");
    }

    [Fact]
    public void Timeout_de_conexao_vem_da_configuracao()
    {
        var config = Config();
        config.TimeoutConexaoSegundos = 40;

        Construir(config).ConnectTimeout.Should().Be(40);
    }

    [Fact]
    public void Timeout_absurdo_na_configuracao_cai_no_padrao()
    {
        // O arquivo é editado à mão; um zero ali não pode virar espera infinita.
        var config = Config();
        config.TimeoutConexaoSegundos = 0;

        Construir(config).ConnectTimeout.Should().Be(15);
    }

    [Fact]
    public void Windows_auth_nao_manda_usuario_nem_senha()
    {
        var config = Config();
        config.WindowsAuth = true;

        var builder = Construir(config);

        builder.IntegratedSecurity.Should().BeTrue();
        builder.UserID.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter "FullyQualifiedName~CatalogoServiceTests|FullyQualifiedName~ConnectionStringFactoryTests"`
Expected: erro de compilação — `CatalogoService` não existe e `AppConfig.TimeoutConexaoSegundos` não existe.

- [ ] **Step 3: Acrescentar as chaves de configuração**

Em `AppConfig.cs`, depois da propriedade `ApplicationName`:

```csharp
    /// <summary>
    /// Limites de espera das leituras curtas. Ficam aqui porque rede de cliente
    /// varia e trocar isto não pode exigir recompilação. A extração não tem limite
    /// (ver ExtractionService): ela varre dezenas de milhões de linhas por natureza,
    /// e um timeout ali só produziria falha no meio de um ZIP que estava indo bem.
    /// </summary>
    public int TimeoutConexaoSegundos { get; set; } = 15;
    public int TimeoutConsultaSegundos { get; set; } = 30;
    public int TimeoutContagemSegundos { get; set; } = 15;

    /// <summary>Quantos meses para trás o catálogo procura sugestões.</summary>
    public int MesesRetroativos { get; set; } = 12;

    internal const int TimeoutConexaoPadrao = 15;
    internal const int TimeoutConsultaPadrao = 30;
    internal const int TimeoutContagemPadrao = 15;
    internal const int MesesRetroativosPadrao = 12;

    /// <summary>O arquivo é editado à mão; valor fora de faixa não pode virar espera infinita.</summary>
    internal static int Segundos(int valor, int padrao) => valor is > 0 and <= 3600 ? valor : padrao;
```

E em `ConnectionStringFactory.Build`, troque a inicialização do builder:

```csharp
        var builder = new SqlConnectionStringBuilder
        {
            // Diferente do driver tedious, o SqlClient entende "host,porta".
            DataSource = config.Porta == 1433 ? config.Servidor : $"{config.Servidor},{config.Porta}",
            InitialCatalog = config.Banco,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = AppConfig.Segundos(config.TimeoutConexaoSegundos, AppConfig.TimeoutConexaoPadrao),

            // Reconecta conexão ociosa quebrada. Não salva comando em execução --
            // o caminho até o PBS do cliente derruba consulta em voo, e isso é
            // tratado por Retentativa.
            ConnectRetryCount = 3,
            ConnectRetryInterval = 10,
        };
```

- [ ] **Step 4: Implementar `CatalogoService.cs`**

Crie `CosmosPro.ML.DemandForCast.Extractor/CatalogoService.cs`:

```csharp
using System.Data;
using System.Globalization;
using FluentResults;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Leituras curtas do PBS: o catálogo de sugestões, a contagem de uma sugestão e as
/// lojas do teste de conexão. Separado de <see cref="ExtractionService"/> porque as
/// necessidades de erro são opostas — aqui a espera longa é sempre sintoma, e
/// retentar é seguro porque nada é escrito.
/// <para>
/// As contagens de linhas e lojas são pedidas para UMA sugestão, na seleção. Pedi-las
/// para o catálogo inteiro custava 30 s por lote de 500 ids na instância real
/// (COUNT(DISTINCT FILIAL) não é coberto pelo índice de SUGESTAO_COMPRA, em 124
/// milhões de linhas), o que dava ~20 min para 12 meses — mais do que a conexão até
/// o cliente sobrevive. Ver o spec de 2026-08-04.
/// </para>
/// </summary>
internal sealed class CatalogoService(AppConfig config, ExtratorLog log)
{
    public Result<IReadOnlyList<SugestaoCatalogoCabecalho>> Carregar(
        string connectionString, DateOnly dataInicio, CancellationToken ct)
    {
        var etapa = new Etapa("catálogo de sugestões", "catalogo_sugestoes.sql");

        return ComRetentativa(() => Consultar(connectionString, etapa, TimeoutConsulta, ct,
            comando =>
            {
                comando.Parameters.Add("@dataInicio", SqlDbType.Date).Value = dataInicio.ToDateTime(TimeOnly.MinValue);
            },
            reader =>
            {
                var cabecalhos = new List<SugestaoCatalogoCabecalho>();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    cabecalhos.Add(LerCabecalho(reader));
                }
                return (IReadOnlyList<SugestaoCatalogoCabecalho>)cabecalhos;
            },
            "{{DATA_INICIO}}", "@dataInicio"));
    }

    public Result<SugestaoContagem> Contar(string connectionString, long sugestaoId, CancellationToken ct)
    {
        var etapa = new Etapa("contagem da sugestão", "catalogo_sugestoes_contagens.sql");

        return ComRetentativa(() => Consultar(connectionString, etapa, TimeoutContagem, ct,
            comando =>
            {
                comando.Parameters.Add("@sugestao", SqlDbType.BigInt).Value = sugestaoId;
            },
            reader => LerContagem(sugestaoId, reader),
            "{{SUGESTOES}}", "@sugestao"));
    }

    public Result<SugestaoCatalogoCabecalho> PorId(string connectionString, long sugestaoId, CancellationToken ct)
    {
        var etapa = new Etapa("cabeçalho da sugestão", "sugestao_por_id.sql");

        var lido = ComRetentativa(() => Consultar(connectionString, etapa, TimeoutConsulta, ct,
            comando =>
            {
                comando.Parameters.Add("@sugestaoId", SqlDbType.BigInt).Value = sugestaoId;
            },
            reader => reader.Read() ? LerCabecalho(reader) : null,
            "{{SUGESTAO_ID}}", "@sugestaoId"));

        if (lido.IsFailed) return Result.Fail<SugestaoCatalogoCabecalho>(lido.Errors);

        return lido.Value is { } cabecalho
            ? Result.Ok(cabecalho)
            : Result.Fail<SugestaoCatalogoCabecalho>(new SugestaoNaoEncontradaErro(sugestaoId));
    }

    public Result<IReadOnlyList<LojaOption>> Lojas(string connectionString, CancellationToken ct)
    {
        var etapa = new Etapa("lojas disponíveis", "lojas_disponiveis.sql");

        return ComRetentativa(() => Consultar(connectionString, etapa, TimeoutConsulta, ct,
            _ => { },
            reader =>
            {
                var lojas = new List<LojaOption>();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    lojas.Add(new LojaOption(reader.GetInt32(0), reader.GetString(1)));
                }
                return (IReadOnlyList<LojaOption>)lojas;
            }));
    }

    /// <summary>
    /// Ordinais compartilhados por catalogo_sugestoes.sql e sugestao_por_id.sql — as
    /// duas devolvem o mesmo cabeçalho, e ler em dois lugares deixaria uma mudança de
    /// coluna aplicada só em um deles.
    /// </summary>
    internal static SugestaoCatalogoCabecalho LerCabecalho(IDataRecord registro) => new(
        registro.GetInt64(0),
        registro.IsDBNull(1) ? null : registro.GetString(1),
        registro.GetDateTime(2),
        registro.GetByte(3),
        // NULL quando os cinco DIAS_CURVA_* são todos NULL. Zero é o fallback seguro:
        // a janela derivada fica só até a data da sugestão, e o comprador escolhe
        // outra, em vez de o catálogo inteiro travar.
        registro.IsDBNull(4) ? 0 : registro.GetInt32(4));

    /// <summary>
    /// Zero linhas é resposta legítima: a sugestão pode existir em SUGESTOES_COMPRAS
    /// e não ter nenhuma linha em SUGESTOES_COMPRAS_RESULTADO (id 17658 na instância
    /// real). Quem recusa a extração é LoadEscopoSugestao, não a contagem.
    /// </summary>
    internal static SugestaoContagem LerContagem(long sugestaoId, IDataReader reader) =>
        reader.Read()
            ? new SugestaoContagem(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2))
            : new SugestaoContagem(sugestaoId, 0, 0);

    /// <summary>
    /// Filtro em memória sobre o catálogo já carregado: doze meses são ~19.500
    /// sugestões na instância real, e isso não se navega com scroll. Nenhuma ida
    /// extra ao banco.
    /// </summary>
    internal static IReadOnlyList<SugestaoCatalogoCabecalho> Filtrar(
        IReadOnlyList<SugestaoCatalogoCabecalho> catalogo, string? filtro)
    {
        if (string.IsNullOrWhiteSpace(filtro)) return catalogo;

        var termo = filtro.Trim();
        return [.. catalogo.Where(c =>
            c.SugestaoId.ToString(CultureInfo.InvariantCulture).Contains(termo, StringComparison.Ordinal)
            || (c.Descricao?.Contains(termo, StringComparison.OrdinalIgnoreCase) ?? false))];
    }

    private int TimeoutConsulta => AppConfig.Segundos(config.TimeoutConsultaSegundos, AppConfig.TimeoutConsultaPadrao);

    private int TimeoutContagem => AppConfig.Segundos(config.TimeoutContagemSegundos, AppConfig.TimeoutContagemPadrao);

    private Result<T> ComRetentativa<T>(Func<Result<T>> consulta) =>
        Retentativa.Executar(consulta, Retentativa.TentativasPadrao, log.Escrever, Thread.Sleep);

    /// <summary>
    /// O único ponto de tradução de exceção deste arquivo. <c>conexaoJaAberta</c> é o
    /// que separa "não consegui conectar" de "a conexão caiu no meio", que são
    /// conselhos opostos para o operador.
    /// </summary>
    private static Result<T> Consultar<T>(
        string connectionString,
        Etapa etapa,
        int timeoutSegundos,
        CancellationToken ct,
        Action<SqlCommand> parametros,
        Func<SqlDataReader, T> ler,
        string? placeholder = null,
        string? substituto = null)
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var conexaoJaAberta = false;

        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            conexaoJaAberta = true;

            var sql = SqlResources.Load(etapa.QueryFile!);
            if (placeholder is not null) sql = sql.Replace(placeholder, substituto);

            using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSegundos };
            parametros(command);
            using var cancelRegistration = ct.Register(command.Cancel);
            using var reader = command.ExecuteReader();

            return Result.Ok(ler(reader));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<T>(
                ClassificadorDeFalha.Classificar(FalhaBruta.De(ex, conexaoJaAberta), etapa, cronometro.Elapsed));
        }
    }
}
```

- [ ] **Step 5: Remover de `ExtractionService.cs` o que migrou**

Apague de `ExtractionService.cs`: `LoadLojas`, `LoadCatalogoSugestoes`, `LoadSugestaoPorId`, `LerCabecalho`, `LoadCabecalhosDoCatalogo`, `LoadContagensDoCatalogo`, `MaxParametrosPorComando`, `SugestoesPorLote`, `LotesDeSugestoes`, `MesclarCatalogo` e `CreateContagensCommand` — com os respectivos comentários de documentação. `SqlResources` fica onde está (o `CatalogoService` o usa).

Em `ExtractionModels.cs`, apague o record `SugestaoCatalogo` (as contagens deixam de viajar junto com o cabeçalho) e mantenha `SugestaoCatalogoCabecalho` e `SugestaoContagem`, ajustando o comentário de `SugestaoCatalogoCabecalho`:

```csharp
/// <summary>Cabeçalho de uma sugestão, como vem de SUGESTOES_COMPRAS sozinha.</summary>
internal sealed record SugestaoCatalogoCabecalho(
    long SugestaoId,
    string? Descricao,
    DateTime DataHora,
    byte TipoCalculo,
    int DiasCoberturaMax);
```

- [ ] **Step 6: Apagar os testes do lote de contagens**

```bash
git rm tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CatalogoSugestoesTests.cs
```

- [ ] **Step 7: Compilar — vai falhar de propósito**

Run: `dotnet build CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj -c Debug --nologo`
Expected: erros em `MainForm.cs` e `ExtractorCli.cs`, que ainda chamam `ExtractionService.LoadLojas` / `LoadCatalogoSugestoes` / `LoadSugestaoPorId`. Isso é esperado: Tasks 6 e 7 os reescrevem. Para fechar este task, aplique o mínimo que compila — troque as chamadas por `CatalogoService` mantendo o `.Value`/`.Errors` cru:

Em `MainForm.TestarConexaoAsync`, `MainForm.CarregarSugestoesAsync` e `ExtractorCli.Listar`/`Extrair`, use um serviço local e trate a falha lançando, como ponte temporária:

```csharp
        var servico = new CatalogoService(_config, new ExtratorLog(AppContext.BaseDirectory));
        var resultado = servico.Carregar(connectionString, dataInicio, CancellationToken.None);
        if (resultado.IsFailed) throw new InvalidOperationException(resultado.Errors[0].Message);
        var catalogo = resultado.Value;
```

O grid passa a receber `SugestaoCatalogoCabecalho`, então em `PopularGrid` troque a projeção para `new SugestaoLinha(c.SugestaoId, c.Descricao ?? "(sem descrição)", c.DataHora, MetodoTexto(c.TipoCalculo))` e remova `QtdLinhas`/`QtdLojas` de `SugestaoLinha`, de `ConfigurarColunas` e das duas escritas do CLI (`EscreverTabela` e `EscreverTsv` — tire as colunas `Linhas` e `Lojas` do cabeçalho e das linhas). `AtualizarJanela` passa a buscar em `_catalogo` do tipo novo.

> Esta ponte é feia de propósito e vive por dois tasks. O Task 6 mata o `throw` do form e o Task 7 mata o do CLI.

- [ ] **Step 8: Rodar a suíte inteira**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: tudo passa. `ExtractorCliTests` pode exigir ajuste se afirmar as colunas `Linhas`/`Lojas` da saída TSV — se afirmar, atualize a expectativa para a lista nova de colunas.

- [ ] **Step 9: Commit**

```bash
git add -A CosmosPro.ML.DemandForCast.Extractor tests/CosmosPro.ML.DemandForCast.Extractor.Tests
git commit -m "perf(extractor)!: load the catalog header-only and count one suggestion on demand

Medido na Natus: cabecalhos de 12 meses (19.581 sugestoes) em 0,27s; o lote de
contagens que existia antes custava 30,25s por 500 ids -- 40 lotes, cerca de 20
minutos, mais do que a conexao ate o cliente sobrevive. COUNT(DISTINCT FILIAL)
nao e coberto pelo indice de SUGESTAO_COMPRA em 124M linhas; a contagem de uma
sugestao custa 0,01s."
```

---

### Task 5: `ExtractionService` devolve `Result`

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractionService.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtractionServiceTests.cs` (acrescenta casos)

**Interfaces:**
- Consumes: `Etapa`, `FalhaBruta`, `ClassificadorDeFalha`, `ContratoErro`, `SugestaoSemItensErro`, `SugestaoNaoEncontradaErro` (Task 1).
- Produces: `Result<ExtractionResult> Run(ExtractionRequest request, IProgress<ExtractionProgress> progress, CancellationToken ct)`.

**Desenho:** a extração tem uma dúzia de etapas internas encadeadas. Enfiar `Result` em cada método privado só espalharia `if (falhou) return` sem ganho — o que a spec exige é **um** ponto de tradução por operação. Então:

- `Step<T>` continua embrulhando falha em `EtapaFalhouException` (renomeado de `ExtractionStepException`), agora carregando a `Etapa` tipada em vez de uma string.
- Falhas de domínio de dentro do `Run` (`SugestaoSemItens`, `SugestaoNaoEncontrada`, contrato) viram `FalhaDeDominioException`, que carrega o `ExtratorErro` pronto.
- `Run` tem **um** `try/catch` que traduz as duas em `Result.Fail` e apaga o ZIP parcial. `OperationCanceledException` continua subindo.
- Nenhuma das duas exceções é pública: `internal sealed` e usadas só dentro do arquivo.

- [ ] **Step 1: Escrever os testes que falham**

Acrescente a `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/ExtractionServiceTests.cs`:

```csharp
    [Fact]
    public void Run_com_pasta_de_saida_invalida_devolve_falha_e_nao_lanca()
    {
        // Caminho impossível: a falha tem de chegar como Result, não como exceção
        // atravessando a borda do serviço.
        var request = new ExtractionRequest
        {
            ConnectionString = "Data Source=nao.existe;Initial Catalog=x;User ID=u;Password=p;Connect Timeout=1",
            SugestaoId = 1,
            DataInicial = new DateOnly(2025, 1, 1),
            DataFinal = new DateOnly(2025, 1, 31),
            OutputDirectory = Path.Combine(Path.GetTempPath(), "extrator-teste-" + Guid.NewGuid().ToString("N")),
        };

        var resultado = new ExtractionService().Run(request, new Progress<ExtractionProgress>(), CancellationToken.None);

        resultado.IsFailed.Should().BeTrue();
        resultado.Errors.Single().Should().BeAssignableTo<ExtratorErro>();
    }

    [Fact]
    public void Falha_de_conexao_na_extracao_nao_deixa_zip_parcial()
    {
        // ZIP parcial é pior que nenhum: ele passa na validação de header do import e
        // entraria no Stage como se estivesse completo.
        var pasta = Path.Combine(Path.GetTempPath(), "extrator-teste-" + Guid.NewGuid().ToString("N"));
        var request = new ExtractionRequest
        {
            ConnectionString = "Data Source=nao.existe;Initial Catalog=x;User ID=u;Password=p;Connect Timeout=1",
            SugestaoId = 1,
            DataInicial = new DateOnly(2025, 1, 1),
            DataFinal = new DateOnly(2025, 1, 31),
            OutputDirectory = pasta,
        };

        new ExtractionService().Run(request, new Progress<ExtractionProgress>(), CancellationToken.None);

        Directory.GetFiles(pasta, "*.zip").Should().BeEmpty();
    }
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~ExtractionServiceTests`
Expected: erro de compilação — `Run` devolve `ExtractionResult`, não `Result<ExtractionResult>`, então `resultado.IsFailed` não existe.

- [ ] **Step 3: Trocar a assinatura e o embrulho de erro**

Em `ExtractionService.cs`, troque a assinatura e o `try/catch` do `Run`:

```csharp
    public Result<ExtractionResult> Run(ExtractionRequest request, IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var zipPath = string.Empty;

        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        try
        {
            Directory.CreateDirectory(request.OutputDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            zipPath = Path.Combine(request.OutputDirectory, $"extracao-pbs_{stamp}.zip");

            // ... corpo atual, sem alteração, do using (var output = File.Create(zipPath)) até o fim do bloco
        }
        catch (FalhaDeDominioException falha)
        {
            TryDelete(zipPath);
            return Result.Fail<ExtractionResult>(falha.Erro);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(zipPath);
            var etapa = ex is EtapaFalhouException etapaFalhou ? etapaFalhou.Etapa : new Etapa("extração", null);
            return Result.Fail<ExtractionResult>(
                ClassificadorDeFalha.Classificar(FalhaBruta.De(ex, conexaoJaAberta: true), etapa, cronometro.Elapsed));
        }

        if (rows[StageContract.Vendas] == 0)
        {
            warnings.Add("Nenhuma venda na janela de dados derivada da sugestão — confira se a sugestão escolhida faz sentido.");
        }
        if (rows[StageContract.EstoquesDiarios] == 0)
        {
            warnings.Add("Nenhum estoque no período — o histórico de ESTOQUE_LANCAMENTOS costuma cobrir apenas os últimos meses.");
        }

        return Result.Ok(new ExtractionResult(zipPath, new FileInfo(zipPath).Length, rows, warnings));
    }
```

Nota: `Directory.CreateDirectory` entra **dentro** do `try` — hoje está fora, e uma pasta inválida escapa como exceção crua pela borda do serviço.

- [ ] **Step 4: Trocar as exceções internas**

No fim de `ExtractionService.cs`, substitua `ExtractionStepException` por:

```csharp
/// <summary>
/// Falha dentro de uma etapa nomeada. Nunca sai desta classe: o único catch de
/// <see cref="ExtractionService.Run"/> a traduz em <c>Result.Fail</c>. Existe porque
/// a extração tem uma dúzia de etapas encadeadas, e devolver Result de cada método
/// privado espalharia verificação sem acrescentar informação.
/// </summary>
internal sealed class EtapaFalhouException(Etapa etapa, Exception causa)
    : InvalidOperationException($"Falha na etapa '{etapa}': {causa.Message}", causa)
{
    public Etapa Etapa { get; } = etapa;
}

/// <summary>Falha de domínio já classificada, a caminho do catch único do Run.</summary>
internal sealed class FalhaDeDominioException(ExtratorErro erro)
    : InvalidOperationException(erro.Message)
{
    public ExtratorErro Erro { get; } = erro;
}
```

Troque `Step<T>` para usar a `Etapa` tipada:

```csharp
    internal static T Step<T>(Etapa etapa, Func<T> acao)
    {
        try
        {
            return acao();
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                      and not EtapaFalhouException
                                      and not FalhaDeDominioException)
        {
            throw new EtapaFalhouException(etapa, ex);
        }
    }

    internal static void Step(Etapa etapa, Action acao) =>
        Step<object?>(etapa, () =>
        {
            acao();
            return null;
        });
```

Atualize as ~10 chamadas de `Step("texto (arquivo.sql)", ...)` para `Step(new Etapa("texto", "arquivo.sql"), ...)`. Exemplo, em `CopyQueryCore`:

```csharp
        Step(new Etapa(entryName, queryFile), () =>
```

E em `CopyEstoques`:

```csharp
        Step(new Etapa(StageContract.EstoquesDiarios, "estoques_movimentos.sql"), () =>
```

- [ ] **Step 5: Trocar os `throw` de domínio**

Em `LoadEscopoSugestao`:

```csharp
        if (lojaIds.Count == 0)
        {
            throw new FalhaDeDominioException(new SugestaoSemItensErro(sugestaoId));
        }
```

No `Run`, o cabeçalho ausente:

```csharp
                var cabecalho = CopySugestaoHeader(connection, zip, request.SugestaoId, 8, total, progress, ct)
                    ?? throw new FalhaDeDominioException(new SugestaoNaoEncontradaErro(request.SugestaoId));
```

Em `EnsureShape`, as duas divergências:

```csharp
        if (reader.FieldCount != header.Count)
        {
            throw new FalhaDeDominioException(new ContratoErro(entryName,
                $"query devolveu {reader.FieldCount} colunas, esperado {header.Count}"));
        }

        for (var i = 0; i < header.Count; i++)
        {
            if (!string.Equals(reader.GetName(i), header[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new FalhaDeDominioException(new ContratoErro(entryName,
                    $"coluna {i + 1} é '{reader.GetName(i)}', esperado '{header[i]}'"));
            }
        }
```

- [ ] **Step 6: Rodar a suíte inteira**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: tudo passa. Os chamadores (`MainForm.ExtrairAsync`, `ExtractorCli.Extrair`) precisam de `.Value`; se o build reclamar, trate a falha lançando como no Task 4 — Tasks 6 e 7 arrumam.

- [ ] **Step 7: Commit**

```bash
git add -A CosmosPro.ML.DemandForCast.Extractor tests/CosmosPro.ML.DemandForCast.Extractor.Tests
git commit -m "refactor(extractor)!: return Result from the extraction, with one translation point

Directory.CreateDirectory entra no try: hoje uma pasta invalida escapa como
excecao crua pela borda do servico."
```

---

### Task 6: `OperacaoUi` — o escopo que sustenta a honestidade

**Files:**
- Create: `CosmosPro.ML.DemandForCast.Extractor/OperacaoUi.cs`
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/OperacaoUiTests.cs`

**Interfaces:**
- Consumes: nada além do WinForms.
- Produces:
  - `sealed record AlvosDaOperacao(IReadOnlyList<Control> Travar, Button Cancelar, ProgressBar Progresso, Label Status)`
  - `sealed class OperacaoUi : IDisposable` com `static OperacaoUi Iniciar(AlvosDaOperacao alvos, string titulo, int? totalDeEtapas)`, `CancellationToken Token`, `void Reportar(string detalhe, int? etapaAtual)`, `void Concluir(string texto)`, `Dispose()`
  - `internal static string TextoDeStatus(string titulo, TimeSpan decorrido, string? detalhe)`

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/OperacaoUiTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O texto do rodapé é o que faltou quando o operador esperou 2min09 sem saber se a
/// operação estava andando. Ele é testado; o resto de OperacaoUi mexe em Control e
/// depende de bomba de mensagens, então fica pequeno de propósito.
/// </summary>
public sealed class OperacaoUiTests
{
    [Fact]
    public void Status_leva_o_relogio_para_lento_nao_parecer_travado()
    {
        var texto = OperacaoUi.TextoDeStatus("Carregando sugestões", TimeSpan.FromSeconds(12), detalhe: null);

        texto.Should().Contain("Carregando sugestões");
        texto.Should().Contain("12s");
    }

    [Fact]
    public void Status_passa_de_um_minuto_em_minutos_e_segundos()
    {
        OperacaoUi.TextoDeStatus("Extraindo", TimeSpan.FromSeconds(129), null).Should().Contain("2min09");
    }

    [Fact]
    public void Detalhe_da_etapa_entra_no_status_sem_esconder_o_relogio()
    {
        var texto = OperacaoUi.TextoDeStatus("Extraindo", TimeSpan.FromSeconds(5), "[3/9] vendas.csv — 25.000 linhas");

        texto.Should().Contain("[3/9] vendas.csv");
        texto.Should().Contain("5s");
    }

    [Fact]
    public void Zero_segundos_ainda_mostra_o_relogio()
    {
        OperacaoUi.TextoDeStatus("Testando conexão", TimeSpan.Zero, null).Should().Contain("0s");
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~OperacaoUiTests`
Expected: erro de compilação — `OperacaoUi` não existe.

- [ ] **Step 3: Implementar**

Crie `CosmosPro.ML.DemandForCast.Extractor/OperacaoUi.cs`:

```csharp
using System.Globalization;

namespace CosmosPro.ML.DemandForCast.Extractor;

internal sealed record AlvosDaOperacao(
    IReadOnlyList<Control> Travar, Button Cancelar, ProgressBar Progresso, Label Status);

/// <summary>
/// Uma operação longa = um <c>using</c>. Enquanto ele vive, os inputs estão travados,
/// o Cancelar responde, a barra anda e o rodapé mostra o tempo decorrido. No
/// <c>Dispose</c> tudo volta, inclusive se a operação morreu no meio.
/// <para>
/// Existe porque as quatro coisas que faziam o extrator parecer travado eram
/// exatamente estas quatro, e cada rota do form as resolvia por conta própria — ou
/// não resolvia: o catálogo não tinha token de cancelamento nenhum.
/// </para>
/// </summary>
internal sealed class OperacaoUi : IDisposable
{
    private readonly AlvosDaOperacao _alvos;
    private readonly string _titulo;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Forms.Timer _cronometro = new() { Interval = 1000 };
    private readonly System.Diagnostics.Stopwatch _decorrido = System.Diagnostics.Stopwatch.StartNew();
    private readonly bool[] _estadoAnterior;
    private readonly ProgressBarStyle _estiloAnterior;
    private string? _detalhe;

    private OperacaoUi(AlvosDaOperacao alvos, string titulo, int? totalDeEtapas)
    {
        _alvos = alvos;
        _titulo = titulo;
        _estadoAnterior = [.. alvos.Travar.Select(c => c.Enabled)];
        _estiloAnterior = alvos.Progresso.Style;

        foreach (var controle in alvos.Travar) controle.Enabled = false;
        alvos.Cancelar.Enabled = true;

        // Marquee quando não há total conhecido: barra parada em zero durante
        // trabalho real afirma que nada está acontecendo.
        alvos.Progresso.Style = totalDeEtapas is null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (totalDeEtapas is { } total)
        {
            alvos.Progresso.Maximum = total;
            alvos.Progresso.Value = 0;
        }

        _cronometro.Tick += (_, _) => AtualizarStatus();
        _cronometro.Start();
        AtualizarStatus();
    }

    public static OperacaoUi Iniciar(AlvosDaOperacao alvos, string titulo, int? totalDeEtapas) =>
        new(alvos, titulo, totalDeEtapas);

    public CancellationToken Token => _cts.Token;

    public void Cancelar() => _cts.Cancel();

    public void Reportar(string detalhe, int? etapaAtual)
    {
        _detalhe = detalhe;
        if (etapaAtual is { } etapa && _alvos.Progresso.Style == ProgressBarStyle.Continuous)
        {
            _alvos.Progresso.Value = Math.Clamp(etapa, _alvos.Progresso.Minimum, _alvos.Progresso.Maximum);
        }
        AtualizarStatus();
    }

    public void Concluir(string texto)
    {
        _cronometro.Stop();
        _alvos.Status.Text = texto;
    }

    public string Decorrido => Duracao(_decorrido.Elapsed);

    internal static string TextoDeStatus(string titulo, TimeSpan decorrido, string? detalhe) =>
        detalhe is null
            ? $"{titulo}… {Duracao(decorrido)}"
            : $"{titulo}… {Duracao(decorrido)} — {detalhe}";

    private static string Duracao(TimeSpan decorrido) =>
        decorrido.TotalSeconds < 60
            ? $"{((int)decorrido.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s"
            : $"{((int)decorrido.TotalMinutes).ToString(CultureInfo.InvariantCulture)}min"
              + decorrido.Seconds.ToString("00", CultureInfo.InvariantCulture);

    private void AtualizarStatus() => _alvos.Status.Text = TextoDeStatus(_titulo, _decorrido.Elapsed, _detalhe);

    public void Dispose()
    {
        _cronometro.Stop();
        _cronometro.Dispose();

        for (var i = 0; i < _alvos.Travar.Count; i++) _alvos.Travar[i].Enabled = _estadoAnterior[i];
        _alvos.Cancelar.Enabled = false;
        _alvos.Progresso.Style = _estiloAnterior;
        if (_estiloAnterior == ProgressBarStyle.Marquee) _alvos.Progresso.Style = ProgressBarStyle.Continuous;

        _cts.Dispose();
    }
}
```

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~OperacaoUiTests`
Expected: 4 passam.

- [ ] **Step 5: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/OperacaoUi.cs \
        tests/CosmosPro.ML.DemandForCast.Extractor.Tests/OperacaoUiTests.cs
git commit -m "feat(extractor): add an operation scope that locks inputs, ticks a clock and cancels"
```

---

### Task 7: `MainForm` — cablagem da honestidade e do catálogo novo

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/MainForm.cs`

**Interfaces:**
- Consumes: `CatalogoService`, `ExtratorLog`, `OperacaoUi`, `AlvosDaOperacao`, `ExtratorErro`, `Result<T>`.
- Produces: nada para tasks seguintes.

- [ ] **Step 1: Campos novos e remoção da constante**

Remova `MesesRetroativosCatalogo` e acrescente os campos:

```csharp
    private readonly NumericUpDown _meses = new() { Width = 60, Minimum = 1, Maximum = 60, Value = 12 };
    private readonly TextBox _filtro = new() { Width = 200, PlaceholderText = "filtrar por id ou descrição" };
    private readonly Button _copiarLog = new() { Text = "Copiar log", Width = 100 };

    private readonly ExtratorLog _log;
    private readonly CatalogoService _catalogoService;
    private OperacaoUi? _operacao;

    private IReadOnlyList<SugestaoCatalogoCabecalho> _catalogo = [];
```

No construtor, antes de `BuildLayout()`:

```csharp
        _log = new ExtratorLog(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            tela: linha => _log_.AppendText(linha + Environment.NewLine));
        _catalogoService = new CatalogoService(_config, _log);
```

(O campo do painel `_log` já existe com esse nome — renomeie o `TextBox` para `_painelDeLog` e use `_painelDeLog.AppendText` acima, para o nome `_log` ficar com o sink.)

Ligações novas no construtor:

```csharp
        _cancelar.Click += (_, _) => _operacao?.Cancelar();
        _filtro.TextChanged += (_, _) => AplicarFiltro();
        _copiarLog.Click += (_, _) => CopiarLog();
        _meses.Value = _config.MesesRetroativos is >= 1 and <= 60 ? _config.MesesRetroativos : 12;
```

No layout, ponha `_meses` e `_filtro` na linha do `_carregarSugestoes` dentro do GroupBox "Sugestão de compra", e `_copiarLog` à direita da barra de progresso. Aumente `ClientSize` para `new Size(660, 760)` e reposicione o que vier depois.

- [ ] **Step 2: Trocar `RunGuardedAsync` pelo escopo**

Substitua `RunGuardedAsync` por:

```csharp
    /// <summary>
    /// Uma operação longa, com tudo o que ela deve ao operador: inputs travados,
    /// Cancelar ativo, relógio andando e o desfecho no log — inclusive quando falha.
    /// </summary>
    private async Task ExecutarAsync<T>(
        string titulo, int? totalDeEtapas, Func<CancellationToken, Result<T>> operacao, Action<T> aoConcluir)
    {
        var alvos = new AlvosDaOperacao(
            [_conexaoBox, _sugestaoBox, _saidaBox, _extrair, _testar, _carregarSugestoes],
            _cancelar, _progresso, _status);

        using var escopo = OperacaoUi.Iniciar(alvos, titulo, totalDeEtapas);
        _operacao = escopo;
        _log.Escrever($"{titulo}...");

        try
        {
            var resultado = await Task.Run(() => operacao(escopo.Token), escopo.Token);

            if (resultado.IsSuccess)
            {
                aoConcluir(resultado.Value);
                escopo.Concluir($"Concluído em {escopo.Decorrido}.");
                return;
            }

            var erro = resultado.Errors.OfType<ExtratorErro>().First();
            escopo.Concluir("Falhou.");
            _log.Escrever($"ERRO: {erro.Message}");
            foreach (var (chave, valor) in erro.Metadata)
            {
                _log.EscreverSoNoArquivo($"  {chave}: {valor}");
            }
            MessageBox.Show(this, erro.Message + Environment.NewLine + Environment.NewLine
                + $"Detalhe completo em {_log.CaminhoDeHoje}", "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (OperationCanceledException)
        {
            escopo.Concluir("Cancelado.");
            _log.Escrever("Cancelado pelo usuário.");
        }
        finally
        {
            _operacao = null;
            AtualizarJanela();
        }
    }
```

`_conexaoBox`, `_sugestaoBox` e `_saidaBox` são os três `GroupBox` que hoje são variáveis locais de `BuildLayout` — promova-os a campos `private readonly GroupBox` para poderem ser travados.

- [ ] **Step 3: Reescrever as três rotas**

```csharp
    private Task TestarConexaoAsync() =>
        ExecutarAsync("Testando conexão", null,
            ct => _catalogoService.Lojas(BuildConnectionString(), ct),
            lojas =>
            {
                _log.Escrever($"Conexão OK. {lojas.Count} lojas ativas encontradas.");
                _config.Save();
            });

    private Task CarregarSugestoesAsync()
    {
        var meses = (int)_meses.Value;
        var dataInicio = DateOnly.FromDateTime(DateTime.Today).AddMonths(-meses);

        return ExecutarAsync($"Carregando sugestões dos últimos {meses} meses", null,
            ct => _catalogoService.Carregar(BuildConnectionString(), dataInicio, ct),
            catalogo =>
            {
                _catalogo = catalogo;
                _config.MesesRetroativos = meses;
                _config.Save();
                _log.Escrever($"{catalogo.Count:N0} sugestões carregadas.");
                AplicarFiltro();
            });
    }

    private Task ContarSelecaoAsync(long sugestaoId, string textoDaJanela) =>
        ExecutarAsync($"Contando itens da sugestão {sugestaoId}", null,
            ct => _catalogoService.Contar(BuildConnectionString(), sugestaoId, ct),
            contagem => _janelaInfo.Text =
                $"{contagem.QtdLinhas:N0} itens · {contagem.QtdLojas:N0} loja(s) · {textoDaJanela}");
```

- [ ] **Step 4: Filtro e seleção**

```csharp
    private void AplicarFiltro()
    {
        var visiveis = CatalogoService.Filtrar(_catalogo, _filtro.Text);
        _sugestoes.DataSource = visiveis
            .Select(c => new SugestaoLinha(
                c.SugestaoId, c.Descricao ?? "(sem descrição)", c.DataHora,
                MetodoTexto(c.TipoCalculo), c.DiasCoberturaMax))
            .ToList();
        ConfigurarColunas();

        if (_catalogo.Count == 0)
        {
            _janela = null;
            _janelaInfo.Text = "Nenhuma sugestão encontrada no período.";
            _extrair.Enabled = false;
        }
        else if (visiveis.Count == 0)
        {
            _janelaInfo.Text = $"Nenhuma das {_catalogo.Count:N0} sugestões carregadas casa com o filtro.";
            _extrair.Enabled = false;
        }
    }
```

Em `AtualizarJanela`, depois de derivar `_janela` e habilitar `_extrair`, dispare a contagem — e **não** deixe a contagem decidir se pode extrair:

```csharp
        if (_janela.Viavel)
        {
            var textoDaJanela = $"janela de dados {_janela.Inicio:dd/MM/yyyy} a {_janela.Fim:dd/MM/yyyy}";
            _janelaInfo.Text = textoDaJanela;
            _extrair.Enabled = true;

            // Contagem é conforto do operador, não pré-condição: se falhar ou
            // estourar o timeout, a extração continua permitida.
            if (_operacao is null) _ = ContarSelecaoAsync(catalogo.SugestaoId, textoDaJanela);
        }
```

`SugestaoLinha` passa a ser `record SugestaoLinha(long SugestaoId, string Descricao, DateTime DataHora, string Metodo, int Cobertura)`, e `ConfigurarColunas` renomeia `Cobertura` para `"Cobert."` e não menciona mais `QtdLinhas`/`QtdLojas`.

- [ ] **Step 5: Extração pela mesma rota, e o botão de copiar**

```csharp
    private Task ExtrairAsync()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada || _janela is not { Viavel: true } janela)
        {
            MessageBox.Show(this, "Selecione uma sugestão com janela viável.", "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }

        var request = new ExtractionRequest
        {
            ConnectionString = BuildConnectionString(),
            SugestaoId = selecionada.SugestaoId,
            DataInicial = janela.Inicio,
            DataFinal = janela.Fim,
            OutputDirectory = _pastaSaida.Text.Trim(),
        };
        _config.Save();

        return ExecutarAsync("Extraindo", StageContract.WriteOrder.Length,
            ct =>
            {
                var progresso = new Progress<ExtractionProgress>(p =>
                    _operacao?.Reportar($"[{p.FileIndex}/{p.FileCount}] {p.FileName} — {p.RowsWritten:N0} linhas", p.FileIndex));
                return new ExtractionService().Run(request, progresso, ct);
            },
            resultado =>
            {
                _log.Escrever($"ZIP gerado: {resultado.ZipPath} ({resultado.ZipBytes / 1024d / 1024d:N1} MB)");
                foreach (var (file, count) in resultado.RowsByFile) _log.Escrever($"  {file}: {count:N0} linhas");
                foreach (var warning in resultado.Warnings) _log.Escrever($"  AVISO: {warning}");
            });
    }

    private void CopiarLog()
    {
        if (_painelDeLog.TextLength > 0) Clipboard.SetText(_painelDeLog.Text);
        _log.Escrever("Log copiado para a área de transferência.");
    }
```

Apague `Log(...)` e `DicaLogonTrigger(...)` — o primeiro virou `_log.Escrever`, o segundo virou `LogonTriggerErro`.

- [ ] **Step 6: Compilar e rodar a suíte**

Run: `dotnet build CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj -c Debug --nologo && dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: build limpo, testes verdes.

- [ ] **Step 7: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/MainForm.cs
git commit -m "fix(extractor): make every long operation cancellable, timed and visible

O catalogo passava CancellationToken.None e o botao Cancelar so existia na rota
de extracao -- por isso 'Carregando sugestoes...' nao tinha como ser
interrompido nem como mostrar que ainda estava andando."
```

---

### Task 8: CLI derivando exit code do erro tipado

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/ExtractorCli.cs`
- Modify: `CosmosPro.ML.DemandForCast.Extractor/CliParser.cs` (texto de ajuda: `--months-back` passa a ser só do `--list`)
- Test: `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CliExitCodeMapTests.cs`

**Interfaces:**
- Consumes: todos os erros (Task 1), `CatalogoService` (Task 4), `ExtractionService.Run` (Task 5).
- Produces: `static class CliExitCodeMap` com `int De(ExtratorErro erro)`.

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CliExitCodeMapTests.cs`:

```csharp
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O código de saída é contrato: quem chama o extrator de um script decide o que
/// fazer olhando o número. Ele vem do erro tipado, num mapa só, para a tela e o
/// script nunca discordarem sobre o que aconteceu.
/// </summary>
public sealed class CliExitCodeMapTests
{
    private static readonly Etapa Qualquer = new("catálogo", "catalogo_sugestoes.sql");

    [Fact]
    public void Falha_de_conexao()
    {
        CliExitCodeMap.De(new ConexaoErro("x")).Should().Be(CliExitCode.FalhaDeConexao);
    }

    [Fact]
    public void Logon_trigger_tambem_e_falha_de_conexao()
    {
        CliExitCodeMap.De(new LogonTriggerErro()).Should().Be(CliExitCode.FalhaDeConexao);
    }

    [Fact]
    public void Queda_no_meio_e_falha_de_conexao()
    {
        CliExitCodeMap.De(new ConexaoPerdidaErro(Qualquer, TimeSpan.FromSeconds(129)))
            .Should().Be(CliExitCode.FalhaDeConexao);
    }

    [Fact]
    public void Sugestao_inexistente_tem_codigo_proprio()
    {
        CliExitCodeMap.De(new SugestaoNaoEncontradaErro(1)).Should().Be(CliExitCode.SugestaoNaoEncontrada);
    }

    [Fact]
    public void Sugestao_sem_itens_e_sugestao_nao_encontrada_para_quem_chama()
    {
        // Do lado do script o desfecho é o mesmo: este id não dá para extrair.
        CliExitCodeMap.De(new SugestaoSemItensErro(1)).Should().Be(CliExitCode.SugestaoNaoEncontrada);
    }

    [Fact]
    public void Janela_inviavel_tem_codigo_proprio()
    {
        CliExitCodeMap.De(new JanelaInviavelErro("m")).Should().Be(CliExitCode.JanelaInviavel);
    }

    [Theory]
    [InlineData(typeof(ContratoErro))]
    [InlineData(typeof(EscritaErro))]
    [InlineData(typeof(TempoExcedidoErro))]
    [InlineData(typeof(EtapaErro))]
    [InlineData(typeof(ConcorrenciaErro))]
    [InlineData(typeof(InesperadoErro))]
    public void O_resto_e_falha_na_extracao(Type tipo)
    {
        ExtratorErro erro = tipo.Name switch
        {
            nameof(ContratoErro) => new ContratoErro("vendas.csv", "d"),
            nameof(EscritaErro) => new EscritaErro("C:\\x", "d"),
            nameof(TempoExcedidoErro) => new TempoExcedidoErro(Qualquer, TimeSpan.FromSeconds(30)),
            nameof(EtapaErro) => new EtapaErro(Qualquer, "d"),
            nameof(ConcorrenciaErro) => new ConcorrenciaErro(Qualquer),
            _ => new InesperadoErro(typeof(FormatException), "d"),
        };

        CliExitCodeMap.De(erro).Should().Be(CliExitCode.FalhaNaExtracao);
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj --filter FullyQualifiedName~CliExitCodeMapTests`
Expected: erro de compilação — `CliExitCodeMap` não existe.

- [ ] **Step 3: Implementar o mapa e reescrever o CLI**

No topo de `ExtractorCli.cs`, acrescente:

```csharp
/// <summary>
/// Erro tipado -> código de saída. Um mapa só: antes o form e o CLI interpretavam a
/// exceção cada um por conta própria, e podiam discordar sobre o que aconteceu.
/// </summary>
internal static class CliExitCodeMap
{
    public static int De(ExtratorErro erro) => erro switch
    {
        ConexaoErro or ConexaoPerdidaErro or LogonTriggerErro => CliExitCode.FalhaDeConexao,
        SugestaoNaoEncontradaErro or SugestaoSemItensErro => CliExitCode.SugestaoNaoEncontrada,
        JanelaInviavelErro => CliExitCode.JanelaInviavel,
        _ => CliExitCode.FalhaNaExtracao,
    };
}
```

Reescreva `Listar` e `Extrair` para consumir `Result` — sem `try/catch` em volta, porque a falha agora chega como valor:

```csharp
    private static int Listar(CliOptions options, AppConfig config, string connectionString, ExtratorLog log, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var resultado = new CatalogoService(config, log)
            .Carregar(connectionString, hoje.AddMonths(-options.MesesRetroativos), ct);

        if (resultado.IsFailed) return Falhar(resultado, options.StackTrace);

        var catalogo = resultado.Value;
        if (catalogo.Count == 0)
        {
            Console.Error.WriteLine(
                $"Nenhuma sugestão de compra nos últimos {options.MesesRetroativos} meses. "
                + "Aumente --months-back para procurar mais para trás.");
            return CliExitCode.Sucesso;
        }

        if (options.Tsv) EscreverTsv(catalogo, hoje);
        else EscreverTabela(catalogo, hoje);

        return CliExitCode.Sucesso;
    }

    private static int Extrair(CliOptions options, AppConfig config, string connectionString, ExtratorLog log, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var servico = new CatalogoService(config, log);

        var cabecalho = servico.PorId(connectionString, options.SugestaoId, ct);
        if (cabecalho.IsFailed) return Falhar(cabecalho, options.StackTrace);

        var sugestao = cabecalho.Value;
        var janela = ExtractionWindow.Derive(DateOnly.FromDateTime(sugestao.DataHora), sugestao.DiasCoberturaMax, hoje);
        if (!janela.Viavel)
        {
            Console.Error.WriteLine(janela.MotivoInviabilidade);
            return CliExitCode.JanelaInviavel;
        }

        Console.WriteLine($"Sugestão {sugestao.SugestaoId} — {Descricao(sugestao)} — {sugestao.DataHora:dd/MM/yyyy HH:mm} — {Metodo(sugestao.TipoCalculo)}");
        Console.WriteLine($"Janela de dados: {janela.Inicio:dd/MM/yyyy} a {janela.Fim:dd/MM/yyyy} ({sugestao.DiasCoberturaMax} dias de cobertura).");
        Console.WriteLine($"Pasta de saída: {options.OutputDirectory}");
        Console.WriteLine();

        var request = new ExtractionRequest
        {
            ConnectionString = connectionString,
            SugestaoId = sugestao.SugestaoId,
            DataInicial = janela.Inicio,
            DataFinal = janela.Fim,
            OutputDirectory = options.OutputDirectory,
        };

        var extracao = new ExtractionService().Run(request, new ConsoleProgress(), ct);
        if (extracao.IsFailed) return Falhar(extracao, options.StackTrace);

        var resultado = extracao.Value;
        Console.WriteLine();
        Console.WriteLine($"ZIP gerado: {resultado.ZipPath} ({resultado.ZipBytes / 1024d / 1024d:N1} MB)");
        foreach (var (arquivo, linhas) in resultado.RowsByFile) Console.WriteLine($"  {arquivo,-28} {linhas,12:N0} linhas");
        foreach (var aviso in resultado.Warnings) Console.WriteLine($"  AVISO: {aviso}");

        return CliExitCode.Sucesso;
    }

    /// <summary>
    /// A primeira linha é a mensagem de negócio; depois vem a metadata da falha
    /// (etapa, query, número SQL, duração), que é o que se cola num chamado. A pilha
    /// só sob pedido, porque ela sepulta o que interessa.
    /// </summary>
    private static int Falhar<T>(Result<T> resultado, bool comStackTrace)
    {
        var erro = resultado.Errors.OfType<ExtratorErro>().First();
        Console.Error.WriteLine(erro.Message);

        foreach (var (chave, valor) in erro.Metadata)
        {
            if (chave == ExtratorErro.ChaveDetalhe && !comStackTrace) continue;
            Console.Error.WriteLine($"  {chave}: {valor}");
        }

        if (!comStackTrace) Console.Error.WriteLine($"Rode de novo com {CliParser.FlagStackTrace} para ver a pilha de chamadas.");

        return CliExitCodeMap.De(erro);
    }
```

No `Execute`, crie o log e o config, mantenha o `try/catch` só de `OperationCanceledException`, e passe `ambiente.Config!` adiante:

```csharp
        var config = ambiente.Config!;
        var connectionString = ConnectionStringFactory.Build(config, ambiente.Senha);
        var log = new ExtratorLog(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            tela: Console.Error.WriteLine);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Cancelando...");
        };

        try
        {
            return options.Command == CliCommand.List
                ? Listar(options, config, connectionString, log, cts.Token)
                : Extrair(options, config, connectionString, log, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelado pelo operador — o ZIP parcial foi descartado.");
            return CliExitCode.Cancelado;
        }
```

Apague `AbrirConexao` e `MensagemDeFalha`: a conexão de teste existia para distinguir "não falei com o SQL Server" de "a extração quebrou", e `ConexaoErro` já faz essa distinção sem uma ida extra ao banco. Se `ExtractorCliTests` cobrir `MensagemDeFalha`, reescreva o caso contra `Falhar`/`CliExitCodeMap`.

Em `CliParser.HelpText`, corrija a linha de `--months-back` — ela agora vale só para `--list`:

```
              --months-back <n>       Quantos meses para trás procurar sugestões no PBS.
                                      Padrão: {MesesRetroativosPadrao}. Vale só para --list;
                                      --extract busca a sugestão pelo id.
```

- [ ] **Step 4: Rodar a suíte inteira**

Run: `dotnet test tests/CosmosPro.ML.DemandForCast.Extractor.Tests/CosmosPro.ML.DemandForCast.Extractor.Tests.csproj`
Expected: tudo verde.

- [ ] **Step 5: Commit**

```bash
git add -A CosmosPro.ML.DemandForCast.Extractor tests/CosmosPro.ML.DemandForCast.Extractor.Tests
git commit -m "refactor(extractor): derive the CLI exit code from the typed error"
```

---

### Task 9: Verificação contra o PBS real, versão e documentação

O único task que exige banco. Ele é o que prova que `FalhaBruta.De` e as queries continuam certas — as duas coisas que teste sem banco não cobre.

**Files:**
- Modify: `CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj` (`<Version>`)
- Modify: `README.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Publicar e listar contra a Natus, medindo**

```bash
dotnet build CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj -c Release --nologo
```

```powershell
$exe = "CosmosPro.ML.DemandForCast.Extractor\bin\Release\net10.0-windows\CosmosPro.ML.DemandForCast.Extractor.exe"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $exe -ArgumentList "--list","--env-prefix","NATUSFARMA_PBS_PROD_","--port","1435","--months-back","12","--tsv" `
     -Wait -PassThru -NoNewWindow -RedirectStandardOutput "lista.tsv" -RedirectStandardError "lista.err"
$sw.Stop(); "ELAPSED: $([math]::Round($sw.Elapsed.TotalSeconds,2))s  EXIT: $($p.ExitCode)"
(Get-Content lista.tsv | Measure-Object -Line).Lines
```

Expected: `EXIT: 0`, **menos de 5 segundos**, ~19.582 linhas (cabeçalho + sugestões). Antes desta mudança o mesmo comando com `--months-back 1` levava 146,8 s. Se passar de 30 s, **pare**: a query de cabeçalho é a única no caminho e ela mede 0,27 s no servidor — um tempo maior significa que o lote de contagens sobreviveu em algum lugar.

- [ ] **Step 2: Provar o `ConexaoErro` com a porta errada**

```powershell
Start-Process -FilePath $exe -ArgumentList "--list","--env-prefix","NATUSFARMA_PBS_PROD_","--months-back","1" `
  -Wait -PassThru -NoNewWindow -RedirectStandardError "porta.err" | Select-Object ExitCode
Get-Content porta.err
```

Expected: `ExitCode 3` (`FalhaDeConexao`) e a mensagem citando **porta** — é o caminho que fez 1433 passar por credencial inválida.

- [ ] **Step 3: Extrair de verdade, uma sugestão conhecida**

```powershell
Start-Process -FilePath $exe -ArgumentList "--extract","--suggestion-id","18172","--output",".\extracoes","--env-prefix","NATUSFARMA_PBS_PROD_","--port","1435" `
  -Wait -PassThru -NoNewWindow -RedirectStandardOutput "extracao.out" -RedirectStandardError "extracao.err" | Select-Object ExitCode
Get-Content extracao.out
```

Expected: `ExitCode 0`, um ZIP em `.\extracoes`, e as nove linhas de contagem por arquivo. A 18172 é a sugestão que rodou o fluxo inteiro em 2026-08-01 (ACHE RX, 365 itens, cobertura 5 dias). Confira que o arquivo de log do dia existe ao lado do `.exe` e **não** contém a senha:

```powershell
Select-String -Path "CosmosPro.ML.DemandForCast.Extractor\bin\Release\net10.0-windows\extrator-log-*.txt" -Pattern "Password=\*\*\*"
Select-String -Path "CosmosPro.ML.DemandForCast.Extractor\bin\Release\net10.0-windows\extrator-log-*.txt" -Pattern $env:NATUSFARMA_PBS_PROD_MSSQL_PASSWORD
```

Expected: o primeiro acha (ou não acha nada, se nenhuma linha logou connection string); o segundo **não acha nada**. Se o segundo achar, pare e corrija `Redigir`.

- [ ] **Step 4: Abrir o form e conferir as quatro promessas**

```powershell
Start-Process -FilePath $exe
```

Confira, com servidor `natusfarma.procfit.com.br`, porta 1435, banco e credenciais das env vars:

1. **Testar conexão** — inputs travam, rodapé mostra relógio, log diz "106 lojas ativas encontradas".
2. **Carregar sugestões** com 12 meses — volta em menos de 5 s com ~19.581 linhas; barra em Marquee durante a espera.
3. **Clicar numa sugestão viável** — a linha de informação mostra itens, lojas e janela; **Extrair** habilita.
4. **Cancelar durante Carregar sugestões** — o rodapé vira "Cancelado." e os inputs destravam.
5. **Copiar log** — o painel inteiro vai para a área de transferência.

- [ ] **Step 5: Subir a versão**

Em `CosmosPro.ML.DemandForCast.Extractor.csproj`: `<Version>0.15.0</Version>`. O `manifesto.json` publicado lê daqui, e a página da sessão mostra a versão — deixar 0.14.0 faria a web afirmar que o operador está com o binário antigo.

- [ ] **Step 6: Atualizar README.md e CLAUDE.md**

Em `README.md`, na seção do extrator, acrescente ao final:

```markdown
**Log do extrator.** Cada execução escreve `extrator-log-AAAA-MM-DD.txt` ao lado do
`.exe` (o painel da janela é o mesmo conteúdo, e o botão "Copiar log" leva tudo para a
área de transferência). A senha é redigida. Ao reportar um problema, é este arquivo
que interessa: ele leva a etapa, o arquivo `.sql`, o número do erro SQL e a duração.

**Catálogo de sugestões.** O carregamento traz só os cabeçalhos — 12 meses da Natus
(19.581 sugestões) em 0,27 s. As contagens de itens e lojas são buscadas para a
sugestão selecionada (0,01 s), porque pedi-las em lote custava 30 s por 500 ids
(`COUNT(DISTINCT FILIAL)` não é coberto pelo índice de `SUGESTAO_COMPRA`, em 124
milhões de linhas) — cerca de 20 minutos para 12 meses, mais do que a conexão até o
cliente sobrevive. Use o campo de filtro para achar a sugestão por id ou descrição.
```

Em `CLAUDE.md` §2, na lista de pacotes preferidos, acrescente:

```markdown
- **Resultado sem exceção (extrator):** `FluentResults` 4.0.0. A camada de serviço do
  extrator (`CatalogoService`, `ExtractionService`) devolve `Result<T>` com erros
  tipados (`ExtratorErros.cs`) que carregam etapa, arquivo `.sql`, número do erro SQL
  e duração. Exceção que escapa dessa camada é bug. Não propagar exceção para a UI e
  não usar `Result` para representar cancelamento — `OperationCanceledException`
  continua sendo o veículo do cancelamento.
```

- [ ] **Step 7: Commit**

```bash
git add CosmosPro.ML.DemandForCast.Extractor/CosmosPro.ML.DemandForCast.Extractor.csproj README.md CLAUDE.md
git commit -m "docs(extractor): record the log file, the instant catalog and the FluentResults boundary

Version 0.15.0: o manifesto.json publicado le daqui e a pagina da sessao mostra
a versao ao operador."
```

---

## Auto-revisão

**Cobertura do spec, seção por seção:**

| seção do spec | task |
|---|---|
| §3.1 quebra em três arquivos | 4 (`CatalogoService`), 5 (`ExtractionService`), 1 (`ExtratorErros`) |
| §3.2 `OperacaoUi` | 6 |
| §3.2 `ExtratorLog` | 3 |
| §4 inputs travados, Cancelar, relógio, Marquee, log com duração, Copiar log | 6 (mecanismo) + 7 (cablagem) |
| §5 erros tipados com metadata | 1 |
| §5 um ponto de tradução, catch por tipo | 4 (`Consultar`), 5 (catch único do `Run`) |
| §6 `ConnectRetryCount`/`Interval` | 4 |
| §6 retry só em leitura curta, com log | 2 (mecanismo) + 4 (uso) |
| §6 extração sem retry automático | 5 (não chama `Retentativa`) |
| §7 contagens na seleção, meses, filtro | 4 (serviço) + 7 (tela) |
| §8 timeouts configuráveis, extração em 0 | 4 |
| §9 CLI derivando exit code | 8 |
| §10 testes | 1, 2, 3, 4, 6, 8 |

**Consistência de tipos:** `Etapa` é record com `(Nome, QueryFile)` em todos os tasks; `ExtratorErro.Transitorio` é `virtual bool` lido por `Retentativa` (Task 2) e definido no Task 1; `CatalogoService.Carregar` devolve `Result<IReadOnlyList<SugestaoCatalogoCabecalho>>` e é consumido com esse tipo nos Tasks 7 e 8; `SugestaoLinha` ganha `Cobertura` no Task 7 e nenhum outro task a referencia.

**Ponte temporária declarada:** o Step 7 do Task 4 deixa `throw new InvalidOperationException(...)` no form e no CLI. Ela é morta nominalmente no Task 7 (form) e no Task 8 (CLI). Se a execução parar entre os tasks 4 e 8, o extrator compila e roda, mas a mensagem de erro perde a metadata — não deixe nesse estado.

**Fora do plano, de propósito** (§10 do spec): janela declarada além do último dia com dado, `GERA_DEMANDA`, manchete de sobra, cobertura de snapshots de estoque, auto-descoberta de porta.
