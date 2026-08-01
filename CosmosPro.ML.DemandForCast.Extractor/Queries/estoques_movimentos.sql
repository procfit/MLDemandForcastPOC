-- Saldo de fim de dia por (Loja, Sku), reconstruído a partir dos movimentos.
--
-- ATENÇÃO: ESTOQUE_LANCAMENTOS.ESTOQUE_SALDO (saldo corrente) vem NULL em 100%
-- das linhas nesta instalação do PBS — não dá para usá-lo. O saldo é derivado
-- ancorando em ESTOQUE_ATUAL (a foto de hoje, essa sim sempre preenchida) e
-- caminhando para trás:
--     saldo(fim do dia D) = saldo_hoje - SOMA(entradas - saídas) de todo
--                           movimento posterior a D
-- Por isso o recorte inferior é @dataInicial mas NÃO há recorte superior: os
-- movimentos entre @dataFinal e hoje são necessários para a volta. O filtro do
-- período final é aplicado só na projeção.
--
-- Alimenta o StockCarryForward, que densifica a série — o ORDER BY por
-- (LojaId, Sku, Data) é OBRIGATÓRIO.
-- Só estoque de loja: TIPO_ESTOQUE = 2 ("RETAGUARDA DE LOJA").
WITH Diario AS (
    SELECT
        Empresa = CE.EMPRESA,
        Centro  = L.CENTRO_ESTOQUE,
        Produto = L.PRODUTO,
        Dia     = CONVERT(date, L.DATA),
        Liquido = SUM(COALESCE(L.ESTOQUE_ENTRADA, 0) - COALESCE(L.ESTOQUE_SAIDA, 0))
    FROM dbo.ESTOQUE_LANCAMENTOS L
    JOIN dbo.CENTROS_ESTOQUE CE ON CE.OBJETO_CONTROLE = L.CENTRO_ESTOQUE
    WHERE CE.TIPO_ESTOQUE = 2
      AND CE.EMPRESA IN ({{LOJAS}})
      AND L.DATA >= @dataInicial
    GROUP BY CE.EMPRESA, L.CENTRO_ESTOQUE, L.PRODUTO, CONVERT(date, L.DATA)
),
Reconstruido AS (
    SELECT
        D.Empresa,
        D.Produto,
        D.Dia,
        Saldo = EA.ESTOQUE_SALDO - COALESCE(
                    SUM(D.Liquido) OVER (
                        PARTITION BY D.Centro, D.Produto
                        ORDER BY D.Dia
                        ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING), 0)
    FROM Diario D
    -- INNER de propósito: produto sem saldo atual não tem âncora, e chutar zero
    -- inventaria ruptura.
    JOIN dbo.ESTOQUE_ATUAL EA
      ON EA.CENTRO_ESTOQUE = D.Centro
     AND EA.PRODUTO = D.Produto
)
SELECT
    LojaId              = CONVERT(int, R.Empresa),
    Sku                 = CONVERT(varchar(30), R.Produto),
    Data                = R.Dia,
    QuantidadeEmEstoque = CONVERT(decimal(12,3), R.Saldo)
FROM Reconstruido R
WHERE R.Dia <= @dataFinal
ORDER BY LojaId, Sku, Data;
