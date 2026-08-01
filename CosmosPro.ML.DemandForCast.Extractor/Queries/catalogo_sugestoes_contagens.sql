-- Contagens de linhas e lojas dos ids que catalogo_sugestoes.sql ja devolveu.
-- A lista fechada de {{SUGESTOES}} e o que torna isto barato: cada id vira uma
-- busca pelo indice de SUGESTAO_COMPRA, em vez da varredura que a versao com
-- JOIN por faixa de datas provocava.
-- Uma sugestao sem nenhuma linha de resultado nao aparece aqui; quem consome
-- assume zero para os ids ausentes (ver ExtractionService.MesclarCatalogo).
SELECT
    SugestaoId = CONVERT(bigint, R.SUGESTAO_COMPRA),
    QtdLinhas  = COUNT(R.SUGESTAO_COMPRA_RESULTADO),
    QtdLojas   = COUNT(DISTINCT R.FILIAL)
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
WHERE R.SUGESTAO_COMPRA IN ({{SUGESTOES}})
GROUP BY R.SUGESTAO_COMPRA;
