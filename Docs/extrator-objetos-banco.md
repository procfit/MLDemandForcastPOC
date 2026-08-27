# Extrator PBS — objetos de banco de dados acessados

Documento de apoio para a equipe de banco de dados da rede. Descreve **todos** os objetos que o executável `extrator.exe` consulta no banco do PBS, o tipo de acesso exigido e os pré-requisitos técnicos do ambiente.

| | |
|---|---|
| Aplicação | `extrator.exe` (CosmosPro — Extrator PBS) |
| Banco de origem | Banco de produção do PBS (nome informado na tela de conexão) |
| Schema | `dbo` (único) |
| Tipo de acesso | **Somente leitura** |
| Nº de objetos acessados | 23 tabelas |
| Documento gerado em | 27/08/2026 |
| Substitui a versão de | 06/08/2026 — ver §8; **a lista de objetos não mudou** |

## 1. Natureza do acesso

O extrator executa **exclusivamente comandos `SELECT`**. Especificamente, o executável:

- **não** executa `INSERT`, `UPDATE`, `DELETE`, `MERGE` ou `TRUNCATE`;
- **não** executa DDL (`CREATE`, `ALTER`, `DROP`) — não cria tabelas temporárias físicas, views, índices nem objetos auxiliares;
- **não** chama nenhuma *stored procedure* nem *function* do PBS;
- **não** usa `sp_` de sistema, nem lê catálogos (`sys.*`, `INFORMATION_SCHEMA.*`);
- **não** abre transações explícitas.

Todas as consultas são texto SQL fixo, embarcado no executável (15 arquivos `.sql` compilados como recurso), parametrizado via `Microsoft.Data.SqlClient`. O resultado é gravado em arquivos CSV compactados (`.zip`) na estação de trabalho do usuário — nada retorna para o banco de origem.

**Concorrência:** as consultas não usam *hints* (`NOLOCK`/`READUNCOMMITTED`) — rodam no nível de isolamento padrão da instância. Se a instância não estiver com `READ_COMMITTED_SNAPSHOT` habilitado e houver preocupação com bloqueio, a alternativa recomendada é apontar o extrator para uma **réplica de leitura** ou para um *restore* recente; o extrator funciona igualmente bem em qualquer cópia do banco.

## 2. Objetos acessados

### 2.1 Sugestão de compra (origem da comparação)

| # | Tabela | Colunas lidas | Uso |
|---|---|---|---|
| 1 | `dbo.SUGESTOES_COMPRAS` | `SUGESTAO_COMPRA`, `DESCRICAO`, `DATA_HORA`, `TIPO_CALCULO`, `LEADTIME`, `DIAS_CURVA_A`, `DIAS_CURVA_B`, `DIAS_CURVA_C`, `DIAS_CURVA_D`, `DIAS_CURVA_E`, `EFETIVIDADE`, `PEDIDOS_PENDENTES`, `ESTOQUE_ZERADO` | Catálogo de sugestões para o usuário escolher, e cabeçalho/parâmetros da sugestão escolhida |
| 2 | `dbo.SUGESTOES_COMPRAS_RESULTADO` | `SUGESTAO_COMPRA`, `SUGESTAO_COMPRA_RESULTADO`, `EMPRESA`, `FILIAL`, `PRODUTO`, `CURVA`, `DEMANDA_DIA`, `DEMANDA_DIA_PONDERADA`, `ESTOQUE_SALDO`, `ESTOQUE_SEGURANCA`, `ESTOQUE_MAXIMO`, `ESTOQUE_MINIMO`, `DIAS_ESTOQUE`, `PEDIDOS_PENDENTES`, `COMPRA_SUGERIDA`, `COMPRA_AUTORIZADA`, `PRECO_COMPRA`, `FATOR_EMBALAGEM`, `FALTEIRO` | Itens da sugestão escolhida; também define o escopo (lojas e SKUs) de todas as demais consultas |

> **Tabela de maior volume.** Todos os acessos a `SUGESTOES_COMPRAS_RESULTADO` são filtrados por `SUGESTAO_COMPRA` (com o operador de igualdade ou com lista preenchida apenas com ids) — nunca por faixa de datas. Um índice que atenda a esse predicado é o único ponto de atenção de desempenho do extrator.

### 2.2 Cadastro de lojas

| # | Tabela | Colunas lidas | Uso |
|---|---|---|---|
| 3 | `dbo.EMPRESAS_USUARIAS` | `EMPRESA_USUARIA`, `NOME`, `NOME_FANTASIA`, `ENTIDADE`, `ATIVO` | Identificação das lojas; lista de lojas ativas no teste de conexão |
| 4 | `dbo.ENDERECOS` | `ENTIDADE`, `ENDERECOS`, `ESTADO`, `CIDADE` | UF e cidade da loja |

### 2.3 Cadastro de produtos

| # | Tabela | Colunas lidas | Uso |
|---|---|---|---|
| 5 | `dbo.PRODUTOS` | `PRODUTO`, `DESCRICAO`, `DESCRICAO_REDUZIDA`, `SECAO_PRODUTO`, `GRUPO_PRODUTO`, `MARCA`, `CADASTRO_ATIVO` | Mestre de produtos (apenas os SKUs da sugestão) |
| 6 | `dbo.SECOES_PRODUTOS` | `SECAO_PRODUTO`, `DESCRICAO` | Categoria |
| 7 | `dbo.GRUPOS_PRODUTOS` | `GRUPO_PRODUTO`, `DESCRICAO` | Subcategoria |
| 8 | `dbo.MARCAS` | `MARCA`, `DESCRICAO` | Fabricante/marca |
| 9 | `dbo.PRODUTOS_EAN` | `PRODUTO`, `PRODUTO_EAN`, `EAN_FORMATADO`, `EAN_PRINCIPAL`, `EAN_INTERNO` | Código de barras principal |
| 10 | `dbo.PRODUTOS_DCB` | `PRODUTO`, `DCB`, `APRESENTACAO` | Princípio ativo e apresentação |
| 11 | `dbo.DCB_MEDICAMENTOS` | `DCB`, `DESCRICAO` | Descrição do princípio ativo |

### 2.4 Vendas

| # | Tabela | Colunas lidas | Uso |
|---|---|---|---|
| 12 | `dbo.VENDAS_ANALITICAS` | `MOVIMENTO`, `EMPRESA`, `PRODUTO`, `QUANTIDADE`, `VENDA_LIQUIDA`, `GERA_DEMANDA` | Histórico de vendas, **agregado ao grão diário** por (data, loja, produto) |

> **Nenhum dado pessoal é extraído.** A agregação diária acontece dentro da própria consulta, no servidor: as colunas de cliente, vendedor e prescritor de `VENDAS_ANALITICAS` **não são lidas** e não saem do banco. O arquivo gerado contém apenas quantidade e valor por dia/loja/produto.

### 2.5 Estoque

| # | Tabela | Colunas lidas | Uso |
|---|---|---|---|
| 13 | `dbo.ESTOQUE_LANCAMENTOS` | `CENTRO_ESTOQUE`, `PRODUTO`, `DATA`, `ESTOQUE_ENTRADA`, `ESTOQUE_SAIDA` | Movimentação, para reconstruir o saldo de fim de dia |
| 14 | `dbo.CENTROS_ESTOQUE` | `OBJETO_CONTROLE`, `EMPRESA`, `TIPO_ESTOQUE` | Filtro de estoque de loja (`TIPO_ESTOQUE = 2`, retaguarda de loja) |
| 15 | `dbo.ESTOQUE_ATUAL` | `CENTRO_ESTOQUE`, `PRODUTO`, `ESTOQUE_SALDO` | Saldo atual, usado como âncora da reconstrução histórica |

### 2.6 Compras

| # | Tabela | Colunas lidas | Uso |
|---|---|---|---|
| 16 | `dbo.PEDIDOS_COMPRAS` | `PEDIDO_COMPRA`, `DATA_HORA`, `DATA_ENTREGA`, `EMPRESA`, `ENTIDADE` | Cabeçalho do pedido de compra (lead time) |
| 17 | `dbo.PEDIDOS_COMPRAS_PRODUTOS` | `PEDIDO_COMPRA`, `PRODUTO`, `QUANTIDADE` | Itens do pedido |
| 18 | `dbo.ENTIDADES` | `ENTIDADE`, `NOME`, `CGC` | Nome do fornecedor (pedidos de compra) e **CNPJ da loja** (cadastro de lojas) |

> **`ENTIDADES.CGC` é leitura nova nesta versão** (ver §8). O CNPJ da filial passou a ser exportado para permitir cruzar as lojas da rede com o painel de pontos de venda do relatório de mercado. É o CNPJ da própria rede — nenhum dado de pessoa física é lido desta tabela.

### 2.7 Promoções

| # | Tabela | Colunas lidas | Uso |
|---|---|---|---|
| 19 | `dbo.PROMOCOES_FLEXIVEIS` | `PROMOCAO_FLEXIVEL`, `VALIDADE_INI`, `VALIDADE_FIM`, `TIPO_PROMOCAO_FLEXIVEL` | Vigência da promoção |
| 20 | `dbo.PROMOCOES_FLEXIVEIS_EMPRESAS` | `PROMOCAO_FLEXIVEL`, `EMPRESA` | Lojas participantes |
| 21 | `dbo.PROMOCOES_FLEXIVEIS_LEVE` | `PROMOCAO_FLEXIVEL`, `PRODUTO`, `DESCONTO_PADRAO` | Produtos e desconto (modalidade "leve") |
| 22 | `dbo.PROMOCOES_FLEXIVEIS_GANHE` | `PROMOCAO_FLEXIVEL`, `PRODUTO`, `DESCONTO` | Produtos e desconto (modalidade "ganhe") |
| 23 | `dbo.TIPOS_PROMOCOES_FLEXIVEIS` | `TIPO_PROMOCAO_FLEXIVEL`, `DESCRICAO` | Descrição do tipo de promoção |

## 3. Permissões necessárias

O conjunto mínimo é `CONNECT` no banco mais `SELECT` nas 23 tabelas acima. Script sugerido para criação de um usuário dedicado:

```sql
-- 1) Login no servidor (ajuste o nome e use uma senha forte)
USE [master];
GO
CREATE LOGIN [svc_cosmospro_extrator]
    WITH PASSWORD = N'<senha-forte-aqui>',
         CHECK_POLICY = ON,
         DEFAULT_DATABASE = [<banco_do_pbs>];
GO

-- 2) Usuário no banco do PBS
USE [<banco_do_pbs>];
GO
CREATE USER [svc_cosmospro_extrator] FOR LOGIN [svc_cosmospro_extrator];
GO

-- 3) SELECT apenas nos objetos utilizados
GRANT SELECT ON dbo.SUGESTOES_COMPRAS            TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.SUGESTOES_COMPRAS_RESULTADO  TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.EMPRESAS_USUARIAS            TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.ENDERECOS                    TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PRODUTOS                     TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.SECOES_PRODUTOS              TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.GRUPOS_PRODUTOS              TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.MARCAS                       TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PRODUTOS_EAN                 TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PRODUTOS_DCB                 TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.DCB_MEDICAMENTOS             TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.VENDAS_ANALITICAS            TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.ESTOQUE_LANCAMENTOS          TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.CENTROS_ESTOQUE              TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.ESTOQUE_ATUAL                TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PEDIDOS_COMPRAS              TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PEDIDOS_COMPRAS_PRODUTOS     TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.ENTIDADES                    TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PROMOCOES_FLEXIVEIS          TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PROMOCOES_FLEXIVEIS_EMPRESAS TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PROMOCOES_FLEXIVEIS_LEVE     TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.PROMOCOES_FLEXIVEIS_GANHE    TO [svc_cosmospro_extrator];
GRANT SELECT ON dbo.TIPOS_PROMOCOES_FLEXIVEIS    TO [svc_cosmospro_extrator];
GO
```

Se a política da rede preferir um papel de banco em vez de *grants* individuais, `db_datareader` também atende — é mais permissivo que o necessário, mas continua sendo somente leitura.

O extrator aceita tanto **autenticação SQL** quanto **autenticação Windows** (integrada); neste segundo caso, conceda as permissões acima à conta de domínio ou ao grupo do AD que executará o programa.

## 4. Pré-requisitos técnicos

| Item | Requisito | Observação |
|---|---|---|
| Versão do SQL Server | **2017 (14.x) ou superior** | As consultas usam `STRING_SPLIT` (2016+) e `STRING_AGG ... WITHIN GROUP` (2017+) |
| Nível de compatibilidade do banco | **140 ou superior** | `STRING_SPLIT` e `STRING_AGG` não são reconhecidos abaixo disso, ainda que a instância seja recente |
| Porta | 1433 por padrão, configurável na tela de conexão | Liberação de rede da estação do usuário até a instância |
| Criptografia | O cliente conecta com `Encrypt=true` e `TrustServerCertificate=true` | Não exige certificado válido na instância |
| Driver | `Microsoft.Data.SqlClient` (aplicação .NET, Windows) | Nenhum componente precisa ser instalado no servidor |

### Gatilho de logon (*logon trigger*)

Algumas instalações do PBS têm um *logon trigger* que só aceita conexões de aplicações com determinados `APP_NAME()`. Se a conexão for recusada com a mensagem "*...devido à execução do acionador (trigger) de logon*", o extrator permite configurar o valor de **Application Name** enviado na conexão, sem necessidade de nova versão do programa. Por favor, informe qual valor deve ser usado, caso esse controle exista.

## 5. Perfil de carga

- A extração é **manual e pontual** — disparada por um operador, não é um processo agendado ou contínuo.
- Cada execução abre **uma única conexão** e roda as consultas em sequência.
- Todas as consultas de histórico (vendas, estoque, compras, promoções) são filtradas por:
  - a **lista de lojas** da sugestão escolhida;
  - a **lista de SKUs** da sugestão escolhida (tipicamente entre centenas e alguns milhares de itens, não o mestre inteiro);
  - uma **janela de datas** definida pelo operador.
- Duração típica observada em base de porte semelhante: poucos minutos por extração.
- As leituras curtas (catálogo, teste de conexão) têm *timeout* de 15–30 segundos; a extração em si roda sem limite de tempo, por natureza.

## 6. Objetos citados mas **não** acessados

Para evitar dúvida na revisão de permissões, estes objetos aparecem na documentação interna do projeto mas **não** são consultados pelo executável, e portanto **não** precisam de `GRANT`:

`dbo.TIPOS_CALCULO_SUGESTAO`, `dbo.PROMOCOES`, `dbo.ABC_FARMA_EDI_PRODUTOS`, `dbo.INDICACOES_TERAPEUTICAS`, `dbo.NF_COMPRA`, `dbo.REGIONAIS`, `dbo.TIPOS_REDES`.

## 7. Dados de mercado (IQVIA) — fora do PBS

A aplicação passou a usar também o relatório mensal de mercado da IQVIA, que a rede já recebe do fornecedor. **Esse dado não vem do PBS e não exige nada da equipe de banco de dados:** o arquivo `.xlsx` é enviado pelo próprio comprador, por uma tela da aplicação, e nunca passa pelo extrator. Ele é citado aqui apenas para deixar claro que **não** há objeto novo a liberar por causa dele.

## 8. O que mudou desde a versão de 06/08/2026

**A lista de objetos não mudou: continuam as mesmas 23 tabelas, e nenhum `GRANT` novo é necessário.** Se as permissões já foram concedidas conforme §3, nada precisa ser feito.

| Mudança | Impacto para o DBA |
|---|---|
| `dbo.ENTIDADES` passou a ter a coluna **`CGC`** lida, além de `ENTIDADE` e `NOME`. A tabela já constava da lista (era usada para o nome do fornecedor); agora também fornece o CNPJ da filial no cadastro de lojas. | **Nenhum** — o `GRANT SELECT` na tabela já cobre a coluna. Registrado por transparência. |
| O executável passou de 14 para **15** consultas embarcadas (uma consulta nova lista as lojas de uma sugestão, para o operador escolher quais exportar). | **Nenhum** — usa `SUGESTOES_COMPRAS_RESULTADO`, tabela e colunas já declaradas. |

## 9. Contato

Dúvidas sobre este documento ou sobre o comportamento do extrator: **Victor Perez** — victor.perez@procfit.com.br
