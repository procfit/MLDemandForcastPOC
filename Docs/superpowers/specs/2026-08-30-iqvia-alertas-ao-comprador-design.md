# F16 parte C — Alertas de mercado IQVIA ao comprador

| | |
|---|---|
| Data | 2026-08-30 |
| Origem do pedido | `CONTROLE DE DESENVOLVIMENTO E VALIDAÇÃO DO POC - MLDEMANDFORECAST.xlsx`, aba `REGRA DE NEGÓCIO IQVIA` (Julio) — itens 9 e 10 do acompanhamento, ambos marcados CRÍTICA |
| Depende de | F16 partes A e B (importação e ciclo de vida do dado de mercado) — ver README e CLAUDE.md §4 |
| Arquivo de referência analisado | `IQVIA_MES_junho2026.XLSX` — 52.837 linhas, 52.804 EANs, 3 bricks de Volta Redonda, meses 202506 e 202606 |

## 1. O que esta fase é, e o que ela não é

**É** uma camada de **diagnóstico** sobre a decisão de compra: o dado de mercado da
IQVIA passa a classificar e explicar itens para o comprador. Nenhuma previsão muda,
nenhuma feature entra no modelo, nenhum número do backtest se move.

**Não é** a entrada da IQVIA na conta do ML. O README descrevia a parte C como
*"features de mercado no `FeatureBuilder` (com corte anti-vazamento sobre `Mes`) … e
backtest walk-forward contra o baseline sem mercado"*. Isso continua válido como
trabalho futuro e passa a se chamar **Caminho 2**; esta spec é o **Caminho 1**.

**Por que nesta ordem.** Os itens 9 e 10 do controle são os marcados como críticos e são
alertas, não previsão. O Caminho 1 é medível pelo questionário que a F15 já instalou. E
ele constrói as duas pontes — EAN↔cadastro e CNPJ↔brick — que o Caminho 2 precisaria de
qualquer forma: está no caminho crítico, não em paralelo. O ganho do Caminho 2 é
incerto por descasamento de grão (IQVIA é mensal por brick; a previsão é diária por
loja), e só um backtest responde.

### 1.1 Regras no corte

| Regra | Enunciado do documento, resumido | Onde aparece |
|---|---|---|
| A1 | Produto vende no brick e não existe no cadastro da rede | Tela nova `/mercado/oportunidades` |
| A2 | Filtrar os ausentes por relevância comercial | idem (parâmetro de tela) |
| A3 | Apresentar como oportunidade de sortimento | idem |
| B1 | Comparar desempenho da rede × mercado no brick | Colunas em `/comparacoes/{id}` |
| B2 | Participação >50% abaixo do mercado dispara alerta | idem |
| B3 | Primeira hipótese: ruptura / baixa disponibilidade | idem |
| B6 | Nenhuma hipótese explica → encaminhar ao comprador | idem |

### 1.2 Regras fora do corte, e por quê

**IQVIA-B4 (desvio de preço) está diferida por impossibilidade no dado, não por
prioridade.** Medição no arquivo real de junho/2026: `Real CPP ÷ Unidades` é
**idêntico** entre bandeiras no mesmo brick e mês (37.410 pares, zero diferença) e
**idêntico** entre bricks na mesma bandeira e mês (55.979 pares). Varia só quando muda o
mês. Logo `Real CPP = Unidades × P(EAN, mês)`: um preço de referência único, normalizado
pela metodologia da IQVIA. **O arquivo não contém preço praticado por concorrente.** B4
devolveria sempre um número, e o número não significaria o que a regra promete — um item
nosso poderia ser apontado como "caro" porque a referência é normalizada, não porque o
vizinho cobra menos. O fato já está registrado no XML doc de
`MercadoObservacao.ValorCpp` e em CLAUDE.md §4.

Se B4 voltar, é como **"nosso preço praticado × referência IQVIA"**, com esse rótulo
literal na tela — nunca como "o concorrente está mais barato", que exigiria outra fonte
de dados.

**IQVIA-B5 cai junto:** ela existe só para calcular o preço médio que B4 ia comparar.
Sem B4, não tem consumidor.

**A coluna de preço que B1 pede fica fora.** Mostrar nosso preço praticado ao lado do
índice da IQVIA é o mesmo erro de B4 sem a luz vermelha: o comprador olha as duas
colunas e conclui sozinho. B1 exibe unidades, valor e participação.

## 2. Onde cada metade aparece, e por que os dois lugares são diferentes

O grupo B é **materializado**; o grupo A é **consulta ao vivo**. A assimetria não é
preferência: ela decorre de onde vivem os dados de entrada.

**Grupo B depende do Stage**, que é apagado inteiro a cada import (`CargaProcessor`,
`DELETE ... WHERE RedeId`): precisa de `Stage.Vendas`, `Stage.EstoquesDiarios` e
`Stage.Lojas.Cnpj`. É exatamente o motivo pelo qual `ComparacaoSessaoItens` já existe
como tabela e não como consulta (CLAUDE.md §4, "O resultado da sessão é materializado,
nunca recalculado"). Calcular o alerta na abertura da tela faria a sessão de hoje
mostrar tela vazia depois do próximo ZIP — ou, pior, mostrar o mercado do envio seguinte
com a cara do anterior.

→ **Colunas novas em `ComparacaoSessaoItens`**, gravadas por
`SessaoResultadoMaterializador` na mesma transação do `DELETE` + `SqlBulkCopy` +
`UPDATE Status`, na última volta em que a sessão ainda está em `Comparando`. **Nenhuma
fase nova na máquina de estados**, nenhuma fila nova.

**Grupo A depende só do banco `engine`**: `MercadoObservacoes`, `MercadoBrickPdvs` e o
cadastro de EANs da rede (§6.2). Os três sobrevivem aos imports.

→ **Tela nova `/mercado/oportunidades`**, consulta ao vivo, sem materialização, não
atrelada a nenhuma sessão. É a resposta a "o que devo incluir no mix agora", que não tem
dono numa sessão de comparação — e o item, por definição, não tem linha na tabela de
itens, porque não está na sugestão.

## 3. A régua do B2

**Definição adotada:**

```
índice = (nossa fatia de unidades neste EAN, neste brick, neste mês)
       ÷ (nossa fatia de unidades agregada, neste brick, neste mês)
```

Alerta quando `índice < 0,5` — a leitura literal de "mais de 50% abaixo" do documento.
Frase para o comprador: *"neste item você captura menos da metade do que normalmente
captura neste bairro"*.

**Por que não a régua por número de lojas.** A formulação mais natural seria `fatia real
÷ fatia esperada pelo número de PDVs` — mas o contador de PDVs concorrentes (37, 28, 36
no arquivo real) **não está no banco**. Ele existe na planilha apenas numa área de tabela
dinâmica abaixo da aba de PDVs, e `IqviaXlsxParser.LerAbaDePdvs` a ignora de propósito
(comentário no método): o regex `bandeira - cnpj` não casa com "Rótulos de Linha".
`MercadoBrickPdvs` guarda 7 linhas próprias e **uma** linha de concorrentes, com CNPJ
`00000000000000` — contar linhas dá 7 e 1, não 7 e 37.

| régua | mediana | dispara | precisa de dado novo |
|---|---|---|---|
| fatia real ÷ fatia esperada por nº de PDVs | 1,57 | 8,5% (1.621 de 18.989) | **sim** |
| fatia no item ÷ nossa fatia média no brick | 1,13 | 15,0% (2.850 de 18.989) | não |

A segunda também controla melhor a variável estrutural: o tamanho da rede no brick está
no numerador e no denominador, e cancela. O que sobra é o efeito do produto, que é o que
B2 quer isolar.

**Melhoria separada, não bloqueante:** capturar o contador de PDVs por brick. Vale pelo
contexto na tela ("7 das 44 lojas do bairro são suas") e para o `1,57` virar número do
sistema. O caminho robusto **não** é raspar a tabela dinâmica — layout de pivot montado
por analista, com rótulos localizados, não é contrato — e sim pedir ao fornecedor do
relatório uma coluna de contagem na aba de PDVs. Enquanto não houver, o `1,57` é
apuração manual, citável na dissertação, fora do software.

## 4. Qual mês da IQVIA é usado

**Grupo B (dentro da sessão): o último mês coberto estritamente anterior ao mês da
sugestão.** Uma regra, sem caso especial.

- Hoje, com só o arquivo de junho/2026 carregado e sugestão de junho/2026, resolve para
  **junho/2025** — o espelho do ano anterior que todo arquivo traz, e que por
  consequência é sazonalmente casado.
- Conforme a rede empilha arquivos mensais, passa a maio/2026 sem mudança de código.

**Por que estritamente anterior.** O mês da sugestão contém as consequências da própria
sugestão. Para um diagnóstico retrospectivo isso passaria; para a afirmação que a
dissertação quer sustentar — *"o alerta da IQVIA teria avisado o comprador"* — é
circular. O atraso real da fonte confirma a regra: o arquivo de junho/2026 chegou em
agosto/2026, então em junho o comprador não tinha junho.

**Grupo A (fora da sessão): o mês mais recente coberto.** Pergunta diferente — "o que
devo incluir agora", não "o que o comprador poderia ter sabido então". Sem sugestão
pontuada, não há o que vazar.

O mês efetivamente usado é **gravado por linha** (`MercadoMes`) e exibido na tela. Sem
isso o comprador não sabe contra o que está sendo comparado, e a sessão perde a
declaração no momento em que a cobertura mudar.

**Cobertura, não ausência de linha.** Só células com medida ≠ 0 viram linha em
`MercadoObservacoes` (~72% do arquivo real é zero). Dentro de um `(mês, brick)` coberto,
ausência de linha significa **venda zero**; fora, significa **não enviado**. Quem separa
os dois é a cobertura já existente (`GET /api/mercado/cobertura` e o `ResumoJson` da
carga) — a resolução do mês tem de consultá-la, nunca inferir cobertura da existência de
linhas.

## 5. Vocabulário do alerta

`MercadoAlerta`, gravado como texto:

| valor | significado |
|---|---|
| `null` | **não avaliado** — falta dado de mercado para este item (§6.1) |
| `SemAlerta` | avaliado, índice ≥ 0,5 |
| `Ruptura` | índice < 0,5 **e** houve dias sem estoque no mês comparado (B3) |
| `SemCausa` | índice < 0,5 **e** não houve dias sem estoque no mês comparado (B6) |
| `NaoApurado` | índice < 0,5, mas o estoque daquele mês não está no histórico importado |

`NaoApurado` existe porque *"não consegui verificar o estoque"* não é *"não houve
ruptura"*. Colapsar os dois mandaria o item para `SemCausa` afirmando algo que ninguém
verificou — o mesmo erro que a tabela já evita ao não gravar zero no braço de ML.

`SemAlerta` existe (em vez de `null` para "está tudo bem") para que a coluna diga sozinha
se houve avaliação, sem exigir leitura cruzada de `MercadoIndiceDesempenho`.

## 6. Modelo de dados

### 6.1 Colunas novas em `ComparacaoSessaoItens`

Migration: `AddMercadoNoItemDaSessao`. Sem backfill — o dado de mercado de uma sessão
antiga descreveria o Stage do envio atual, o mesmo motivo pelo qual `Categoria` entrou
sem backfill.

| coluna | tipo CLR | precisão / tamanho | conteúdo |
|---|---|---|---|
| `MercadoMes` | `DateOnly?` | — | primeiro dia do mês da IQVIA usado |
| `MercadoBrick` | `string?` | `HasMaxLength(80)` | brick da loja, resolvido por CNPJ |
| `MercadoUnidadesRede` | `decimal?` | `(15,3)` | espelha `MercadoObservacao.Unidades` |
| `MercadoUnidadesConcorrentes` | `decimal?` | `(15,3)` | idem |
| `MercadoIndiceDesempenho` | `decimal?` | `(9,4)` | a régua do §3 |
| `MercadoDiasSemEstoque` | `int?` | — | dias em ruptura **no mês comparado** (B3) |
| `MercadoAlerta` | `string?` | `HasMaxLength(20)` | vocabulário do §5 |

As precisões são declaradas porque o default do EF Core é `decimal(18,2)` e truncaria em
silêncio. `(9,4)` no índice cobre 5 dígitos inteiros — o teto teórico é `1 ÷ fatia
agregada`, e fatia agregada de 1% já daria 100.

**Nulo significa "não foi possível calcular", nunca zero.** É o contrato que a tabela já
tem para o braço de ML, e vale igual aqui. Motivos de nulo, todos legítimos:

1. loja sem `Cnpj` no Stage (ZIPs anteriores à F16, ou cadastro sem CGC);
2. CNPJ da loja não aparece em `MercadoBrickPdvs` (loja fora do painel);
3. SKU sem `Ean` no Stage;
4. EAN não existe em `MercadoObservacoes` (produto fora do painel da IQVIA);
5. nenhum mês coberto anterior ao mês da sugestão.

**Zero é medição, e é o alerta mais forte que existe.** `MercadoUnidadesRede = 0` com
`MercadoUnidadesConcorrentes > 0` num recorte coberto significa: o item está no nosso
cadastro, está na sugestão, o bairro vende, e nós vendemos nada. O índice é 0 e o alerta
dispara. Não confundir com nulo em nenhuma ponta — montador, `DataTable` do bulk, DTO ou
Razor.

### 6.2 Tabela nova `RedeCatalogoEans` (banco `engine`)

Migration: `AddRedeCatalogoEans`.

| coluna | tipo | nota |
|---|---|---|
| `RedeId` | `int` | FK para `Redes`, `Restrict` |
| `Ean` | `varchar(14)` | só dígitos; parte da PK |
| `Sku` | `nvarchar(30)` | código do ERP, para a tela poder citar |
| `Nome` | `nvarchar(200)?` | conveniência de exibição |

PK `(RedeId, Ean)`. Substituição por `RedeId` inteiro a cada envio de catálogo — é
retrato do cadastro, não série histórica.

**Por que no `engine` e não no Stage.** O extrator vai passar a trazer o cadastro
(decidido). Se o CSV novo só populasse `Stage.Produtos`, a tela de oportunidades zeraria
no próximo import, porque `/mercado/oportunidades` não pertence a sessão nenhuma. O
`CargaProcessor` carrega o CSV no Stage como os demais **e** faz o upsert em
`RedeCatalogoEans` — mesmo ciclo de vida do dado de mercado, pelo mesmo motivo dele.

**Por que o cadastro completo é obrigatório para A1.** `produtos.csv` é escopado aos SKUs
da sugestão (comentário em `Queries/produtos.sql`: 79.749 produtos da rede viravam
79.749 linhas para uma sugestão de 1.695 SKUs). Contra um `Stage.Produtos` escopado, "EAN
ausente" significa *"não está nesta sugestão"*, não *"não está no cadastro"* — e A1
apontaria como oportunidade de sortimento um item já cadastrado. Sem o catálogo
completo, o grupo A **não roda**; a tela declara isso em vez de exibir lista vazia.

### 6.3 Contrato do CSV novo

`catalogo_eans.csv`, **arquivo opcional** no ZIP (como `sinais_externos.csv`), header
`Sku,Ean,Nome`. Uma linha por produto do mestre, sem venda nenhuma. ZIPs anteriores
seguem válidos — entrada desconhecida no ZIP nunca foi validada. Entra na versão nova do
extrator já prevista (linha 17 do documento de controle).

## 7. Joins e normalização

**Loja → brick:** `Stage.Lojas.Cnpj` (`CHAR(14)`, só dígitos, populado de
`ENTIDADES.CGC`) ↔ `MercadoBrickPdvs.Cnpj`. Um CNPJ aparece em um brick, então o
mapeamento é de valor único. Loja sem CNPJ ou fora do painel → colunas de mercado nulas.

**SKU → EAN:** `Stage.Produtos.Ean` (`VARCHAR(14)`, vem de `PRODUTOS_EAN` com preferência
por `EAN_PRINCIPAL` e não-interno) ↔ `MercadoObservacoes.Ean`.

**A convenção de comprimento não está resolvida, e é risco real.** No arquivo da IQVIA:
51.070 EANs de 13 dígitos, 1.053 de 12, 529 de 11, 148 de 8, 3 entre 9 e 10 — e 34 linhas
**sem EAN**, descartadas no parse. A convenção do `EAN_FORMATADO` do PBS não foi medida.
Regra provisória: comparar só-dígitos exato, com tentativa secundária de zero-padding à
esquerda até 13. **A regra definitiva sai da medição do §9, não desta spec.**

**Escopo por inquilino, como nas demais telas:** `RedeId` nunca vem de rota, query ou
arquivo — sai do `IRedeContext`. `ComparacaoSessaoItens` não tem `RedeId` (o escopo é
transitivo pela FK da sessão), então todo endpoint que a ler junta o pai e filtra pelo
inquilino num único round-trip, e responde **404, não 403**, para sessão de outra rede.

## 8. Superfície de UI e API

### 8.1 Grupo B — tabela de itens da sessão

`TabelaItensComparacao.razor` ganha as colunas de mercado e um filtro **"só itens com
alerta"**. O filtro passa pela **mesma cláusula** que já unifica lista, contagem,
totalizadores e Excel (`ComparacoesEndpoints.AplicarFiltros`) — se divergisse, o
comprador veria N itens na tela e um total apurado sobre outro conjunto. As colunas novas
entram também em `ComparacaoItensExcelExporter`, e a aba de capa declara o mês da IQVIA
usado.

Linha sem dado de mercado é sinalizada como tal, não renderizada como zero — mesma
exigência que `JanelaAlemDoHistorico` e as colunas do braço de ML já impõem.

### 8.2 Grupo A — tela de oportunidades

`GET /api/mercado/oportunidades`, paginado, com filtros combináveis por brick, área da
farmácia e corte de relevância. Tela `/mercado/oportunidades`, alcançável de `/mercado`.

**Corte de relevância (A2), medido em junho/2026:**

| corte | avisos (EAN × brick) | EANs distintos |
|---|---|---|
| sem filtro | 44.874 | — |
| ≥ 1 un/loja-concorrente/mês | 2.135 | 1.403 |
| **≥ 5 un/loja-concorrente/mês (padrão)** | **202** | **137** |
| ≥ 10 un/loja-concorrente/mês | 59 | 44 |

95% da "oportunidade" bruta é cauda que vende menos de uma unidade por loja por mês. O
padrão é **≥ 5**, ajustável na tela; não é constante escondida no código. A2 não é
refinamento: sem ela a regra é inutilizável, não apenas ruidosa.

**O corte "por loja concorrente" não é computável hoje**, porque o divisor é o contador
de PDVs que não está no banco (§3). Até ele existir, o parâmetro é **unidades absolutas
do agregado de concorrentes no brick**, calibrado para render uma lista do mesmo tamanho:

| corte absoluto | pares | EANs |
|---|---|---|
| ≥ 50 | 1.331 | 899 |
| ≥ 150 | 250 | 172 |
| **≥ 200 (padrão provisório)** | **156** | **116** |
| ≥ 300 | 72 | 56 |

`≥ 200` é o padrão provisório: ele emoldura o alvo de 202 pares / 137 EANs do corte
`≥ 5 un/loja`. O rótulo da tela diz "unidades no bairro", não "por loja", enquanto for
assim.

**A distorção conhecida é do corte absoluto, e é o motivo de ele ser provisório:** ele
não normaliza pelo tamanho do painel, então favorece o brick com mais lojas
concorrentes — 526 tem 37 e 527 tem 28. Quando o contador de PDVs entrar, o parâmetro
volta a ser por loja e a distorção sai.

## 9. Validação obrigatória antes de construir tela

**Medir a taxa de casamento EAN↔cadastro contra o cadastro real da Retiro.** É a primeira
tarefa do plano de implementação, antes de qualquer UI. Se o casamento vier baixo, A1
nasce como lista de falsos positivos e a tela mente para o comprador. A medição decide a
regra de normalização do §7 e informa se o grupo A é viável neste corte.

Saída esperada da medição: percentual de EANs do cadastro que casam com a IQVIA,
percentual de EANs da IQVIA que casam com o cadastro, e a distribuição dos que falham por
comprimento.

## 10. Limitações declaradas

- **O grupo B é escopado aos SKUs da sugestão**, porque `vendas.csv` e
  `estoques_diarios.csv` também são. Para "enriquecer a sugestão de compra" esse é o
  recorte certo; não confundir com "toda a rede".
- **Não existe preço de concorrente** nesta fonte (§1.2). Nenhuma tela deve sugerir o
  contrário.
- **Enquanto a rede tiver um único arquivo carregado**, o grupo B compara contra o mesmo
  mês do ano anterior. É metodologicamente defensável e sazonalmente casado, mas não é a
  leitura mais recente do mercado — a tela diz qual mês usou.
- **Bricks pedidos no filtro da consulta IQVIA que não geram coluna não existem para o
  sistema.** O arquivo real pede 4 bricks e entrega 3; a cobertura vem das colunas
  presentes, não do filtro.

## 11. Fora de escopo (registrado para não voltar como esquecimento)

- Caminho 2 — IQVIA como feature no `FeatureBuilder`, com corte anti-vazamento sobre
  `Mes`, cobertura materializada no treino e backtest walk-forward contra o baseline sem
  mercado.
- IQVIA-B4 e IQVIA-B5 (§1.2).
- Coluna de preço na comparação B1 (§1.2).
- Captura do contador de PDVs por brick (§3).
- Item 11 do controle (clima, sazonalidade, epidemiológico). `Stage.SinaisExternos`
  existe, é opcional no import, **o extrator não o gera** e nada o consome — mesmo estado
  em que a `MercadoIqvia` antiga estava antes da F16.
