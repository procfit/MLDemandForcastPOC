-- Stage.Promocoes <- PROMOCOES_FLEXIVEIS (+ _EMPRESAS, _LEVE, _GANHE).
-- A tabela PROMOCOES "simples" está vazia no PBS analisado; o que a rede usa
-- são as promoções flexíveis, que trazem o percentual pronto
-- (_LEVE.DESCONTO_PADRAO e _GANHE.DESCONTO) — não é preciso derivar de leve/pague.
-- Promoções do tipo "leve X" têm FAIXAS por quantidade (ex.: 0/5/10/20% conforme
-- o volume), e o Stage não tem coluna de faixa. Colapsamos no MAIOR desconto da
-- promoção — é o desconto anunciado, e o que melhor explica o pico de demanda.
-- Faixa com desconto zero é descartada (não é promoção).
SELECT
    DataInicio  = CONVERT(date, PF.VALIDADE_INI),
    DataFim     = CONVERT(date, PF.VALIDADE_FIM),
    Sku         = CONVERT(varchar(30), X.PRODUTO),
    LojaId      = CONVERT(int, PE.EMPRESA),
    Tipo        = CONVERT(varchar(40), LEFT(T.DESCRICAO, 40)),
    DescontoPct = CONVERT(decimal(5,2), MAX(X.DESCONTO))
FROM dbo.PROMOCOES_FLEXIVEIS PF
JOIN dbo.PROMOCOES_FLEXIVEIS_EMPRESAS PE ON PE.PROMOCAO_FLEXIVEL = PF.PROMOCAO_FLEXIVEL
LEFT JOIN dbo.TIPOS_PROMOCOES_FLEXIVEIS T ON T.TIPO_PROMOCAO_FLEXIVEL = PF.TIPO_PROMOCAO_FLEXIVEL
CROSS APPLY (
    SELECT L.PRODUTO, DESCONTO = L.DESCONTO_PADRAO
    FROM dbo.PROMOCOES_FLEXIVEIS_LEVE L
    WHERE L.PROMOCAO_FLEXIVEL = PF.PROMOCAO_FLEXIVEL
    UNION ALL
    SELECT G.PRODUTO, DESCONTO = G.DESCONTO
    FROM dbo.PROMOCOES_FLEXIVEIS_GANHE G
    WHERE G.PROMOCAO_FLEXIVEL = PF.PROMOCAO_FLEXIVEL
) X
WHERE PE.EMPRESA IN ({{LOJAS}})
  AND PF.VALIDADE_FIM >= @dataInicial
  AND PF.VALIDADE_INI <  DATEADD(day, 1, @dataFinal)
GROUP BY
    CONVERT(date, PF.VALIDADE_INI),
    CONVERT(date, PF.VALIDADE_FIM),
    X.PRODUTO,
    PE.EMPRESA,
    LEFT(T.DESCRICAO, 40)
HAVING MAX(X.DESCONTO) > 0
ORDER BY 1, 3, 4;
