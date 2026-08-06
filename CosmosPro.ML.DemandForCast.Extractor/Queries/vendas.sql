-- ESCOPO POR SKU: @skus traz os produtos da sugestao, num parametro unico lido por
-- STRING_SPLIT. Um parametro por SKU estouraria o teto de 2.100 do SQL Server numa
-- sugestao grande (1.695 SKUs + 93 lojas ja da 1.788) -- e estouraria em producao.
-- Stage.Vendas <- VENDAS_ANALITICAS, agregado para o grão diário (Data, Loja, Sku).
-- MOVIMENTO é a data do movimento; DATA é o getdate() da inclusão e não serve.
-- Não há preço unitário na origem — é derivado do valor líquido sobre a quantidade.
-- A agregação diária também elimina o PII da tabela (cliente, vendedor, prescritor).
-- NÃO filtrar por TIPO_BONIFICACAO: medido em dados reais, 'P' responde por
-- 99,7% das linhas (277.551 de 278.810 numa amostra de 5 lojas), ou seja é o
-- caso normal e não brinde. Excluí-lo zerava a extração.
-- PENDENTE: confirmar a semântica de GERA_DEMANDA e se devoluções
-- (OPERACAO_FISCAL) já ficam de fora.
SELECT
    Data          = V.MOVIMENTO,
    LojaId        = CONVERT(int, V.EMPRESA),
    Sku           = CONVERT(varchar(30), V.PRODUTO),
    Quantidade    = CONVERT(decimal(12,3), SUM(V.QUANTIDADE)),
    PrecoUnitario = CONVERT(decimal(12,4), SUM(COALESCE(V.VENDA_LIQUIDA, 0)) / NULLIF(SUM(V.QUANTIDADE), 0)),
    ValorTotal    = CONVERT(decimal(14,4), SUM(COALESCE(V.VENDA_LIQUIDA, 0)))
FROM dbo.VENDAS_ANALITICAS V
WHERE V.EMPRESA IN ({{LOJAS}})
  AND V.MOVIMENTO >= @dataInicial
  AND V.MOVIMENTO <= @dataFinal
  AND V.GERA_DEMANDA = 1
  AND V.PRODUTO IN (SELECT CONVERT(numeric(15,0), value) FROM STRING_SPLIT(@skus, ','))
GROUP BY V.MOVIMENTO, V.EMPRESA, V.PRODUTO
HAVING SUM(V.QUANTIDADE) <> 0
ORDER BY V.MOVIMENTO, V.EMPRESA, V.PRODUTO;
