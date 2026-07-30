-- Diagnóstico, não vai para o Stage: conta linhas de SUGESTOES_COMPRAS_RESULTADO
-- em que EMPRESA diverge de FILIAL para a sugestão escolhida. sugestoes_compra_itens.sql
-- usa FILIAL como LojaId; os dois coincidem na NatusFarma mas isso não foi
-- validado na Retiro (item aberto do plano F12). QtdDivergencias > 0 é o sinal
-- para o extrator avisar o usuário em vez de seguir em silêncio.
SELECT
    QtdDivergencias = COUNT(*)
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}}
  AND R.EMPRESA <> R.FILIAL;
