-- Cabeçalho de cada sugestão de compra gerada pelo ERP PBS (origem:
-- dbo.SUGESTOES_COMPRAS). SugestaoId preserva o identificador do PBS para o
-- usuário conseguir rastrear a sugestão no ERP.
--
-- TipoCalculo espelha dbo.TIPOS_CALCULO_SUGESTAO do PBS:
--   1 = "Emax e Eseg"        → usa estoque máximo e de segurança
--   2 = "Dias de Reposição"  → cobertura fixa em dias; NÃO usa eMax/eSeg
-- Os dois convivem no ERP (na NatusFarma, 5.098 e 14.085 sugestões num ano), e o
-- comparativo trata cada um como baseline separado — misturá-los numa métrica
-- não significa nada.
--
-- DiasCurvaA..E: dias de cobertura por classe de giro, parâmetro da execução.
CREATE TABLE dbo.SugestoesCompra
(
    RedeId                    INT           NOT NULL,
    SugestaoId                BIGINT        NOT NULL,
    Descricao                 NVARCHAR(100) NULL,
    DataHora                  DATETIME2(0)  NOT NULL,
    TipoCalculo               TINYINT       NOT NULL,
    LeadTimeDias              SMALLINT      NULL,
    DiasCurvaA                SMALLINT      NOT NULL,
    DiasCurvaB                SMALLINT      NOT NULL,
    DiasCurvaC                SMALLINT      NOT NULL,
    DiasCurvaD                SMALLINT      NOT NULL,
    DiasCurvaE                SMALLINT      NOT NULL,
    Efetividade               DECIMAL(6,2)  NOT NULL,
    ConsideraPedidosPendentes BIT           NOT NULL,
    IncluiEstoqueZerado       BIT           NOT NULL,

    CONSTRAINT PK_SugestoesCompra       PRIMARY KEY (RedeId, SugestaoId),
    CONSTRAINT FK_SugestoesCompra_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId),
    CONSTRAINT CK_SugestoesCompra_TipoCalculo CHECK (TipoCalculo IN (1, 2)),

    -- Padrão de acesso do comparativo: sugestões de um método numa janela.
    INDEX IX_SugestoesCompra_Tipo_Data NONCLUSTERED (RedeId, TipoCalculo, DataHora)
);
