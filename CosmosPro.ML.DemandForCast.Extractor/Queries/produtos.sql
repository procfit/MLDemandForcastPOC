-- Stage.Produtos <- PRODUTOS (+ EAN, hierarquia, marca, DCB)
-- Exporta o mestre inteiro (não filtra por loja) para garantir a FK de
-- Vendas/Estoques/Compras/Promocoes no Stage.
-- RegistroAnvisa e ClasseTerapeutica não têm fonte confiável no PBS:
-- ABC_FARMA_EDI_PRODUTOS está vazia e INDICACOES_TERAPEUTICAS é degenerada.
-- ListaControle segue pendente de definição da fonte (LISTA_PNU vs CONTROLADO).
SELECT
    Sku               = CONVERT(varchar(30), P.PRODUTO),
    Nome              = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(P.DESCRICAO)), ''), P.DESCRICAO_REDUZIDA), 200),
    Categoria         = SEC.DESCRICAO,
    Subcategoria      = GRU.DESCRICAO,
    Fabricante        = MAR.DESCRICAO,
    PrincipioAtivo    = CONVERT(varchar(200), LEFT(PA.PrincipiosAtivos, 200)),
    Apresentacao      = CONVERT(varchar(120), LEFT(PA.Apresentacao, 120)),
    Ean               = EANP.EAN_FORMATADO,
    RegistroAnvisa    = CONVERT(varchar(20),  NULL),
    ListaControle     = CONVERT(varchar(10),  NULL),
    ClasseTerapeutica = CONVERT(varchar(120), NULL),
    Ativo             = CAST(CASE WHEN P.CADASTRO_ATIVO = 'S' THEN 1 ELSE 0 END AS bit)
FROM dbo.PRODUTOS P
LEFT JOIN dbo.SECOES_PRODUTOS SEC ON SEC.SECAO_PRODUTO = P.SECAO_PRODUTO
LEFT JOIN dbo.GRUPOS_PRODUTOS GRU ON GRU.GRUPO_PRODUTO = P.GRUPO_PRODUTO
LEFT JOIN dbo.MARCAS          MAR ON MAR.MARCA         = P.MARCA
OUTER APPLY (
    SELECT TOP 1 E.EAN_FORMATADO
    FROM dbo.PRODUTOS_EAN E
    WHERE E.PRODUTO = P.PRODUTO
    ORDER BY CASE WHEN E.EAN_PRINCIPAL = 'S' THEN 0 ELSE 1 END,
             CASE WHEN E.EAN_INTERNO   = 'N' THEN 0 ELSE 1 END,
             E.PRODUTO_EAN
) EANP
OUTER APPLY (
    SELECT PrincipiosAtivos = STRING_AGG(CONVERT(varchar(max), M.DESCRICAO), ' + ')
                                WITHIN GROUP (ORDER BY M.DESCRICAO),
           Apresentacao     = MAX(PD.APRESENTACAO)
    FROM dbo.PRODUTOS_DCB PD
    JOIN dbo.DCB_MEDICAMENTOS M ON M.DCB = PD.DCB
    WHERE PD.PRODUTO = P.PRODUTO
) PA
ORDER BY P.PRODUTO;
