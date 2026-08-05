-- Catalogo para o usuario escolher a sugestao: cabecalho puro, sem tocar em
-- SUGESTOES_COMPRAS_RESULTADO.
-- A cobertura NAO sai daqui. O `MAX(DIAS_CURVA_A..E)` do cabecalho parecia ser
-- ela, mas e o parametro do metodo 2 e vem zerado em 83% das sugestoes de
-- eMax/eSeg — ver o comentario de catalogo_sugestoes_contagens.sql, que passou a
-- devolve-la a partir do DIAS_ESTOQUE dos itens.
-- As contagens de linhas e lojas vem de catalogo_sugestoes_contagens.sql, numa
-- segunda ida ao banco: agregar a tabela de resultado (dezenas de milhoes de
-- linhas) por faixa de datas do cabecalho custava minutos na instancia real,
-- enquanto agregar por uma lista fechada de ids responde instantaneamente.
-- Toda coluna daqui é lida de forma tipada pelo consumidor, então toda coluna
-- declara o tipo: numérico do PBS é sempre numeric(p,s) e chega como
-- System.Decimal quando falta o CONVERT.
SELECT
    SugestaoId        = CONVERT(bigint, S.SUGESTAO_COMPRA),
    Descricao         = CONVERT(varchar(100), LEFT(S.DESCRICAO, 100)),
    DataHora          = CONVERT(datetime2(0), S.DATA_HORA),
    TipoCalculo       = CONVERT(tinyint, S.TIPO_CALCULO)
FROM dbo.SUGESTOES_COMPRAS S
WHERE S.TIPO_CALCULO IS NOT NULL
  AND S.DATA_HORA >= {{DATA_INICIO}}
ORDER BY S.DATA_HORA DESC;
