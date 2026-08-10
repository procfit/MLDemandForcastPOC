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

**A razão é confidencialidade, e ela vale por si.** A rede topou exportar algumas lojas,
de algumas regiões. Dado de venda, preço de compra e política de estoque é estratégico e
não pode chegar a concorrente. Hoje o extrator exporta **todas** as lojas que a sugestão
cita, sem escolha — e não há nada que o comprador possa fazer a respeito.

Sugestões recentes da Natus, para dimensionar o problema:

| sugestão | lojas | itens |
|---|---|---|
| 22140 · PBM SEM REPOSIÇÃO | 100 | 1.200 |
| 22136 · MEDLEY LOJA | 98 | 16.072 |
| 22137 · ENCOMENDAS | 8 | 24 |
| 22135 · PECCIN | 1 | 20 |

Uma sugestão de 98 lojas exporta hoje o histórico de 98 lojas. Não há como pedir menos.

### O que este spec NÃO afirma

Uma versão anterior deste documento dizia que uma sugestão de 98 lojas é "inextraível", com
base numa extração que produziu 3.502.333 linhas de estoque para **uma** loja. Aquela
medição foi feita antes de `e2a4480` estar na árvore, que escopou a extração aos SKUs da
sugestão. **Os volumes de hoje são muito menores e não foram remedidos**, então
inviabilidade por volume fica como hipótese não provada, não como justificativa.

O que continua verdade sem medição nova: o volume das tabelas pesadas cresce linearmente
com o número de lojas, e o filtro é o que dá ao comprador controle sobre esse fator.

### Interação com a guarda de horizonte

`a7b4446` fez o extrator **recusar** sugestão cuja cobertura passe dos 7 dias que o modelo
prevê, lendo a cobertura dos itens. Amostrando 25 sugestões de julho, as coberturas de
curva são 0, 10, 18, 20, 30 e 35 dias — então boa parte do catálogo é recusada antes de o
filtro de lojas importar.

Isso **não** enfraquece este trabalho: a autorização da rede vale independentemente de
quantas sugestões passam pela guarda, e o filtro é ortogonal a ela. Mas quem for verificar
esta feature contra o banco real precisa escolher uma sugestão que o extrator aceite — ver
§9.

## 2. O que já é escopado por loja, e o que não é

Dois eixos de escopo já existem, e é preciso não confundi-los. **SKU:** desde `e2a4480`,
`produtos`, `vendas`, `estoques_diarios`, `compras` e `promocoes` recebem `@skus` — os SKUs
da sugestão, num parâmetro único lido por `STRING_SPLIT`, porque um parâmetro por SKU
estouraria o teto de 2.100 do SQL Server. **Loja:** `{{LOJAS}}` vira um parâmetro por id.

| arquivo do ZIP | escopo por SKU | escopo por loja hoje | depois |
|---|---|---|---|
| `vendas`, `estoques_diarios`, `compras`, `promocoes` | sim, `@skus` | sim, `{{LOJAS}}` = lojas da sugestão | `{{LOJAS}}` = lojas **escolhidas** |
| `lojas` | n/a | sim, `{{LOJAS}}` | idem |
| `produtos` | sim, `@skus` | n/a (não tem `LojaId`) | inalterado |
| `sugestoes_compra_itens` | é a **fonte** dos SKUs | **não** | ganha `AND R.FILIAL IN ({{LOJAS}})` |
| `sugestoes_compra` (cabeçalho), `mercado_iqvia` (vazio) | n/a | n/a | inalterado |

**O único arquivo que precisa de query nova é `sugestoes_compra_itens`**, e não é opcional,
por dois motivos que se somam. É o arquivo mais sensível do ZIP — demanda/dia calculada
pelo ERP, estoque de segurança, estoque máximo, compra sugerida, compra autorizada e
**preço de compra**, por loja e por SKU. E sem o filtro ele cita lojas que não estão em
`lojas.csv`, o que quebra a FK composta `(RedeId, LojaId)` no `SqlBulkCopy` do Worker: o
import falharia inteiro.

`produtos.csv` **não** leva o mestre da rede — leva os SKUs da sugestão, e só. Uma versão
anterior deste spec afirmava o contrário, e a pergunta feita ao usuário sobre sortimento
partiu dessa premissa errada. A resposta dele ("sortimento não é segredo") continua válida
e o desfecho é melhor do que ele aceitou: não há buraco de sortimento a fechar.

**Efeito colateral que o filtro de loja produz sozinho:** com menos lojas, o conjunto de
SKUs da sugestão diminui — `escopo_sugestao.sql` devolve pares (loja, SKU), e restringir
lojas restringe os SKUs. Logo `@skus` encolhe junto, e `produtos`/`vendas`/`estoques`/
`compras`/`promocoes` encolhem por dois eixos ao mesmo tempo. Isso é desejável, mas
significa que o recorte de loja **muda o conteúdo de `produtos.csv`** — algo a conferir na
verificação, porque um SKU que só existe nas lojas descartadas não deve mais aparecer.

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

**Com banco, no fim.** A verificação que transforma "filtramos" em "está provado que
filtramos" — e a única que fecha o requisito de confidencialidade de ponta a ponta.

Ela precisa de uma sugestão que satisfaça **três** condições ao mesmo tempo: mais de uma
loja, cobertura dentro dos 7 dias que a guarda de horizonte aceita, e itens em
`SUGESTOES_COMPRAS_RESULTADO`. **Nenhum id fica cravado neste spec**: a versão anterior
mandava usar a 22136, que tem cobertura 0 e é recusada antes de chegar ao filtro. Quem
executar acha o candidato assim:

1. `--list` e escolher uma sugestão com mais de uma loja;
2. tentar `--extract` sem `--stores`; se a guarda recusar (exit 5), tentar a próxima;
3. a primeira que passar é a sugestão do teste.

Com ela em mãos, extrair duas vezes — sem `--stores` e com `--stores` de duas lojas — e
conferir, nos CSVs dos dois ZIPs:

- **nenhum** dos sete CSVs do ZIP recortado contém `LojaId` fora das duas escolhidas;
- `lojas.csv` tem exatamente duas linhas;
- `sugestoes_compra_itens.csv` encolheu, e todo `LojaId` dele está entre as duas;
- `produtos.csv` do recorte é subconjunto do produtos do ZIP inteiro (ver §2: restringir
  loja restringe SKU);
- o `manifesto.json` declara as duas lojas e o total da sugestão.

Se nenhuma sugestão do catálogo passar pela guarda de horizonte, a verificação de ponta a
ponta **não pode ser feita** — e isso é resultado, não desculpa: registre no relatório em
vez de declarar verificado.

## 10. Fora de escopo

- **A limitação de horizonte** (o modelo prevê 7 dias, a sugestão do ERP cobre 10 a 35).
  É o que hoje recusa boa parte do catálogo, é maior que este trabalho e merece
  brainstorming próprio. O filtro de lojas é ortogonal e vale por si — a autorização da
  rede não depende de quantas sugestões passam pela guarda.
- **Mostrar a restrição na tela da comparação** — o dado passa a existir no manifesto;
  exibi-lo toca Worker, ApiService, Engine (migration) e Web.
- **Teto configurado de lojas autorizadas**, que transformaria "não vaza" de disciplina em
  garantia. Foi oferecido e recusado; fica registrado como caminho conhecido.
- **Estimativa de volume** antes de extrair. O diálogo mostra itens por loja, que é o
  sinal barato; estimar linhas de venda e estoque exigiria consulta própria.
