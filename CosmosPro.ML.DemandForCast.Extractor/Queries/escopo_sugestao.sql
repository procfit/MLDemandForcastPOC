-- Lojas e SKUs citados por UMA sugestão, para escopar as demais queries e garantir
-- a união dos produtos (ver ExtractionService.CopyProdutosGarantindoUniao).
-- Não vai para o Stage: o consumidor lê as duas colunas de forma tipada.
--
-- Todo numérico do PBS é declarado numeric(p,s) — nunca int/bigint —, e o driver
-- entrega numeric como System.Decimal. Sem o CONVERT explícito, GetInt32 estoura
-- InvalidCastException em tempo de execução. FILIAL é numeric(5,0) e PRODUTO é
-- numeric(15,0) nesta instalação.
SELECT DISTINCT
    LojaId = CONVERT(int, R.FILIAL),
    Sku    = CONVERT(varchar(30), R.PRODUTO)
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}};
