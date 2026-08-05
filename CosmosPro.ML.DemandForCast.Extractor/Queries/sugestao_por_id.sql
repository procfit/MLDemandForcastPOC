-- Cabecalho de UMA sugestao, por id. Existe porque extrair uma sugestao conhecida
-- nao precisa de catalogo: o --extract carregava a lista inteira (milhares de
-- sugestoes, com contagens) so para achar uma por FirstOrDefault, e na instancia
-- real isso passava de 8 minutos. Aqui e um seek na chave primaria.
--
-- Sem filtro de data de proposito: quem ja sabe o id da sugestao nao deve esbarrar
-- na janela de meses retroativos do catalogo, que existe para navegacao.
-- Toda coluna é lida de forma tipada pelo consumidor, então toda coluna declara o
-- tipo — ver o cabeçalho de catalogo_sugestoes.sql.
-- A cobertura nao sai daqui: vem de catalogo_sugestoes_contagens.sql, do
-- DIAS_ESTOQUE dos itens. Ver o comentario de lá.
SELECT TOP 1
    SugestaoId        = CONVERT(bigint, S.SUGESTAO_COMPRA),
    Descricao         = CONVERT(varchar(100), LEFT(S.DESCRICAO, 100)),
    DataHora          = CONVERT(datetime2(0), S.DATA_HORA),
    TipoCalculo       = CONVERT(tinyint, S.TIPO_CALCULO)
FROM dbo.SUGESTOES_COMPRAS S
WHERE S.SUGESTAO_COMPRA = {{SUGESTAO_ID}}
  AND S.TIPO_CALCULO IS NOT NULL;
