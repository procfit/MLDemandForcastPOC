-- Stage.SugestoesCompraItens <- SUGESTOES_COMPRAS_RESULTADO. Escopada a UMA
-- sugestão — sem esse filtro a origem tem 120M linhas.
-- LojaId = FILIAL, não EMPRESA: os dois coincidem na NatusFarma mas não foram
-- validados na Retiro (ver sugestoes_compra_diagnostico.sql e
-- Docs/extracao-pbs-stage.md). Sku sai como texto para casar com
-- Stage.Produtos.Sku (NVARCHAR(30)); PRODUTO é numérico no PBS.
-- EstoqueSeguranca/EstoqueMaximo saem zerados quando a sugestão é
-- TipoCalculo = 2 ("Dias de Reposição") — não é dado faltando.
SELECT
    SugestaoId          = CONVERT(bigint, R.SUGESTAO_COMPRA),
    LojaId               = CONVERT(int, R.FILIAL),
    Sku                  = CONVERT(varchar(30), R.PRODUTO),
    Curva                = CONVERT(char(1), R.CURVA),
    DemandaDia           = CONVERT(decimal(12,4), R.DEMANDA_DIA),
    DemandaDiaPonderada  = CONVERT(decimal(15,4), R.DEMANDA_DIA_PONDERADA),
    EstoqueSaldo         = CONVERT(decimal(15,3), R.ESTOQUE_SALDO),
    EstoqueSeguranca     = CONVERT(decimal(15,3), R.ESTOQUE_SEGURANCA),
    EstoqueMaximo        = CONVERT(decimal(15,3), R.ESTOQUE_MAXIMO),
    EstoqueMinimo        = CONVERT(decimal(15,3), R.ESTOQUE_MINIMO),
    DiasEstoque          = CONVERT(smallint, R.DIAS_ESTOQUE),
    PedidosPendentes     = CONVERT(decimal(15,3), R.PEDIDOS_PENDENTES),
    CompraSugerida       = CONVERT(decimal(15,3), R.COMPRA_SUGERIDA),
    CompraAutorizada     = CONVERT(decimal(15,3), R.COMPRA_AUTORIZADA),
    PrecoCompra          = CONVERT(decimal(15,4), R.PRECO_COMPRA),
    FatorEmbalagem       = CONVERT(decimal(7,2), R.FATOR_EMBALAGEM),
    Falteiro             = CAST(CASE WHEN R.FALTEIRO = 'S' THEN 1 ELSE 0 END AS bit)
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
WHERE R.SUGESTAO_COMPRA = {{SUGESTAO}}
ORDER BY R.FILIAL, R.PRODUTO;
