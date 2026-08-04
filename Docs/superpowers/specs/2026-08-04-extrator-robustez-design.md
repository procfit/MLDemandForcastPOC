# Extrator — honestidade na UI e robustez — design

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe `.md` de planejamento/decisão.
> Gravado a pedido explícito do usuário, seguindo o precedente desta pasta.

**Objetivo:** o extrator precisa ser um executável que o comprador da farmácia opera
sozinho, num terminal do cliente, sem ninguém do lado. Isso exige que a tela diga a
verdade sobre o que está acontecendo, que toda espera seja cancelável, e que uma
falha deixe rastro suficiente para ser diagnosticada sem reproduzir.

**Quem é o usuário:** o operador do PBS (hoje, na prática, o próprio desenvolvedor
rodando com dado real da Natus Farma). Ele não tem acesso ao código, não vê console,
e a única coisa que ele pode reportar é o que a tela mostrou.

---

## 1. O que aconteceu

O usuário conectou ao PBS da Natus, clicou em **Carregar sugestões**, e o rodapé
escreveu "Carregando sugestões..." para sempre. A lista nunca apareceu.

Não era travamento. Era uma espera de **cerca de 20 minutos**, sem relógio, sem
progresso e sem botão de desistir.

### Medições na instância real (Natus, `SUGESTOES_COMPRAS_RESULTADO` = 124.029.991 linhas)

| etapa | tempo |
|---|---|
| cabeçalhos do catálogo, 12 meses (19.581 sugestões) | **0,27 s** |
| contagens, 1 lote de 500 ids — query atual | **30,25 s** |
| contagens, o mesmo lote sem `COUNT(DISTINCT FILIAL)` | **1,56 s** |
| contagens de **uma** sugestão | **0,01 s** |
| escopo (lojas + SKUs) de uma sugestão | **0,01 s** |

12 meses = 19.581 ids = 40 lotes de 500 × 30 s ≈ **20 min**. Confirmado ponta a
ponta pelo CLI: `--list --months-back 1` (1.797 sugestões, 4 lotes) levou **146,8 s**.

### Causa

`COUNT(DISTINCT R.FILIAL)`. O índice `ixfk_SUGESTOES_COMPRAS_RESULTADO_SUGESTAO_COMPRA`
cobre `SUGESTAO_COMPRA`, mas **não** `FILIAL` — então cada linha do lote exige um
lookup na clustered. `COUNT(*)` é servido inteiramente pelo índice, e é 20× mais
rápido. A otimização registrada em `perf(extractor): split suggestion catalog into
header and count queries` resolveu o join por faixa de datas, mas manteve a agregação
caríssima do lado das contagens.

### Por que o sintoma foi "travou" e não "está lento"

Quatro escolhas de código se somaram:

1. `CommandTimeout = 0` em toda consulta do serviço — espera infinita por contrato.
2. `MainForm.CarregarSugestoesAsync` passa `CancellationToken.None`
   ([MainForm.cs:172](../../../CosmosPro.ML.DemandForCast.Extractor/MainForm.cs#L172)) —
   o serviço registra `ct.Register(command.Cancel)`, mas recebe um token que nunca
   cancela.
3. O botão **Cancelar** só é habilitado na rota de extração.
4. `ProgressBar` parada em zero e `_status` com texto fixo: a tela **afirma** que
   nada está acontecendo.

Nada disso é específico do catálogo. É o padrão de toda operação do form, e é o que
este design corrige.

### Achado lateral

A variável de ambiente `NATUSFARMA_PBS_PROD_MSSQL_PORT` não existe; o CLI cai no
padrão 1433, onde algo responde e o logon falha com *"Não é possível abrir o banco de
dados ... Falha de logon"* — mensagem que não menciona porta. A instância real está
na **1435** (é o que o `.mcp.json` usa). O erro de conexão precisa sugerir conferir
a porta.

## 2. Decisões tomadas

| Questão | Decisão | Por quê |
|---|---|---|
| Contagens Linhas/Lojas no grid | **Sob demanda, na seleção** | O catálogo carrega em 0,27 s; a contagem da sugestão escolhida custa 0,01 s. A informação não se perde, ela passa a aparecer para a única linha que interessa. |
| Fronteira do `Result` | **Camada de serviço inteira, extração incluída** | Dois estilos de erro na mesma classe é o que produz o `catch (Exception)` que engole contexto. Com a fronteira inteira, exceção que escapa passa a significar bug. |
| Log | **Arquivo + botão copiar** | O operador roda no terminal do cliente. Log que morre com o form transforma toda falha em "não sei, deu erro". |
| Timeout da extração | **Continua 0 (ilimitado)** | Varrer dezenas de milhões de linhas é a natureza da operação, não um sintoma. O que muda é cancelamento que responde e progresso que anda. |
| Timeout das consultas de catálogo | **Limitado e configurável** | Ali a espera longa é sempre bug ou rede ruim; silêncio infinito não ajuda ninguém. |

## 3. Arquitetura

### 3.1 Quebra de `ExtractionService.cs` (720 linhas) em três

O arquivo faz dois trabalhos com necessidades de erro opostas: ler catálogo (rápido,
sem efeito colateral, timeout curto) e extrair (longo, escreve ZIP, timeout
ilimitado, precisa apagar o parcial). Separar é o que deixa a fronteira `Result`
legível.

| arquivo | responsabilidade | assinatura pública |
|---|---|---|
| `CatalogoService.cs` (novo) | Cabeçalhos do catálogo, contagens de **uma** sugestão, cabeçalho por id, lojas do teste de conexão. | `Result<IReadOnlyList<SugestaoCatalogo>>`, `Result<SugestaoContagem>`, `Result<SugestaoCatalogo?>`, `Result<IReadOnlyList<LojaOption>>` |
| `ExtractionService.cs` | Só a extração e o ZIP. | `Result<ExtractionResult> Run(...)` |
| `ExtratorErros.cs` (novo) | Erros tipados + tradutor `Exception` → erro tipado. Consumido pelo form e pelo CLI. | ver §5 |

`ExtractionStepException` deixa de ser o veículo de falha e vira detalhe interno do
tradutor: a informação que ela carrega (nome da etapa, arquivo `.sql`) passa a viver
no `Metadata` do erro, onde o CLI e o log conseguem ler campo a campo em vez de
raspar texto de mensagem.

### 3.2 Dois arquivos novos de infraestrutura

**`OperacaoUi.cs`** — o escopo que sustenta a honestidade da tela. Uma operação =
um `using`:

```
using var operacao = OperacaoUi.Iniciar(_ui, "Carregando sugestões", ProgressoDesconhecido);
var resultado = await operacao.ExecutarAsync(ct => catalogo.Carregar(cs, dataInicio, ct));
```

Ele é responsável por: travar os inputs, habilitar **Cancelar**, criar o
`CancellationTokenSource`, ligar o cronômetro de 1 s, escolher `Marquee` ou
`Continuous`, e no `Dispose` devolver tudo ao estado anterior — inclusive se a
operação morrer. Deliberadamente pequeno: não há harness de WinForms neste repo,
então ele carrega o mínimo de lógica testável e nada de regra de negócio.

**`ExtratorLog.cs`** — sink duplo. Toda linha que vai ao painel do form vai também
para `extrator-log.txt`, ao lado do `.exe`, um arquivo por dia
(`extrator-log-2026-08-04.txt`), append, com **senha e connection string redigidas**.
O arquivo recebe o detalhe completo da exceção (`ex.ToString()`); o painel recebe o
resumo. Falha ao escrever o log **não** derruba operação nenhuma — mesma política do
`AppConfig.Save()`.

## 4. Honestidade na UI — comportamento exigido

Vale para **toda** operação longa (testar conexão, carregar catálogo, contar itens da
seleção, extrair):

- **Inputs travados de verdade.** Não só os três botões de hoje: grupo de conexão,
  grid, pasta de saída, campo de meses. Um `Dispose` restaura, e o estado do botão
  **Extrair** volta a ser decidido pela seleção corrente — nunca habilitado
  incondicionalmente.
- **Cancelar habilitado sempre**, com token real chegando ao `SqlCommand`.
  `OperationCanceledException` continua sendo desfecho legítimo, não falha.
- **Relógio no status:** `"Carregando sugestões… 12s"`, atualizado a cada segundo.
  É o que distingue lento de travado sem código-fonte na mão.
- **ProgressBar coerente:** `Marquee` quando não há total conhecido (conexão,
  catálogo, contagem), `Continuous` na extração, que sabe 1/9..9/9. A barra nunca
  fica parada em zero durante trabalho real.
- **Log com início, fim e duração por etapa**, e desfecho com número:
  `"19.581 sugestões em 0,3s"`.
- **Botão "Copiar log"**, que coloca o painel inteiro no clipboard.

## 5. Erros tipados

`Result<T>` do **FluentResults 4.0.0** (MIT). Traz duas dependências transitivas
(`Microsoft.Extensions.Logging.Abstractions`, que já chega hoje pelo
`Microsoft.Data.SqlClient`, e `System.Threading.Tasks.Extensions`, fachada vazia no
.NET moderno). Sem TFM `net10.0`; resolve os ativos `net9.0`, o que é o normal.

Todo erro deriva de um `ExtratorError` comum e carrega `Metadata`: `etapa`,
`queryFile`, `sqlNumber`, `duracao`, `tipoInterno`. O arquivo de log grava o
`Metadata` inteiro; a `MessageBox` mostra a mensagem de negócio.

| erro | quando | o que a mensagem acrescenta |
|---|---|---|
| `ConexaoError` | conexão não abriu | **conferir servidor E porta** — a mensagem crua do SQL Server não menciona porta, e foi assim que a 1433 vs 1435 passou por logon inválido |
| `LogonTriggerError` | `SqlException.Number == 17892` | mantém a dica de `ApplicationName` em `extrator.config.json` |
| `TempoExcedidoError` | timeout limitado estourou | nome da query e o limite que foi excedido |
| `EtapaError` | falha dentro de etapa nomeada | etapa + arquivo `.sql` + tipo interno |
| `ContratoError` | `EnsureShape` reprovou | qual coluna divergiu do header do Stage |
| `SugestaoNaoEncontradaError` | id inexistente ou sem `TIPO_CALCULO` | manda conferir com `--list` |
| `SugestaoSemItensError` | sugestão sem linhas em `..._RESULTADO` | nada para extrair |
| `JanelaInviavelError` | cobertura ainda não terminou | data-limite (texto de `ExtractionWindow`) |
| `EscritaError` | I/O do ZIP | disco cheio, pasta sem permissão, arquivo travado por antivírus |
| `InesperadoError` | qualquer outra | tipo + mensagem na tela, `ex.ToString()` no arquivo |

### Onde o `try/catch` fica

**Um** ponto de tradução por operação de serviço, capturando **por tipo**:
`SqlException` (com mapa por `Number`), `InvalidCastException` (coluna sem `CONVERT`
— e aponta qual query), `IOException`/`UnauthorizedAccessException` (ZIP),
`InvalidOperationException` (contrato). `catch (Exception)` existe **só** na borda
externa, e lá ele registra tudo em arquivo em vez de resumir. Não há `catch` no meio
do caminho.

`OperationCanceledException` nunca é convertida em erro: cancelamento é desfecho, e
os dois modos já o distinguem (na UI pelo status, no CLI pelo exit code).

## 6. Catálogo

`catalogo_sugestoes_contagens.sql` sai do caminho de carregamento. Ela passa a ser
chamada para **uma** sugestão, na seleção do grid, e o resultado aparece ao lado da
janela derivada:

> 365 itens · 1 loja · janela de dados 14/06/2025 a 19/06/2026

As colunas Linhas e Lojas saem do grid. Duas adições que o carregamento instantâneo
torna necessárias — sem elas a tela fica pior do que era, porque 19.581 linhas de uma
vez não se navega com scroll:

- **Campo "meses retroativos"** (`NumericUpDown`, padrão 12), hoje uma constante
  privada. Persistido em `extrator.config.json`.
- **Filtro por descrição ou id**, aplicado em memória sobre o catálogo já carregado
  (nenhuma ida extra ao banco).

Se a contagem da seleção falhar ou estourar o timeout, a linha de informação diz isso
e a extração **continua permitida**: contagem é conforto do operador, não
pré-condição.

## 7. Timeouts

| operação | hoje | proposto | chave em `extrator.config.json` |
|---|---|---|---|
| abrir conexão | 15 s | 15 s | `TimeoutConexaoSegundos` |
| lojas (teste de conexão) | 30 s (padrão do provider, nunca declarado) | 30 s, declarado | `TimeoutConsultaSegundos` |
| cabeçalhos do catálogo | **0** | 30 s | `TimeoutConsultaSegundos` |
| contagens da seleção | **0** | 15 s | `TimeoutContagemSegundos` |
| extração (todas as queries) | 0 | **0, deliberado** | — |

O 0 da extração fica fora da configuração de propósito: um timeout ali só produziria
falha no meio de um ZIP que estava indo bem. Quem interrompe extração é o operador,
pelo botão.

## 8. CLI

`CliExitCode` passa a ser **derivado** do erro tipado, num único mapa, em vez de
escolhido dentro de cada `catch`. Hoje o form e o CLI podem discordar sobre o que
aconteceu porque cada um interpreta a exceção por conta própria; com o mapa único, o
código de saída e o texto da tela vêm da mesma fonte. `--list` fica instantâneo pelo
mesmo caminho do form. `MensagemDeFalha` passa a formatar erro tipado, mantendo
`--stack-trace`.

## 9. Testes

Os 138 testes existentes continuam passando; nenhum contrato de CSV, ZIP, manifesto
ou janela muda. Novos, todos sem banco:

- `SqlException.Number` → erro tipado (17892 → `LogonTriggerError`, timeout → `TempoExcedidoError`)
- erro tipado → `CliExitCode`, um caso por erro
- `Result` de falha preserva `Metadata` (etapa, `queryFile`)
- redação de senha e connection string no log
- append e troca de arquivo por dia no `ExtratorLog`
- filtro do catálogo em memória (descrição e id)
- `MesclarCatalogo` continua valendo com contagens ausentes (a sugestão sem linhas
  em `..._RESULTADO` — id 17658 na instância real — não pode sumir da lista)

## 10. Fora de escopo

Registrado para não parecer esquecimento:

- **A janela declarada excede o dado extraído.** O ZIP declara `JanelaFim` além do
  último dia com venda, e a comparação recusa os itens (bloqueador #2 do teste com a
  Natus, 01/08/2026). É semântica de dado, não robustez de UI.
- **`GERA_DEMANDA`** e o filtro de o-que-conta-como-demanda em `vendas.sql`.
- **A manchete que culpa a compra pelo estoque preexistente.**
- **Cobertura de 36% dos snapshots de estoque.**
- **Auto-descoberta de porta ou instância do SQL Server.** O que entra é a *dica* no
  `ConexaoError`, não a descoberta.
