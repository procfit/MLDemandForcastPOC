# Sessões de Comparação — design

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe `.md` de planejamento/decisão.
> Gravado a pedido explícito do usuário, seguindo o precedente desta pasta.

**Objetivo:** dar ao operador de compras do PBS um fluxo em que ele compara, uma
sugestão por vez, o que o ERP mandou comprar com o que o ML diria — e vê a diferença
em linguagem de negócio.

**Quem é o usuário:** comprador de rede de farmácia. Opera a sugestão de compra no
PBS. Não sabe nada de ML e não precisa saber.

---

## 1. Problema

O app hoje tem quatro páginas técnicas desconectadas (`/`, `/dados`, `/treinamento`,
`/sugestao-compra`). Nenhuma responde à pergunta que o comprador tem: *"posso confiar
na sugestão que o sistema me deu?"*. O usuário navega pelas etapas da nossa
engenharia, não pelo trabalho dele.

Há também uma lacuna física no processo: para comparar, alguém precisa ir ao ERP,
rodar o extrator e voltar com um ZIP. Isso pode levar dias. O app precisa modelar
essa espera, não ignorá-la.

## 2. Decisões tomadas

| Questão | Decisão | Por quê |
|---|---|---|
| A que a comparação fica ancorada | **A uma sugestão específica do PBS** | É a compra que o comprador reconhece e viveu. Ele compara uma por uma. |
| Onde a sugestão é escolhida | **No extrator** | A web não tem acesso ao PBS e não sabe quais sugestões existem. O extrator já está na máquina com acesso ao banco, e é o único que pode calcular as janelas corretas. Uma viagem ao ERP. |
| Forma do fluxo | **Painel de sessões + página de sessão com estado** | Processo que dura dias precisa de painel para retomar e de condução para não travar. |
| Manchete do resultado | **Consequência em dinheiro**, com quantidade no detalhe e previsão na área técnica | As três existem, com hierarquia. Três manchetes de mesmo peso viram parede de números. |
| Páginas técnicas atuais | **Rebaixadas, não apagadas** | São o instrumento da dissertação. |

## 3. Modelo de dados

### `ComparacaoSessao` (banco `engine`)

Criada **antes** de existir dado: nasce vazia e o `SugestaoId` é preenchido quando o
ZIP chega e declara de qual sugestão é. É assim que o ovo-e-galinha se resolve — a
web não pode pedir a sugestão antes de ter os dados do PBS.

- `Id`, `RedeId` (FK, padrão F10), `Nome` (opcional, dado pelo usuário)
- `Status`, `CriadoEm`, `AtualizadoEm`
- `SugestaoId` (nulo até o ZIP chegar) + retrato para o painel ser legível sem
  consultar o Stage: `SugestaoDescricao`, `SugestaoDataHora`, `SugestaoTipoCalculo`
- Referências aos jobs existentes: `CargaStageId`, `TreinoJobId`, `ComparacaoPbsId`
- `ResultadoJson` — agregados
- `MotivoInviabilidade` / `MensagemErro`

### `ComparacaoSessaoItens` (banco `engine`)

O detalhe por item **não** vai em JSON. Motivo em §7: o Stage é apagado a cada
import, então o resultado precisa ser materializado, e a tabela por item precisa
suportar ordenação e paginação server-side um mês depois.

- `SessaoId`, `LojaId`, `Sku`, `NomeProduto`, `Curva`
- `CompraSugeridaPbs`, `CompraSugeridaMl`, `VendidoNaJanela`
- `DemandaDiaPbs`, `DemandaDiaMl`, `DemandaDiaReal`
- `SobraPbs`, `SobraMl` (em unidades e em R$)
- PK `(SessaoId, LojaId, Sku)`

### Estados

Sete, e cada um tem **uma única próxima ação**:

| Estado | O usuário vê | O que roda |
|---|---|---|
| `AguardandoDados` | "Baixe o extrator, escolha a sugestão, traga o ZIP" | nada |
| `ProcessandoDados` | "Importando seus dados" | `CargaStage` → `ImportWorker` |
| `Treinando` | "Aprendendo o padrão de venda das suas lojas" | `TreinoJob` → `TreinoWorker` |
| `Comparando` | "Comparando os dois métodos" | `ComparacaoPbs` |
| `Concluida` | o resultado | — |
| `Inviavel` | falta pré-condição, com o remédio | — |
| `Falha` | erro, com como retomar | — |

**`Inviavel` é distinto de `Falha` de propósito.** Não deu erro: faltou
pré-condição, e o remédio é outro. Exemplo: *"Essa sugestão é de 3 dias atrás; as
vendas que provariam quem acertou ainda não aconteceram. Escolha uma anterior a
DD/MM."*

### Orquestração

Um `SessaoWorker` novo, dentro do Worker que já existe, faz polling das sessões em
estado intermediário e cria o próximo job quando o anterior conclui.

Deixa `ImportWorker`, `TreinoWorker` e `SimulacaoWorker` intocados e concentra a
regra de fluxo num lugar legível. A alternativa — cada worker saber quem vem depois —
espalharia a máquina de estados por três arquivos.

## 4. Fluxo e telas

### Painel de comparações — `/` (nova home)

Lista: nome, sugestão do PBS (descrição + data), método usado pelo ERP, estado, e
resultado resumido quando concluída. Ação primária: "Nova comparação". Estado vazio
no primeiro acesso explica o que é isso.

### Nova comparação

Pede só um nome opcional ("Compra de julho — MATTEL"), cria a sessão em
`AguardandoDados` e vai direto para a página dela. Um clique — a escolha da sugestão
é no extrator.

### Página da sessão — `/comparacoes/{id}`

Renderiza conforme o estado. Fase atual em destaque com a próxima ação; fases
anteriores recolhidas.

- `AguardandoDados` — download do extrator, instruções numeradas, área de upload
- Intermediários — o que está acontecendo, com polling de 3s (padrão das telas atuais)
- `Concluida` — o resultado (§5)
- `Inviavel` / `Falha` — o que aconteceu e a próxima ação

### Download do extrator

Servido do **MinIO**, não embutido como asset estático. O `.exe` self-contained tem
~118 MB; embutir carregaria isso no git a cada versão. Endpoint `/extrator/download`
faz stream do MinIO; a UI mostra versão e checksum ao lado do botão.

Custo operacional aceito: publicar o `.exe` no MinIO ao gerar versão nova.

### Upload

Reaproveita o pipeline existente (ApiService → MinIO → Worker). A diferença é nascer
amarrado a uma sessão; o ZIP declara a sugestão e a web valida.

## 5. Tela de resultado

### Manchete — só o que é medível

Duas colunas, "Pelo PBS" e "Como teria sido pelo ML":

- **Capital comprado além do que vendeu**, em R$ —
  `(comprado + estoque_inicial − vendido_na_janela) × preço de compra`, piso em zero.
- **Dias em ruptura e quantos itens** — de `EstoquesDiarios`.

Acima, uma frase em português: *"Nesta compra, o método ML teria deixado R$ 5.300
menos parado em estoque, com um dia a mais de ruptura."* Se o ML perdeu, a frase diz
isso com a mesma clareza.

**Venda perdida em R$ NÃO entra na manchete.** Ela exige saber a demanda que não
virou venda — mas se o item faltou, ele não vendeu, e a própria venda nunca revela
quanto teria vendido. Estimar isso exige assumir uma demanda, e a demanda é
exatamente o que está em disputa entre os dois métodos: usar a estimativa de um
método para julgá-lo é circular. Aparece só na área técnica, rotulada como estimativa
e com a premissa escrita ao lado.

### Detalhe por item

Tabela onde a confiança é construída: item, curva, quanto o PBS mandou, quanto o ML
diria, quanto vendeu, quem ficou mais perto. Ordenável — o comprador vai procurar os
itens que reconhece e conferir contra a memória dele.

### Área técnica

Previsão contra previsão (MAE/WAPE) por categoria e curva, reaproveitando o
drill-down da F7.

### Bloco "onde o ML foi pior"

Fixo, não escondido em aba. Um relatório que só mostra vitória não é evidência.

## 6. O que muda no que existe

- Home deixa de ser a tela de import; passa a ser o painel de comparações.
- Menu com três blocos: **Comparações** (principal), **Administração** (PowerUser, já
  existe), **Técnico** recolhido — recebe `/dados`, `/treinamento`, `/sugestao-compra`.
- Nada é apagado além do que a F13 já previa (`EMaxESegPolicy`).
- O upload avulso continua acessível na área técnica, para importar dado sintético
  sem sessão.

## 7. Consequência do `DELETE ... WHERE RedeId`

O import apaga o Stage da rede a cada ZIP novo. Portanto **o resultado da sessão é
materializado, nunca recalculado a partir do Stage**. Sem isso, abrir uma comparação
antiga depois de rodar uma nova mostraria dado vazio ou errado.

É a razão de `ComparacaoSessaoItens` existir como tabela em vez de JSON.

## 8. Casos de borda

Viram `Inviavel`, com a razão em português e a próxima ação:

- Sugestão recente demais — mostra a data limite calculada
- ZIP sem sugestão do PBS
- Histórico insuficiente antes de T para o modelo aprender (mínimo 34 dias por
  SKU×loja, conforme F5)
- Sem vendas depois de T para julgar quem acertou

Viram `Falha`: import rejeitado (CSV inválido, violação de FK), treino sem SKUs
suficientes.

Outros:
- Re-upload numa sessão que já tem `SugestaoId` diferente → avisa antes de trocar
- Duas sessões para a mesma sugestão → permitido, com aviso

## 9. Testes

- **Unit:** transições da máquina de estados, incluindo cada condição de `Inviavel`.
- **Unit:** cálculo de sobra em unidades e R$ com entradas conhecidas.
- **Unit:** derivação da janela de extração a partir da data da sugestão.
- **Shared:** faker de `SugestoesCompra` + itens, para gerar ZIP com sugestão.
- **Integração:** ciclo completo da sessão até `Concluida`.
- **E2E:** criar sessão, ver `AguardandoDados`, subir ZIP, ver resultado.

## 10. Fora de escopo

**Comparação de demonstração com dado sintético.** Resolveria o problema de a
primeira impressão ser só esforço, mas o gerador sintético não produz sugestão do
PBS — seria preciso ensiná-lo a gerar sugestões coerentes com as vendas sintéticas
(`DemandaDia`, `EstoqueSaldo`, `CompraSugerida` consistentes entre si). Trabalho real
que não serve o fluxo principal. Candidato a fase própria.

**IQVIA e outras fontes externas** no mesmo upload. O usuário parqueou
explicitamente.

**Ação sobre o resultado** (alterar a compra no ERP a partir da comparação). O app
mostra evidência; não escreve no PBS.

## 11. Riscos

| Risco | Observação |
|---|---|
| Volume do ZIP | A extração cobre meses antes e depois de T. O extrator precisa mostrar o tamanho estimado antes de rodar. |
| A Retiro pode usar só "Dias de Reposição" | Pergunta ainda aberta com o usuário. Muda o enquadramento do comparativo, não o desenho da sessão. |
| Sugestões cobrem poucos itens | Se o recorte do PBS for pequeno, a comparação por item fica rasa. Medir a contagem antes de concluir. |
| Primeira impressão só de esforço | Aceito por ora, com a demonstração fora de escopo. |
