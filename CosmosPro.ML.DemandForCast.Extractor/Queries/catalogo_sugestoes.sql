-- Catalogo para o usuario escolher a sugestao. Traz contagem de linhas e lojas para
-- ele ver o tamanho antes de extrair, e DiasCoberturaMax (o maior DIAS_CURVA_*) para
-- derivar a janela.
SELECT
    SugestaoId        = CONVERT(bigint, S.SUGESTAO_COMPRA),
    Descricao         = LEFT(S.DESCRICAO, 100),
    DataHora          = CONVERT(datetime2(0), S.DATA_HORA),
    TipoCalculo       = CONVERT(tinyint, S.TIPO_CALCULO),
    DiasCoberturaMax  = CONVERT(int, (SELECT MAX(v) FROM (VALUES
                            (S.DIAS_CURVA_A), (S.DIAS_CURVA_B), (S.DIAS_CURVA_C),
                            (S.DIAS_CURVA_D), (S.DIAS_CURVA_E)) AS t(v))),
    QtdLinhas         = COUNT(R.SUGESTAO_COMPRA_RESULTADO),
    QtdLojas          = COUNT(DISTINCT R.FILIAL)
FROM dbo.SUGESTOES_COMPRAS S
JOIN dbo.SUGESTOES_COMPRAS_RESULTADO R ON R.SUGESTAO_COMPRA = S.SUGESTAO_COMPRA
WHERE S.TIPO_CALCULO IS NOT NULL
  AND S.DATA_HORA >= {{DATA_INICIO}}
GROUP BY S.SUGESTAO_COMPRA, S.DESCRICAO, S.DATA_HORA, S.TIPO_CALCULO,
         S.DIAS_CURVA_A, S.DIAS_CURVA_B, S.DIAS_CURVA_C, S.DIAS_CURVA_D, S.DIAS_CURVA_E
ORDER BY S.DATA_HORA DESC;
