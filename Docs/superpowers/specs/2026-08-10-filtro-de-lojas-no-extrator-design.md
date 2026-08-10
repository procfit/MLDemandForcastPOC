# Filtro de lojas no extrator — design

> **Desvio registrado (CLAUDE.md §3):** o guia proíbe `.md` de planejamento/decisão.
> Gravado a pedido explícito do usuário, seguindo o precedente desta pasta.

**Objetivo:** permitir que o comprador escolha, a cada extração, quais lojas da sugestão
entram no ZIP — porque a rede autorizou exportar apenas parte das lojas, e o dado de
venda, preço de compra e política de estoque das demais não pode sair.

**Quem é o usuário:** o comprador da rede, operando o extrator na máquina do cliente. Ele
conhece o acordo; o software não.

---

## 1. Por que agora

Duas razões independentes, e as duas medidas na instância real da Natus.

**Confidencialidade.** A rede topou exportar algumas lojas, de algumas regiões. Dado de
venda e de política comercial é estratégico e não pode chegar a concorrente. Hoje o
extrator exporta **todas** as lojas que a sugestão cita, sem escolha.

**Viabilidade.** Sugestões recentes, contando lojas e itens:

| sugestão | lojas | itens |
|---|---|---|
| 22140 · PBM SEM REPOSIÇÃO | 100 | 1.200 |
| 22136 · MEDLEY LOJA | 98 | 16.072 |
| 22137 · ENCOMENDAS | 8 | 24 |
| 22135 · PECCIN | 1 | 20 |
| 18172 · ACHE RX | 1 | 365 |

A extração de **uma** loja (18172) produziu 3.502.333 linhas de estoque, 12,2 MB, em
37 s. As tabelas pesadas são escopadas por loja e por 12 meses de janela, então uma
sugestão de 98 lojas tende a centenas de milhões de linhas. Hoje essas sugestões são,
na prática, inextraíveis.

## 2. O que já é escopado por loja, e o que não é

| arquivo do ZIP | escopado hoje | depois |
|---|---|---|
| `lojas`, `vendas`, `estoques_diarios`, `compras`, `promocoes` | sim, por `{{LOJAS}}` = lojas da sugestão | `{{LOJAS}}` = lojas **escolhidas** |
| `sugestoes_compra_itens` | **não** | ganha `AND R.FILIAL IN ({{LOJAS}})` |
| `produtos` | não (mestre inteiro, 79.711 SKUs) | **continua inteiro** |
| `sugestoes_compra` (cabeçalho), `mercado_iqvia` (vazio) | sem dimensão de loja | inalterado |

`sugestoes_compra_itens` não é opcional por dois motivos que se somam. É o arquivo mais
sensível do ZIP — demanda/dia calculada pelo ERP, estoque de segurança, estoque máximo,
compra sugerida, compra autorizada e **preço de compra**, por loja e por SKU. E sem o
filtro ele cita lojas que não estão em `lojas.csv`, o que quebra a FK composta
`(RedeId, LojaId)` no `SqlBulkCopy` do Worker: o import falharia inteiro.

`produtos.csv` fica inteiro por decisão do usuário: o sortimento de uma farmácia é
visível na prateleira, e o que é estratégico (volume, margem, preço, política de estoque)
vive nos arquivos que têm `LojaId`. Fechar esse buraco depois custa uma consulta a mais
para levantar a união de SKUs das lojas escolhidas, e exige garantir que nenhum SKU
citado por venda/estoque/compra fique de fora — senão a FK `(RedeId, Sku)` quebra.

## 3. Decisões tomadas

| Questão | Decisão | Por quê |
|---|---|---|
| Quem autoriza as lojas | **O comprador escolhe a cada extração**, sem teto configurado | Decisão do usuário. Registrado o custo: "não vaza" passa a ser disciplina do operador, não garantia do software — um clique a mais exporta a loja que o acordo excluía, e nada impede. |
| `produtos.csv` | **Continua inteiro** | Sortimento não é segredo; o que é estratégico tem `LojaId`. |
| Onde o recorte fica registrado | **Manifesto + tela + log do extrator**; web inalterada | O dado fica gravado esperando a tela que vai exibi-lo. Sem isso, o resultado da comparação seria indistinguível de um da rede inteira. |
| Onde o comprador escolhe | **Diálogo separado** | 98 lojas não cabem no form de 660×760 sem espremer o grid. `MainForm.cs` não tem cobertura automatizada e concentrou três defeitos recentes; não mexer no layout dele é redução de risco deliberada. |
| Padrão da seleção | **Nada marcado**; Extrair desabilitado até haver uma loja | Marcar 3 é menos trabalho que desmarcar 95, e o padrão que erra, erra para o lado de não exportar. |

## 4. Como o comprador escolhe

**Query nova, `lojas_da_sugestao.sql`:** `FILIAL` e contagem de itens, agrupados, para uma
sugestão. Mesma forma barata da contagem que já existe (0,01 s medido para uma sugestão).
Os nomes vêm de `lojas_disponiveis.sql`, que já existe e traz as 106 lojas ativas; o
casamento é em memória.

O diálogo lista `código · nome · N itens`, com filtro por texto e "marcar todas / nenhuma".
Loja citada pela sugestão que não esteja no cadastro ativo aparece com o código e
`(sem cadastro)` — some da lista seria pior: o comprador precisa saber que ela existe na
sugestão e decidir sobre ela.

O rodapé da seleção passa a dizer:

> 365 itens · **3 de 98 lojas** · janela de dados 09/06/2025 a 14/06/2026

## 5. O que fica registrado

`ZipManifest` ganha `LojasExportadas` (lista de ids) e `LojasNaSugestao` (quantas a
sugestão tinha). O log do dia registra a escolha por extenso, com nomes.

**Isto obriga a mexer no Worker**, e não só no extrator: `ZipManifestTests` compara campo
a campo o `ZipManifest` do extrator com o `ManifestoDaSugestao` do Worker e exige que
sejam iguais — o teste existe para impedir que os dois lados divirjam em silêncio.
`ManifestoDaSugestao` ganha os mesmos dois campos, **sem uso ainda**: eles ficam
desserializados e disponíveis para a fase que puser a restrição na tela do comprador.

O leitor do Worker usa `JsonSerializerOptions` padrão, que ignora campo desconhecido — mas
essa tolerância é o que salva ZIPs **antigos** lidos por código novo, não o contrário.
Campo novo escrito pelo extrator com o Worker desatualizado é lido e ignorado; o teste de
contrato é que impede o par de sair divergente.

## 6. Linha de comando

`--stores 12,45,78`. **Ausente = todas as lojas da sugestão**, o que preserva o
comportamento atual e mantém o teste ponta a ponta funcionando sem enumerar loja.

Assimetria registrada de propósito: a interface gráfica é fail-closed (nada marcado) e o
CLI é fail-open (ausente = todas). São públicos diferentes — o operador de terminal é
explícito por natureza e o flag está documentado —, mas é uma diferença de política e não
uma conveniência, então fica dita aqui e no `--help`.

## 7. `ExtractionRequest` volta a ter `LojaIds`

Com significado **diferente** do que a F14 removeu. Lá era escolha livre de lojas *no
lugar* da sugestão — a UX que a F14 substituiu por "uma sugestão, janelas derivadas".
Aqui é recorte *dentro* da sugestão: o serviço interseciona com as lojas que
`LoadEscopoSugestao` devolveu e **recusa** id que não pertença a ela, com erro tipado.
Isso impede que um `--stores` digitado errado exporte loja de outra sugestão.

Lista vazia depois da interseção é recusa, não ZIP vazio: um ZIP com sete CSVs de
cabeçalho passaria na validação do import e entraria no Stage como se fosse completo —
o mesmo motivo pelo qual o ZIP parcial é apagado hoje.

## 8. Erros novos

Dois, na taxonomia que já existe (`ExtratorErros.cs`):

- `LojasNaoSelecionadasErro` — nenhuma loja escolhida, ou a interseção esvaziou.
- `LojaForaDaSugestaoErro(ids)` — `--stores` citou loja que a sugestão não tem; a
  mensagem nomeia os ids recusados, porque a correção é digitar outro número.

Ambos mapeiam para `CliExitCode.ArgumentosInvalidos` — é entrada errada do operador, não
falha de infraestrutura.

## 9. Testes

**Sem banco:** a interseção (subconjunto válido, id estranho recusado com os ids
nomeados, lista vazia recusada, lista ausente no CLI = todas); o parser de `--stores`
(lista, espaços, id repetido, id não numérico); a serialização e a releitura dos dois
campos novos do manifesto; o teste de contrato existente, estendido; a leitura de
`lojas_da_sugestao.sql` por `DataTableReader`; e a junção com os nomes, inclusive a loja
sem cadastro.

**Com banco, no fim:** extrair a sugestão 22136 (98 lojas) escolhendo 2, e conferir que
**nenhum** dos sete CSVs contém `LojaId` fora das duas escolhidas. É a verificação que
transforma "filtramos" em "está provado que filtramos", e é a única que fecha o requisito
de confidencialidade de ponta a ponta.

## 10. Fora de escopo

- **Restringir `produtos.csv`** ao sortimento das lojas escolhidas (§2).
- **Mostrar a restrição na tela da comparação** — o dado passa a existir no manifesto;
  exibi-lo toca Worker, ApiService, Engine (migration) e Web.
- **Teto configurado de lojas autorizadas**, que transformaria "não vaza" de disciplina em
  garantia. Foi oferecido e recusado; fica registrado como caminho conhecido.
- **Estimativa de volume** antes de extrair. O diálogo mostra itens por loja, que é o
  sinal barato; estimar linhas de venda e estoque exigiria consulta própria.
