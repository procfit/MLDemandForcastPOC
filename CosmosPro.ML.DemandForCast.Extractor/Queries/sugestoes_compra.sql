-- Stage.SugestoesCompra <- SUGESTOES_COMPRAS. Escopada a UMA sugestão (o usuário
-- escolhe no catálogo, ver catalogo_sugestoes.sql) — não há janela de data aqui.
-- TipoCalculo espelha dbo.TIPOS_CALCULO_SUGESTAO do PBS: 1 = "Emax e Eseg",
-- 2 = "Dias de Reposição". Não misturar os dois numa métrica (ver
-- Docs/extracao-pbs-stage.md).
SELECT
    SugestaoId                = CONVERT(bigint, S.SUGESTAO_COMPRA),
    Descricao                 = CONVERT(varchar(100), LEFT(S.DESCRICAO, 100)),
    DataHora                  = CONVERT(datetime2(0), S.DATA_HORA),
    TipoCalculo               = CONVERT(tinyint, S.TIPO_CALCULO),
    LeadTimeDias              = CONVERT(smallint, S.LEADTIME),
    DiasCurvaA                = CONVERT(smallint, S.DIAS_CURVA_A),
    DiasCurvaB                = CONVERT(smallint, S.DIAS_CURVA_B),
    DiasCurvaC                = CONVERT(smallint, S.DIAS_CURVA_C),
    DiasCurvaD                = CONVERT(smallint, S.DIAS_CURVA_D),
    DiasCurvaE                = CONVERT(smallint, S.DIAS_CURVA_E),
    Efetividade               = CONVERT(decimal(6,2), S.EFETIVIDADE),
    ConsideraPedidosPendentes = CAST(CASE WHEN S.PEDIDOS_PENDENTES = 'S' THEN 1 ELSE 0 END AS bit),
    IncluiEstoqueZerado       = CAST(CASE WHEN S.ESTOQUE_ZERADO    = 'S' THEN 1 ELSE 0 END AS bit)
FROM dbo.SUGESTOES_COMPRAS S
WHERE S.SUGESTAO_COMPRA = {{SUGESTAO}};
