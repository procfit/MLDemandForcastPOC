# Mapeamento de Extração PBS → Stage

Documento **vivo** de controle da engenharia de extração dos dados reais do ERP
**PBS** para o schema **Stage** deste projeto (tabelas em
[TableSchemas.cs](../CosmosPro.ML.DemandForCast.Worker/TableSchemas.cs)). Atualizar
a cada tabela/campo resolvido.

## Contexto e forma de trabalho

- **Objetivo:** produzir `SELECT`s T-SQL que convertem o modelo do PBS para o shape
  exato do nosso Stage, entregues como CSV para o pipeline de import existente.
- **Fontes:**
  - **Pensefarma** — instância PBS usada **apenas como referência de schema** (acesso
    TDS via MCP). **Somente metadados; nenhuma leitura de dado de negócio** — o acesso
    aos dados deles não foi autorizado para este projeto. Serve para descobrir
    estrutura, nomes, tipos e relacionamentos e validar o *shape* das queries (zero
    linha, via `sp_describe_first_result_set` / `SELECT TOP 0`).
  - **NatusFarma** — instância PBS com **dados autorizados** para este projeto (MCP
    `mssql-natusfarma-pbs-prod`, porta 1435). Serve como ambiente de teste real: aqui as
    queries podem ser **executadas com dados**, validando semântica e cobertura antes de ir
    para a Retiro. Atenção: a versão do PBS pode diferir da Retiro.
  - **Farmácias Retiro** — cliente-alvo, dados **autorizados** (fins acadêmicos).
    Acesso apenas gráfico (SSMS em Terminal Services). **É onde os SELECTs finais
    rodam** e de onde sai o CSV real.
- **Entrega:** projeto **`CosmosPro.ML.DemandForCast.Extractor`** — app WinForms
  self-contained (single-file win-x64) que roda no host da Retiro, executa as queries,
  gera os CSVs, zipa e salva num diretório. O ZIP entra pela UI de import já existente
  (Web → ApiService → MinIO → Worker `SqlBulkCopy`). **Dispensa o SSMS.**
  *(Decisão revista em 2026-07-27: a proposta anterior era rodar os SELECTs no SSMS; o
  volume, o carry-forward de estoque e o empacotamento tornaram o app a opção melhor.)*

### Escopo do piloto (fixado)

| Parâmetro | Valor |
|---|---|
| Lojas | Derivadas da sugestão de compra escolhida — sem seleção manual (F14) |
| SKUs | **todos com movimento** (preserva itens intermitentes) |
| Estoque | grade diária com **carry-forward** |
| Volume estimado | ~12M linhas de estoque (~50 MB zipado) |

### Como publicar o extrator

```
dotnet publish CosmosPro.ML.DemandForCast.Extractor -c Release -r win-x64 ^
  --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Saída: um `.exe` de ~118 MB, sem dependência de runtime .NET no host.

O comando acima é para gerar o `.exe` à mão. No caminho normal quem o gera é o CI: o job
Windows publica, calcula o checksum e sobe o par `extrator.exe` + `manifesto.json` como
artefato `extrator` da execução — que a UI do Actions entrega como um `.zip`.

De um jeito ou de outro, o `.exe` só chega ao comprador depois de publicado no bucket MinIO
`extrator` — pela tela `/admin/extrator` (`PowerUser`), que recebe **um ZIP** com os dois
arquivos. Passo a passo em
[README.md § Publicar o extrator no MinIO](../README.md#publicar-o-extrator-no-minio).

> **Guardrail:** este documento não contém credenciais, hosts nem dados reais — só
> estrutura, mapeamento e contagens agregadas.

## Objetos PBS que o extrator toca (lista para o DBA)

Todo acesso é **somente leitura** (`SELECT`); o extrator nunca escreve, nunca cria objeto e
não usa tabela temporária no PBS. Lista derivada de
[`Extractor/Queries/*.sql`](../CosmosPro.ML.DemandForCast.Extractor/Queries/) — ao mexer em
qualquer query, **reconferir esta tabela** (é o que o DBA usa para o GRANT):

| Objeto (`dbo.`) | Usado por | Para quê |
|---|---|---|
| `EMPRESAS_USUARIAS` | lojas, lojas_disponiveis | mestre de lojas |
| `ENDERECOS` | lojas | UF/cidade da loja |
| `ENTIDADES` | lojas, compras | **CNPJ da loja (`CGC`, F16)** e nome do fornecedor |
| `PRODUTOS` | produtos | mestre de produtos |
| `PRODUTOS_EAN` | produtos | EAN principal |
| `SECOES_PRODUTOS` | produtos | categoria |
| `GRUPOS_PRODUTOS` | produtos | subcategoria |
| `MARCAS` | produtos | fabricante |
| `PRODUTOS_DCB` | produtos | princípio ativo (ponte) |
| `DCB_MEDICAMENTOS` | produtos | princípio ativo (dicionário) |
| `VENDAS_ANALITICAS` | vendas | fato de vendas (agregado a diário) |
| `ESTOQUE_LANCAMENTOS` | estoques_movimentos | movimentos de estoque |
| `ESTOQUE_ATUAL` | estoques_movimentos | âncora do saldo reconstruído |
| `CENTROS_ESTOQUE` | estoques_movimentos | centro → loja (`TIPO_ESTOQUE=2`) |
| `PEDIDOS_COMPRAS` | compras | cabeçalho de pedidos |
| `PEDIDOS_COMPRAS_PRODUTOS` | compras | itens de pedidos |
| `PROMOCOES_FLEXIVEIS` (+ `_EMPRESAS`, `_LEVE`, `_GANHE`) | promocoes | campanhas |
| `TIPOS_PROMOCOES_FLEXIVEIS` | promocoes | tipo da campanha |
| `SUGESTOES_COMPRAS` | catalogo_sugestoes, sugestao_por_id, sugestoes_compra | cabeçalho da sugestão (F12/F14) |
| `SUGESTOES_COMPRAS_RESULTADO` | catalogo_sugestoes_contagens, escopo_sugestao, lojas_da_sugestao, sugestoes_compra_itens, sugestoes_compra_diagnostico | itens da sugestão + escopo de lojas/SKUs (F12/F14) |

**23 objetos**, todos tabela. `TIPOS_CALCULO_SUGESTAO` aparece só em **comentário** no
`sugestoes_compra.sql` (o `TipoCalculo` é copiado como número) — não é consultada e **não
entra no GRANT**; um `grep` ingênuo por `dbo.` a inclui por engano. Mesmo caso de
`PROMOCOES`, `ABC_FARMA_EDI_PRODUTOS`, `INDICACOES_TERAPEUTICAS`, `REGIONAIS` e
`TIPOS_REDES`, citadas neste documento mas nunca lidas.

Fora do PBS (não entram no GRANT): dados de mercado **IQVIA**, que desde a **F16** chegam
por XLSX na tela `/mercado` e vivem no banco `engine` — o extrator não os toca mais (o
antigo `mercado_iqvia.csv` vazio foi removido do ZIP); e **SinaisExternos** (clima/gripe),
ainda sem fonte.

> **A versão desta lista que vai para o cliente é outra:**
> [extrator-objetos-banco.md](extrator-objetos-banco.md). Este documento aqui é **interno**
> — cita instâncias de outros clientes, volumes e ferramental nosso, e **não pode ser
> enviado ao DBA da rede**. Ao mexer nas queries, atualizar os dois.

## Contrato CSV (reader do Worker)

Ver [CsvWriter.cs](../CosmosPro.ML.DemandForCast.SyntheticData/Generation/CsvWriter.cs)
e [TableSchemas.cs](../CosmosPro.ML.DemandForCast.Worker/TableSchemas.cs). Regras:

- **Cultura invariante** (independe do locale do host — cuidado com o "Save Results As
  CSV" do SSMS, que usa o locale da máquina e quebra isto).
- Delimitador `,` ; 1 arquivo por tabela ; **linha de cabeçalho** com os nomes das colunas na ordem do schema.
- **Datas** `yyyy-MM-dd` ; **decimais** com `.` (formato `0.####`) ; **bool** `1`/`0`.
- **NULL** = campo vazio (só para colunas nullable; obrigatória vazia = erro no import).
- **Aspas** só quando o valor contém `,` `"` `\n` `\r`; `"` interno vira `""` (RFC 4180).

## Legenda de status

| | Significado |
|---|---|
| ✅ | mapeado (falta só validar shape / validado) |
| 🟡 | mapeado com ressalva ou pendência |
| ⏳ | a investigar |
| ⏭️ | pulado — correspondência ainda não encontrada no PBS (**revisitar**) |
| ❌ | indisponível no PBS (entregar NULL) |

## Progresso geral

| Tabela Stage | Origem PBS (principal) | Status |
|---|---|---|
| Lojas | `EMPRESAS_USUARIAS` (+ ENDERECOS) | 🟡 mapeado; `Regiao`/`Perfil`/`DiasOperacaoSemana`/`DataAbertura` sem fonte na Retiro |
| **Produtos** | `PRODUTOS` (+ EAN, hierarquia, DCB, MARCAS) | 🟡 shape validado (12 col); falta item ListaControle + validação de dado na Retiro |
| Vendas | `VENDAS_ANALITICAS` (61,6M linhas → agregado diário) | ✅ extraído com dados reais; resta confirmar `OPERACAO_FISCAL`/devoluções |
| EstoquesDiarios | `ESTOQUE_LANCAMENTOS` + `ESTOQUE_ATUAL` (âncora) + `CENTROS_ESTOQUE` | ✅ extraído com carry-forward; histórico só ~10 meses |
| Compras | `PEDIDOS_COMPRAS` + `_PRODUTOS` | ✅ mapeado e validado |
| Promocoes | `PROMOCOES_FLEXIVEIS` (+ `_EMPRESAS`, `_LEVE`, `_GANHE`) | ✅ mapeado e validado |
| SugestoesCompra | `SUGESTOES_COMPRAS` (+ `TIPOS_CALCULO_SUGESTAO`) | ✅ mapeado e validado (F12/F14) |
| SugestoesCompraItens | `SUGESTOES_COMPRAS_RESULTADO` | ✅ mapeado e validado (F12/F14); também deriva o escopo de lojas/SKUs do envio |
| ~~MercadoIqvia~~ | *(fonte externa IQVIA)* | **Removida do ZIP na F16** — o XLSX da IQVIA entra pela tela `/mercado` direto no `engine`; o extrator não gera mais o CSV vazio |
| SinaisExternos | *(fonte externa: clima/gripe — fora do PBS)* | ⏳ |

## Tabelas de referência PBS já inspecionadas (metadados)

| Tabela PBS | Linhas | Uso |
|---|---:|---|
| `dbo.PRODUTOS` | 59.813 | mestre de produtos (PK `PRODUTO` numeric identity) |
| `dbo.PRODUTOS_EAN` | 69.545 | EAN por produto (flag `EAN_PRINCIPAL`) |
| `dbo.SECOES_PRODUTOS` | 8 | nome da Categoria |
| `dbo.GRUPOS_PRODUTOS` | 16 | nome da Subcategoria |
| `dbo.MARCAS` | 1.951 | nome do Fabricante |
| `dbo.PRODUTOS_DCB` | 5.541 | princípio ativo/apresentação por produto (1:N) |
| `dbo.DCB_MEDICAMENTOS` | 12.728 | dicionário — nome do princípio ativo (`DESCRICAO`) |
| `dbo.INDICACOES_TERAPEUTICAS` | 2 | insuficiente para classe terapêutica |
| `dbo.ABC_FARMA_EDI_PRODUTOS` | 0 | **vazia** — descartada (tinha princípio ativo/registro/tarja) |
| `dbo.LISTA_PNU` | 4 | candidata a ListaControle (item aberto) |
| `dbo.EMPRESAS_USUARIAS` | 90 | mestre de lojas/filiais (PK `EMPRESA_USUARIA`) |
| `dbo.ENDERECOS` | 576.822 | endereço por entidade (`CIDADE`, `ESTADO` diretos) |
| `dbo.MUNICIPIOS` | 5.571 | município normalizado (`NOME` + `ESTADO`) |
| `dbo.REGIONAIS` | 9 | nome da Regiao (`NOME`) |
| `dbo.TIPOS_REDES` | 3 | candidata a Perfil (`DESCRICAO`) |
| `dbo.ENTIDADES` | 746.784 | entidade da loja (sem endereço próprio) |
| `dbo.VENDAS_ANALITICAS` | 61.605.192 | fato de vendas item-a-item (43 GB; agregar p/ diário) |

Inspecionadas na **NatusFarma** (dados autorizados):

| Tabela PBS | Linhas | Uso |
|---|---:|---|
| `dbo.ESTOQUE_LANCAMENTOS` | 52.614.107 | movimentos de estoque (3,7 GB; 2025-09-29→2026-07-24). ⚠️ `ESTOQUE_SALDO` **100% NULL** — usar `ESTOQUE_ENTRADA`/`ESTOQUE_SAIDA` |
| `dbo.ESTOQUE_ATUAL` | 61.270 (5 lojas) | foto atual sem data; **é a âncora** do saldo (saldo 100% preenchido) |
| `dbo.ESTOQUE_LANCAMENTOS_CACHE` | 1.297.792 | cache de ajustes — **descartada** |
| `dbo.CENTROS_ESTOQUE` | 160 | centro → `EMPRESA` (loja); `TIPO_ESTOQUE=2` = retaguarda de loja |
| `dbo.TIPOS_ESTOQUE` | 16 | dicionário (1=DEPOSITO, 2=RETAGUARDA DE LOJA, 3=FRENTE DE LOJA, 5=DESCARTE/AVARIA…) |

---

## Produtos 🟡

**Origem:** `dbo.PRODUTOS` (PK `PRODUTO`) + joins. Decisões confirmadas: Categoria←Seção,
Subcategoria←Grupo, Fabricante←Marca, Ativo←`CADASTRO_ATIVO`.

| # | Campo Stage | Tipo | Null | Origem PBS | Transformação | Status | Obs |
|---|---|---|:--:|---|---|:--:|---|
| 1 | `Sku` | string | N | `PRODUTOS.PRODUTO` | `CONVERT(varchar(30), …)` | ✅ | numeric → string |
| 2 | `Nome` | string | N | `PRODUTOS.DESCRICAO` | `COALESCE(DESCRICAO, DESCRICAO_REDUZIDA)`, `LEFT(…,200)` | ✅ | `DESCRICAO` é nullable |
| 3 | `Categoria` | string | S | `SECOES_PRODUTOS.DESCRICAO` | join por `SECAO_PRODUTO` | ✅ | só 8 seções (baixa cardinalidade) |
| 4 | `Subcategoria` | string | S | `GRUPOS_PRODUTOS.DESCRICAO` | join por `GRUPO_PRODUTO` | ✅ | só 16 grupos; avaliar `SUBGRUPOS_PRODUTOS` se quiser mais fino |
| 5 | `Fabricante` | string | S | `MARCAS.DESCRICAO` | join por `MARCA` | ✅ | |
| 6 | `PrincipioAtivo` | string | S | `DCB_MEDICAMENTOS.DESCRICAO` via `PRODUTOS_DCB` | `STRING_AGG(' + ')`, `LEFT(…,200)` | 🟡 | cobertura ~9% (só medicamentos); combos = 1:N; `STRING_AGG` exige SQL Server 2017+. ❓ **DÚVIDA:** cobertura real e se combo deve concatenar ou pegar o principal — validar na Retiro |
| 7 | `Apresentacao` | string | S | `PRODUTOS_DCB.APRESENTACAO` | `MAX`/1º, `LEFT(…,120)` | 🟡 | mesma cobertura do princípio ativo. ❓ **DÚVIDA:** se >1 DCB, qual apresentação? (hoje `MAX` arbitrário) |
| 8 | `Ean` | string | S | `PRODUTOS_EAN.EAN_FORMATADO` | `OUTER APPLY TOP 1` where `EAN_PRINCIPAL='S'` | ✅ | fallback p/ não-interno (`PRODUTOS_EAN.ATIVO` removido — **não existe na Retiro**). ❓ **DÚVIDA:** SKUs com 0 ou >1 EAN principal? — validar na Retiro |
| 9 | `RegistroAnvisa` | string | S | — | NULL | ❌ | só existia em `ABC_FARMA_EDI_PRODUTOS` (vazia) |
| 10 | `ListaControle` | string | S | `LISTA_PNU`? / `CONTROLADO`? | *(a definir)* | ⏳ | **item 5** — `LISTA_PNU` tem só 4 valores; tarja granular (A1/B1…) pode não existir. ❓ **DÚVIDA:** confirmar fonte (você ia buscar) |
| 11 | `ClasseTerapeutica` | string | S | — | NULL | ❌ | `INDICACOES_TERAPEUTICAS` tem só 2 linhas; ABCFarma vazia |
| 12 | `Ativo` | bool | N | `PRODUTOS.CADASTRO_ATIVO` | `CASE 'S' → 1 ELSE 0` | ✅ | |

**SELECT atual (rascunho — pendente de validação de shape):**

```sql
SELECT
    Sku               = CONVERT(varchar(30), P.PRODUTO),
    Nome              = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(P.DESCRICAO)),''), P.DESCRICAO_REDUZIDA), 200),
    Categoria         = SEC.DESCRICAO,
    Subcategoria      = GRU.DESCRICAO,
    Fabricante        = MAR.DESCRICAO,
    PrincipioAtivo    = CONVERT(varchar(200), LEFT(PA.PrincipiosAtivos, 200)),
    Apresentacao      = LEFT(PA.Apresentacao, 120),
    Ean               = EANP.EAN_FORMATADO,
    RegistroAnvisa    = CONVERT(varchar(20),  NULL),   -- indisponível (ABCFarma vazia)
    ListaControle     = CONVERT(varchar(10),  NULL),   -- pendente item 5
    ClasseTerapeutica = CONVERT(varchar(120), NULL),   -- indisponível (só 2 indicações)
    Ativo             = CAST(CASE WHEN P.CADASTRO_ATIVO = 'S' THEN 1 ELSE 0 END AS bit)
FROM dbo.PRODUTOS P
LEFT JOIN dbo.SECOES_PRODUTOS SEC ON SEC.SECAO_PRODUTO = P.SECAO_PRODUTO
LEFT JOIN dbo.GRUPOS_PRODUTOS GRU ON GRU.GRUPO_PRODUTO = P.GRUPO_PRODUTO
LEFT JOIN dbo.MARCAS          MAR ON MAR.MARCA         = P.MARCA
OUTER APPLY (                              -- EAN principal, 1 por SKU
    SELECT TOP 1 E.EAN_FORMATADO
    FROM dbo.PRODUTOS_EAN E
    WHERE E.PRODUTO = P.PRODUTO
    ORDER BY CASE WHEN E.EAN_PRINCIPAL='S' THEN 0 ELSE 1 END,
             CASE WHEN E.EAN_INTERNO='N'   THEN 0 ELSE 1 END,
             E.PRODUTO_EAN
) EANP
OUTER APPLY (                              -- princípio ativo (concatena combos) + apresentação
    SELECT PrincipiosAtivos = STRING_AGG(CONVERT(varchar(max), M.DESCRICAO), ' + ')
                                WITHIN GROUP (ORDER BY M.DESCRICAO),
           Apresentacao     = MAX(PD.APRESENTACAO)
    FROM dbo.PRODUTOS_DCB PD
    JOIN dbo.DCB_MEDICAMENTOS M ON M.DCB = PD.DCB
    WHERE PD.PRODUTO = P.PRODUTO
) PA;
```

**Pendências Produtos:**
- [ ] Item 5: fonte de `ListaControle` (`LISTA_PNU` vs `CONTROLADO`).
- [ ] Versão do SQL Server da Retiro (se < 2017, trocar `STRING_AGG` por `FOR XML PATH`).
- [x] Validar shape via `sp_describe_first_result_set` — **OK 2026-07-10** (12 colunas, tipos cabem no Stage).
- [ ] Ajustar SELECT para emitir CSV do contrato (colunas na ordem, `Ativo` 1/0, quoting).
- [ ] Perguntas de **dado** (rodar na Retiro): cobertura de princípio ativo; SKUs com 0 ou >1 EAN principal; combos (>1 DCB).

---

## Lojas 🟡

**Origem:** `dbo.EMPRESAS_USUARIAS` (PK `EMPRESA_USUARIA`, 90 filiais) + joins.
Endereço não está na `EMPRESAS_USUARIAS` **nem** em `ENTIDADES` → vem de `ENDERECOS`,
que já traz `CIDADE` e `ESTADO` direto (dispensa `MUNICIPIOS`). Desde a **F16** a query
também junta `ENTIDADES` (via `E.ENTIDADE`) para extrair o **CNPJ** da loja.

| # | Campo Stage | Tipo | Null | Origem PBS | Transformação | Status | Obs |
|---|---|---|:--:|---|---|:--:|---|
| 1 | `LojaId` | int | N | `EMPRESAS_USUARIAS.EMPRESA_USUARIA` (PK) | `CONVERT(int,…)` | ✅ | **RESOLVIDO:** `VENDAS_ANALITICAS.EMPRESA` usa essa chave |
| 2 | `Nome` | string | N | `NOME_FANTASIA` (fallback `NOME`) | `COALESCE`, `LEFT` | 🟡 | ❓ **DÚVIDA:** fantasia vs. razão social |
| 3 | `UF` | string | N | `ENDERECOS.ESTADO` (via `ENTIDADE`) | `OUTER APPLY TOP 1` | 🟡 | ❓ **DÚVIDA:** qual endereço se houver +1 — desempate agora é só menor `ENDERECOS` (`ATIVO` **não existe na Retiro**) |
| 4 | `Cidade` | string | N | `ENDERECOS.CIDADE` (ou `MUNICIPIOS.NOME`) | idem | 🟡 | `CIDADE` é texto livre; `MUNICIPIOS.NOME` normaliza |
| 5 | `Regiao` | string | S | — | NULL | ⏭️ | `REGIONAIS` **não existe na Retiro**. Alternativa: derivar macro-região do `UF` (IBGE) — decidir |
| 6 | `Perfil` | string | S | — | NULL | ⏭️ | `TIPOS_REDES` **não existe na Retiro**. Alternativas a investigar: `GRUPO_COMERCIAL`, `FAIXA_PRECO` |
| 7 | `DiasOperacaoSemana` | byte | N | — | default provisório (7) | ⏭️ | ❓ **DÚVIDA:** **sem fonte** no PBS e campo é NOT NULL — precisa default ou outra tabela |
| 8 | `DataAbertura` | date | S | — | NULL | ⏭️ | ❓ **DÚVIDA:** sem fonte óbvia; revisitar |
| 9 | `Ativo` | bool | N | `EMPRESAS_USUARIAS.ATIVO='S'` | `CAST … bit` | ✅ | há também `ATIVO_COMERCIAL` |
| 10 | `Cnpj` | string | S | `ENTIDADES.CGC` (via `E.ENTIDADE`) | `REPLACE` de `.`/`/`/`-` → só dígitos | 🟡 | **F16** — ponte com o painel de PDVs da IQVIA (`engine.MercadoBrickPdvs`). ❓ **DÚVIDA:** nome `CGC` assumido pela convenção Procfit; **confirmar na Retiro/NatusFarma** antes do primeiro ZIP real |

**Query:** implementada em
[`Queries/lojas.sql`](../CosmosPro.ML.DemandForCast.Extractor/Queries/lojas.sql) — a versão
embarcada é a fonte de verdade (o rascunho que vivia aqui envelheceu: hoje a query filtra
pelas lojas da sugestão via `{{LOJAS}}`, e desde a F16 junta `ENTIDADES` para o `Cnpj`).

**Pendências Lojas:**
- [x] Chave da loja = `EMPRESA_USUARIA` (confirmado: `VENDAS_ANALITICAS.EMPRESA`).
- [ ] `Cnpj`: confirmar que a coluna em `ENTIDADES` se chama `CGC` (assumido pela
      convenção Procfit; MCPs do PBS fora do ar quando a F16 foi implementada) e o
      formato do conteúdo (com ou sem máscara).
- [ ] `Nome`: fantasia vs. razão social.
- [ ] Endereço: critério quando há múltiplos (`ATIVO` indisponível na Retiro; avaliar `TIPO_ENDERECO`).
- [ ] `Regiao`: derivar macro-região do `UF` (IBGE) ou deixar NULL?
- [ ] `Perfil`: investigar `GRUPO_COMERCIAL`/`FAIXA_PRECO` na Retiro ou deixar NULL.
- [ ] `DiasOperacaoSemana`: **sem fonte** (NOT NULL → definir default, ex.: 7).
- [ ] `DataAbertura`: sem fonte — revisitar.
- [ ] Validar shape via `sp_describe_first_result_set`.

---

## Vendas 🟡

**Origem:** `dbo.VENDAS_ANALITICAS` — **61,6M linhas / 43 GB, 129 colunas** (maioria fiscal:
PIS/COFINS/ICMS/IBS/CBS). Grão do Stage é **diário por (Data, LojaId, Sku)** (ver
[Vendas.sql](../CosmosPro.ML.DemandForCast.Database/Tables/Vendas.sql)) → a extração
**agrega** as linhas de item.

| # | Campo Stage | Tipo | Null | Origem PBS | Transformação | Status | Obs |
|---|---|---|:--:|---|---|:--:|---|
| 1 | `Data` | date | N | `VENDAS_ANALITICAS.MOVIMENTO` | GROUP BY | ✅ | usar `MOVIMENTO`; **não** `DATA` (= `getdate()` de inclusão) |
| 2 | `LojaId` | int | N | `EMPRESA` | `CONVERT(int)` + GROUP BY | ✅ | mesma chave de `EMPRESAS_USUARIAS.EMPRESA_USUARIA` |
| 3 | `Sku` | string | N | `PRODUTO` | `CONVERT(varchar30)` + GROUP BY | ✅ | casa com `PRODUTOS.PRODUTO` |
| 4 | `Quantidade` | decimal | N | `SUM(QUANTIDADE)` | agregado | ✅ | **RESOLVIDO:** usar `QUANTIDADE` (quantidade de venda), não `QUANTIDADE_DEMANDA` |
| 5 | `PrecoUnitario` | decimal | N | derivado `SUM(valor)/NULLIF(SUM(QUANTIDADE),0)` | calculado | 🟡 | não há preço unitário direto |
| 6 | `ValorTotal` | decimal | N | `SUM(VENDA_LIQUIDA)` | agregado | ✅ | **RESOLVIDO:** `VENDA_LIQUIDA` |

**Decisões de negócio (críticas — tocam o núcleo do POC):**
- ✅ **`TIPO_BONIFICACAO` NÃO deve ser filtrado** (medido em dados reais): `'P'` responde por
  **99,7%** das linhas (277.551 de 278.810 em 5 lojas / 8 semanas) — é o caso normal, não
  brinde. O filtro `ISNULL(TIPO_BONIFICACAO,'') = ''` do rascunho reduzia a extração a 840
  linhas. **Removido.**
- ⏳ **PENDENTE (usuário buscando info) — o que mais conta como demanda?** Restam
  `GERA_DEMANDA` (bit; hoje filtrado em 1 — só 419 linhas de 278.810 são 0),
  `OPERACAO_FISCAL` (venda vs devolução) e `CANAL_VENDA`. `QUANTIDADE_DEMANDA` foi
  descartada em favor de `QUANTIDADE`.
- ❓ **Janela temporal:** 61,6M linhas — a extração na Retiro **precisa** de filtro de
  período (ex.: últimos 2-3 anos), senão não sai no SSMS.
- ✅ **PII:** `CLIENTE`, `VENDEDOR`, `MEDICO`, `CR_PRESCRITOR`, `UF_PRESCRITOR` são dados
  pessoais — **não entram** no Stage; a agregação diária já os elimina.

**SELECT atual (rascunho — decisões pendentes):**

```sql
DECLARE @inicio date = '2023-01-01';   -- parametrizar a janela na Retiro
SELECT
    Data          = V.MOVIMENTO,
    LojaId        = CONVERT(int, V.EMPRESA),
    Sku           = CONVERT(varchar(30), V.PRODUTO),
    Quantidade    = CONVERT(decimal(12,3), SUM(V.QUANTIDADE)),
    ValorTotal    = CONVERT(decimal(14,4), SUM(COALESCE(V.VENDA_LIQUIDA, 0))),
    PrecoUnitario = CONVERT(decimal(12,4),
                       SUM(COALESCE(V.VENDA_LIQUIDA,0)) / NULLIF(SUM(V.QUANTIDADE), 0))
FROM dbo.VENDAS_ANALITICAS V
WHERE V.MOVIMENTO >= @inicio
  AND V.GERA_DEMANDA = 1                    -- só o que o PBS considera demanda
  -- NÃO filtrar TIPO_BONIFICACAO: 'P' é o caso normal (99,7%), não brinde.
GROUP BY V.MOVIMENTO, V.EMPRESA, V.PRODUTO
HAVING SUM(V.QUANTIDADE) <> 0;
```

**Pendências Vendas:**
- [x] `Quantidade` = `QUANTIDADE` (quantidade de venda).
- [x] `ValorTotal` = `VENDA_LIQUIDA`.
- [ ] ⏳ Filtro de demanda (`GERA_DEMANDA` / bonificação / `OPERACAO_FISCAL`) — **usuário buscando informações**.
- [ ] Definir janela temporal para a Retiro.
- [ ] Validar shape via `sp_describe_first_result_set`.

---

## EstoquesDiarios 🟡

**Origem:** `dbo.ESTOQUE_LANCAMENTOS` (52,6M linhas / 3,7 GB) + **`dbo.ESTOQUE_ATUAL`** como
âncora. Investigado e **executado com dados reais** na NatusFarma.

> ⚠️ **`ESTOQUE_LANCAMENTOS.ESTOQUE_SALDO` vem NULL em 100% das linhas** (medido: 0 de
> 404.746 numa amostra de 5 lojas / 8 semanas). O saldo corrente por lançamento **não é
> utilizável**. `ESTOQUE_ENTRADA` e `ESTOQUE_SAIDA` estão sempre preenchidas, e
> `ESTOQUE_ATUAL.ESTOQUE_SALDO` também (61.270/61.270). Por isso o saldo diário é
> **reconstruído para trás** a partir da foto de hoje:
> `saldo(fim do dia D) = saldo_hoje − Σ(entradas − saídas) posteriores a D`.
> Consequência prática: a query lê movimentos de `@dataInicial` **até hoje** (sem corte
> superior) e só depois recorta o período.

Descartadas:
- `ESTOQUE_ATUAL` — chave (`PRODUTO`,`CENTRO_ESTOQUE`) **sem data**: é só a foto atual, sem histórico.
- `ESTOQUE_LANCAMENTOS_CACHE` — cache de **ajustes** (`SALDO_AJUSTE`/`TIPO_AJUSTE`), não foto diária.

**Mapeamento centro → loja:** `ESTOQUE_LANCAMENTOS.CENTRO_ESTOQUE` →
`CENTROS_ESTOQUE.OBJETO_CONTROLE` → `CENTROS_ESTOQUE.EMPRESA` (= `LojaId`).
Filtrar **`TIPO_ESTOQUE = 2` ("RETAGUARDA DE LOJA")** — na NatusFarma são 138 centros para
138 empresas (1:1). Tipo 1 = "DEPOSITO" (21 centros / 3 empresas = CDs, fora do escopo);
tipo 3 = "FRENTE DE LOJA" existe no dicionário mas **sem centros** na NatusFarma.

| # | Campo Stage | Tipo | Null | Origem PBS | Transformação | Status | Obs |
|---|---|---|:--:|---|---|:--:|---|
| 1 | `Data` | date | N | `ESTOQUE_LANCAMENTOS.DATA` | `CONVERT(date,…)` | ✅ | |
| 2 | `LojaId` | int | N | `CENTROS_ESTOQUE.EMPRESA` | join por `CENTRO_ESTOQUE` | ✅ | filtrar `TIPO_ESTOQUE=2` |
| 3 | `Sku` | string | N | `ESTOQUE_LANCAMENTOS.PRODUTO` | `CONVERT(varchar30)` | ✅ | FK p/ `PRODUTOS` |
| 4 | `QuantidadeEmEstoque` | decimal | N | `ESTOQUE_SALDO` do **último** lançamento do dia | `ROW_NUMBER()` | 🟡 | ver problema do carry-forward |

### ⚠️ Dois problemas estruturais (decisão necessária)

**1. Histórico curto.** Na NatusFarma `ESTOQUE_LANCAMENTOS` cobre só
**2025-09-29 → 2026-07-24 (~10 meses)** — o menor `ESTOQUE` (PK identity) é 2.238.538, ou
seja, movimentos antigos foram expurgados. Vendas têm histórico muito maior, então a janela
**conjunta vendas+estoque** fica limitada a ~10 meses. Confirmar o mesmo na Retiro.

**2. Carry-forward é obrigatório (não opcional).** O lançamento só existe em dias com
movimento. Se exportarmos apenas esses dias, o [FeatureBuilder](../CosmosPro.ML.DemandForCast.Features/FeatureBuilder.cs)
densifica a série preenchendo dias faltantes com `Quantidade = 0` e **`EmRuptura = false`**
(linhas 117-142) e `IsValidTarget = !EmRuptura` (linha 75) — logo um dia **em ruptura**
(sem movimento justamente porque não havia estoque) entraria como **alvo válido com demanda 0**.
Isso é exatamente o anti-pattern do CLAUDE.md ("venda = 0 como demanda = 0 sem checar ruptura
→ viés sistemático para baixo"). Opções:
- **(a)** gerar a grade diária completa no SQL (calendário × loja × SKU) com o último saldo conhecido;
- **(b)** exportar esparso e implementar carry-forward no import/Features (mudança de código);
- **(c)** reduzir escopo (piloto: N lojas × top SKUs) e aí a grade completa fica viável.

**Volume:** a grade completa da rede (138 lojas × ~59,8k produtos × ~300 dias ≈ **2,5 bilhões**
de linhas) é inviável — daí o piloto de 5 lojas. Medido na prática: 5 lojas × 8 semanas com
carry-forward = **1.723.840 linhas** (ZIP total de 7,24 MB).

**Decisão tomada:** opção **(c) + carry-forward em C#** — o
[StockCarryForward](../CosmosPro.ML.DemandForCast.Extractor/StockCarryForward.cs) densifica a
série durante o streaming, sem SQL gigante e sem alterar o Features.

**Query:** implementada em
[`Queries/estoques_movimentos.sql`](../CosmosPro.ML.DemandForCast.Extractor/Queries/estoques_movimentos.sql)
(reconstrução do saldo ancorada em `ESTOQUE_ATUAL`, conforme o aviso acima).

**Pendências EstoquesDiarios:**
- [x] Carry-forward: resolvido em C# no extrator (opção b/c combinadas).
- [x] Escopo do piloto: 5 lojas, todos os SKUs com movimento.
- [ ] Confirmar na Retiro: janela de `ESTOQUE_LANCAMENTOS`, se `ESTOQUE_SALDO` também vem
      NULL, e se usa `FRENTE DE LOJA` (tipo 3) além de retaguarda — se usar, o saldo da loja
      é a **soma** dos dois centros.
- [x] Execução validada com dados reais na NatusFarma.

---

## Compras ✅

**Origem:** `dbo.PEDIDOS_COMPRAS` (732k) + `dbo.PEDIDOS_COMPRAS_PRODUTOS` (1,35M itens).
Shape validado na NatusFarma via `sp_describe_first_result_set`.

| # | Campo Stage | Tipo | Null | Origem PBS | Transformação | Status | Obs |
|---|---|---|:--:|---|---|:--:|---|
| 1 | `DataPedido` | date | N | `PEDIDOS_COMPRAS.DATA_HORA` | `CONVERT(date,…)` | ✅ | |
| 2 | `DataRecebimento` | date | S | `PEDIDOS_COMPRAS.DATA_ENTREGA` | `CONVERT(date,…)` | 🟡 | é a entrega **prevista**, não o recebimento real — lead time realizado exigiria a NF de entrada |
| 3 | `LojaId` | int | N | `PEDIDOS_COMPRAS.EMPRESA` | `CONVERT(int,…)` | ✅ | |
| 4 | `Sku` | string | N | `PEDIDOS_COMPRAS_PRODUTOS.PRODUTO` | `CONVERT(varchar30)` | ✅ | |
| 5 | `Quantidade` | decimal | N | `SUM(PEDIDOS_COMPRAS_PRODUTOS.QUANTIDADE)` | agregado | ✅ | |
| 6 | `Fornecedor` | string | S | `ENTIDADES.NOME` (via `PEDIDOS_COMPRAS.ENTIDADE`) | `LEFT(…,120)` | ✅ | |

Query implementada em `CosmosPro.ML.DemandForCast.Extractor/Queries/compras.sql`.

---

## Promocoes ✅

**Origem:** `dbo.PROMOCOES_FLEXIVEIS` (+ `_EMPRESAS`, `_LEVE`, `_GANHE`).
⚠️ A tabela `PROMOCOES` "simples" está **vazia** (assim como `PROMOCOES_EMPRESAS` e
`PROMOCOES_PRODUTOS`) — a rede usa **promoções flexíveis**. Shape validado na NatusFarma.

| # | Campo Stage | Tipo | Null | Origem PBS | Transformação | Status | Obs |
|---|---|---|:--:|---|---|:--:|---|
| 1 | `DataInicio` | date | N | `PROMOCOES_FLEXIVEIS.VALIDADE_INI` | `CONVERT(date,…)` | ✅ | |
| 2 | `DataFim` | date | N | `PROMOCOES_FLEXIVEIS.VALIDADE_FIM` | `CONVERT(date,…)` | ✅ | |
| 3 | `Sku` | string | N | `_LEVE.PRODUTO` / `_GANHE.PRODUTO` | UNION ALL | ✅ | |
| 4 | `LojaId` | int | S | `_EMPRESAS.EMPRESA` | join | ✅ | promoção é por loja |
| 5 | `Tipo` | string | S | `TIPOS_PROMOCOES_FLEXIVEIS.DESCRICAO` | `LEFT(…,40)` | ✅ | |
| 6 | `DescontoPct` | decimal | S | `_LEVE.DESCONTO_PADRAO` / `_GANHE.DESCONTO` | direto | ✅ | o percentual já existe pronto — não foi preciso derivar de "leve X pague Y" |

Query implementada em `CosmosPro.ML.DemandForCast.Extractor/Queries/promocoes.sql`.

---

## MercadoIqvia — removida do escopo do extrator (F16)

Resolvido fora do PBS: o dado de mercado é o **relatório mensal XLSX da IQVIA**
(cross-tab EAN × brick × bandeira × mês), enviado pelo comprador na tela `/mercado` e
gravado no banco `engine` (`MercadoObservacoes` etc. — ver CLAUDE.md §4). O extrator não
gera mais o `mercado_iqvia.csv` (que sempre saiu vazio, por não ter fonte no ERP) e a
tabela `Stage.dbo.MercadoIqvia` foi removida. A única relação com o PBS que restou é o
**CNPJ** em `Lojas`, extraído de `ENTIDADES` (ver seção Lojas), que faz a ponte entre as
lojas da rede e o painel de PDVs do relatório.

---

## SinaisExternos ⏳

Fonte externa (clima/gripe). **Fora do PBS.**

| # | Campo Stage | Tipo | Null |
|---|---|---|:--:|
| 1 | `Data` | date | N |
| 2 | `Geografia` | string | N |
| 3 | `Tipo` | string | N |
| 4 | `Valor` | decimal | N |

---

## Decisões e perguntas em aberto

- **Item 5 — ListaControle:** usuário verificando fonte da tarja/lista no PBS.
- **Versão do SQL Server na Retiro:** define `STRING_AGG` (2017+) vs `FOR XML PATH`.
- **O que roda no host da Retiro** além do SSMS (`sqlcmd`/`bcp`/PowerShell) e se dá
  para copiar arquivos para fora — decide o formato de entrega das tabelas grandes.
- **Volume** de `Vendas`/`EstoquesDiarios` — grid do SSMS vs streaming para arquivo.
- **Carry-forward de estoque** (a/b/c) — decisão que afeta diretamente a qualidade do treino.
- **Escopo do piloto** (lojas × SKUs) caso a grade diária completa seja inviável.

## Log de progresso

- **2026-07-10:** Conexão MCP à instância PBS de referência OK (era env var fora do
  processo). `Produtos` mapeado (PRODUTOS + EAN + Seção/Grupo + Marca + DCB); `ABC_FARMA_EDI_PRODUTOS`
  está vazia; `RegistroAnvisa` e `ClasseTerapeutica` indisponíveis neste PBS. Demais
  tabelas com stub aguardando investigação.
- **2026-07-10:** Shape do SELECT de `Produtos` validado via `sp_describe_first_result_set`
  (12 colunas, tipos cabem no Stage, zero linha lida). Tipagem explícita aplicada
  (`PrincipioAtivo` → varchar(200); `Ativo` → bit).
- **2026-07-10:** `Lojas` mapeado a partir de `EMPRESAS_USUARIAS` (não existe tabela
  `EMPRESAS` pura). Endereço via `ENDERECOS` (tem `CIDADE`/`ESTADO` diretos), Regiao via
  `REGIONAIS`, Perfil via `TIPOS_REDES`. Sem fonte para `DiasOperacaoSemana` (NOT NULL) e
  `DataAbertura` — marcados para revisitar. Chave da loja (`EMPRESA_USUARIA` vs `FILIAL`)
  a confirmar junto das tabelas-fato.
- **2026-07-10:** `Vendas` mapeado a partir de `VENDAS_ANALITICAS` (61,6M linhas, 129 col).
  Grão diário → agregação por (`MOVIMENTO`, `EMPRESA`, `PRODUTO`). Chave de loja
  confirmada = `EMPRESA`. Decisões em aberto: `QUANTIDADE` vs `QUANTIDADE_DEMANDA`,
  valor (`VENDA_LIQUIDA`/`BRUTA`/`VALOR_TOTAL_ITEM`), filtro de demanda (`GERA_DEMANDA`,
  bonificação, devolução) e janela temporal. PII (cliente/vendedor/médico/prescritor) fora.
- **2026-07-10:** `Vendas` — decidido `Quantidade`=`QUANTIDADE` e `ValorTotal`=`VENDA_LIQUIDA`.
  Filtro de demanda (item 3) segue pendente (usuário buscando info).
- **2026-07-10:** ⚠️ **Divergência de schema Pensefarma × Retiro confirmada:** `PRODUTOS_EAN.ATIVO`
  existe na Pensefarma mas **não** na Retiro. Removido do SELECT de `Produtos`. Sinal de que
  as queries validadas na Pensefarma precisam de um teste de compilação também na Retiro
  (versões/customizações do PBS diferem).
- **2026-07-10:** Mais divergências na Retiro: **`REGIONAIS` e `TIPOS_REDES` não existem**
  (ambas criadas em 2019-02-20 na Pensefarma → a Retiro roda PBS mais antigo ou sem esses
  módulos) e **`ENDERECOS.ATIVO` não existe**. `Lojas.Regiao` e `Lojas.Perfil` passam a sair
  NULL. **Regra prática daqui pra frente:** preferir colunas do núcleo antigo do PBS e tratar
  tabelas/colunas pós-2019 como opcionais.
- **2026-07-24:** 🎉 **NatusFarma autorizou o uso dos dados** e o MCP `mssql-natusfarma-pbs-prod`
  está conectado (porta 1435 — a config exigiu host e porta separados, `host,porta` não é aceito
  pelo driver tedious). Agora há um ambiente PBS onde as queries podem ser **executadas com dados**.
- **2026-07-24:** `EstoquesDiarios` mapeado: `ESTOQUE_LANCAMENTOS` (saldo corrente por movimento)
  + `CENTROS_ESTOQUE` (`TIPO_ESTOQUE=2` = retaguarda de loja, 1:1 com empresa). `ESTOQUE_ATUAL` e
  `..._CACHE` descartadas. **Dois bloqueios levantados:** histórico de só ~10 meses
  (2025-09-29→2026-07-24) e necessidade de **carry-forward** — sem ele o FeatureBuilder marca
  dia de ruptura como alvo válido com demanda 0 (viés para baixo).
- **2026-07-27:** Escopo do piloto fixado (5 lojas, todos os SKUs, carry-forward) e criado
  o projeto **`CosmosPro.ML.DemandForCast.Extractor`** (WinForms self-contained) — substitui
  a extração manual via SSMS. `Compras` e `Promocoes` mapeadas e validadas; a `PROMOCOES`
  simples está vazia, a rede usa promoções flexíveis (que já trazem o percentual pronto).
  O carry-forward roda em C# no streaming, resolvendo o bloqueio do estoque.
  26 testes passando (contrato de headers vs. Worker/ApiService, carry-forward, CSV).
- **2026-07-27:** 🚀 **Extração real executada com sucesso na NatusFarma** — 5 lojas
  (2,3,4,6,7 / MG), 2026-06-01 a 2026-07-24, ZIP de 7,24 MB:

  | arquivo | linhas |
  |---|---:|
  | lojas.csv | 5 |
  | produtos.csv | 79.678 |
  | vendas.csv | 187.223 |
  | estoques_diarios.csv | 1.723.840 |
  | compras.csv | 17.307 |
  | promocoes.csv | 9.211 |
  | mercado_iqvia.csv | 0 (só header, por design) |

  **Quatro defeitos só apareceram com dado real** (nenhum seria pego por metadados):
  *(nota F16: a linha `mercado_iqvia.csv` acima é histórica — o arquivo saiu do ZIP)*
  1. **Logon trigger** — o PBS recusa conexão por `APP_NAME()`. Um `ApplicationName`
     próprio na connection string era barrado ("falha devido à execução do acionador").
     Agora é configurável em `extrator.config.json` (vazio = default do provider), com
     mensagem de erro específica para o SQL 17892. **Vale conferir isso na Retiro.**
  2. **`ESTOQUE_SALDO` 100% NULL** → saldo reconstruído a partir de `ESTOQUE_ATUAL`.
  3. **Filtro de bonificação invertido** → zerava as vendas.
  4. **Faixas de promoção duplicando linhas** → colapsadas no maior desconto.
- **2026-08-27 (F16):** Documento posto em dia com o extrator real e com a F16:
  (1) nova seção **"Objetos PBS que o extrator toca"** — lista consolidada para o GRANT do
  DBA, derivada de `Queries/*.sql`; ela **não existia** e as tabelas de sugestão
  (`SUGESTOES_COMPRAS`, `SUGESTOES_COMPRAS_RESULTADO`, `TIPOS_CALCULO_SUGESTAO`, F12/F14)
  eram lidas sem constar aqui. (2) `Lojas.Cnpj` novo, via `ENTIDADES.CGC` — nome da coluna
  **assumido**, confirmar na Retiro/NatusFarma. (3) `MercadoIqvia` saiu do escopo do
  extrator: o dado de mercado agora é o XLSX da IQVIA enviado em `/mercado`, gravado no
  `engine`; `Stage.dbo.MercadoIqvia` foi removida.
