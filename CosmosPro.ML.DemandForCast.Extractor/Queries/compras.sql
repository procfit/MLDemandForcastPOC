-- ESCOPO POR SKU: @skus traz os produtos da sugestao, num parametro unico lido por
-- STRING_SPLIT. Um parametro por SKU estouraria o teto de 2.100 do SQL Server numa
-- sugestao grande (1.695 SKUs + 93 lojas ja da 1.788) -- e estouraria em producao.
-- Stage.Compras <- PEDIDOS_COMPRAS + PEDIDOS_COMPRAS_PRODUTOS.
-- DataRecebimento usa DATA_ENTREGA, que é a entrega *prevista* do pedido —
-- não a data real de recebimento da mercadoria. Suficiente para lead time
-- planejado; se o lead time realizado virar necessário, a fonte é a nota de
-- entrada (NF_COMPRA), ainda não mapeada.
SELECT
    DataPedido      = CONVERT(date, P.DATA_HORA),
    DataRecebimento = CONVERT(date, P.DATA_ENTREGA),
    LojaId          = CONVERT(int, P.EMPRESA),
    Sku             = CONVERT(varchar(30), I.PRODUTO),
    Quantidade      = CONVERT(decimal(12,3), SUM(I.QUANTIDADE)),
    Fornecedor      = CONVERT(varchar(120), LEFT(F.NOME, 120))
FROM dbo.PEDIDOS_COMPRAS P
JOIN dbo.PEDIDOS_COMPRAS_PRODUTOS I ON I.PEDIDO_COMPRA = P.PEDIDO_COMPRA
LEFT JOIN dbo.ENTIDADES F ON F.ENTIDADE = P.ENTIDADE
WHERE P.EMPRESA IN ({{LOJAS}})
  AND P.DATA_HORA >= @dataInicial
  AND P.DATA_HORA <  DATEADD(day, 1, @dataFinal)
  AND I.PRODUTO IN (SELECT CONVERT(numeric(15,0), value) FROM STRING_SPLIT(@skus, ','))
GROUP BY CONVERT(date, P.DATA_HORA), CONVERT(date, P.DATA_ENTREGA), P.EMPRESA, I.PRODUTO, F.NOME
HAVING SUM(I.QUANTIDADE) <> 0
ORDER BY 1, 3, 4;
