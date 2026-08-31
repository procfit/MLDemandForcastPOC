-- Catálogo de códigos de barras da rede: o mestre INTEIRO, sem escopo por sugestão.
--
-- POR QUE SEM ESCOPO, ao contrário de produtos.sql: este arquivo existe para responder
-- "este produto que o mercado vende está no meu cadastro?", e um cadastro escopado à
-- sugestão responde outra pergunta -- "está nesta compra?". Contra o escopado, todo
-- produto fora da sugestão parece ausente do cadastro, e a tela de oportunidades de
-- sortimento vira uma lista de itens que a rede já vende.
--
-- Só código, SKU e nome: é o menor recorte que fecha a comparação com a IQVIA. Hierarquia,
-- preço e princípio ativo já viajam em produtos.csv para os SKUs que importam.
--
-- MESMA PREFERÊNCIA DE EAN de produtos.sql: principal antes de secundário, externo antes de
-- interno. EAN interno (código de balança, etiqueta da loja) não existe no cadastro da
-- IQVIA -- contá-lo como cobertura inflaria a comparação em silêncio.
--
-- O FILTRO FINAL NÃO É TRIVIAL. Produto sem EAN utilizável não responde a pergunta desta
-- consulta e ocuparia dois terços do arquivo: medido na Natusfarma em 2026-08-31, dos
-- 79.873 registros do mestre, 47.658 estão inativos e quase nenhum tem EAN -- dos 29.068
-- com EAN utilizável, 29.053 são ativos. O arquivo sai com ~29 mil linhas.
--
-- CADASTRO_ATIVO não filtra de propósito: incluir o registro inativo que TEM código deixa a
-- comparação mais conservadora (menos "você não tem isto" sobre item que a rede conhece), e
-- custa 15 linhas.
SELECT
    Sku  = CONVERT(varchar(30), P.PRODUTO),
    Ean  = EANP.EAN_FORMATADO,
    Nome = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(P.DESCRICAO)), ''), P.DESCRICAO_REDUZIDA), 200)
FROM dbo.PRODUTOS P
OUTER APPLY (
    SELECT TOP 1 E.EAN_FORMATADO
    FROM dbo.PRODUTOS_EAN E
    WHERE E.PRODUTO = P.PRODUTO
      AND E.EAN_FORMATADO IS NOT NULL
      AND E.EAN_INTERNO = 'N'
    ORDER BY CASE WHEN E.EAN_PRINCIPAL = 'S' THEN 0 ELSE 1 END,
             E.PRODUTO_EAN
) EANP
WHERE EANP.EAN_FORMATADO IS NOT NULL
ORDER BY P.PRODUTO;
