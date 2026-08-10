-- Lojas que UMA sugestao cita, com quantos itens caem em cada uma, para o comprador
-- escolher quais exportar. Barata pelo mesmo motivo das contagens: o filtro por
-- SUGESTAO_COMPRA e uma busca pelo indice, e o agrupamento acontece sobre o punhado
-- de linhas daquela sugestao -- nao sobre a tabela inteira.
--
-- FILIAL e numeric(5,0) nesta instalacao e o driver entrega numeric como
-- System.Decimal; sem o CONVERT, GetInt32 estoura InvalidCastException.
SELECT
    LojaId = CONVERT(int, R.FILIAL),
    Itens  = CONVERT(int, COUNT(*))
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}}
GROUP BY R.FILIAL
ORDER BY R.FILIAL;
