-- Sinal exógeno de mercado farma (IQVIA-like). Granularidade mensal por
-- princípio ativo × UF. Para TCC sem licença IQVIA real, este schema é
-- compatível com geração sintética calibrada.
-- Mes: primeiro dia do mês de referência (yyyy-MM-01).
-- DemandaMercadoUnidades: total de unidades estimadas vendidas no mercado.
-- MarketShareCategoria: 0-1, fração da categoria representada por este
--   princípio ativo na UF.
-- RedeId, ainda que o dado de mercado seja o mesmo para todas as redes: mantém a
-- semântica de "cada import é dono completo do Stage da sua rede". Sem isso, o
-- import de uma rede alteraria dado visível pela outra. Duplicação desprezível.
CREATE TABLE dbo.MercadoIqvia
(
    RedeId                 INT           NOT NULL,
    Mes                    DATE          NOT NULL,
    PrincipioAtivo         NVARCHAR(200) NOT NULL,
    UF                     CHAR(2)       NOT NULL,
    DemandaMercadoUnidades DECIMAL(18,3) NOT NULL,
    MarketShareCategoria   DECIMAL(6,4)  NULL,

    CONSTRAINT PK_MercadoIqvia       PRIMARY KEY (RedeId, Mes, PrincipioAtivo, UF),
    CONSTRAINT FK_MercadoIqvia_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId),
    CONSTRAINT CK_MercadoIqvia_MarketShare CHECK (MarketShareCategoria IS NULL OR (MarketShareCategoria >= 0 AND MarketShareCategoria <= 1)),
    CONSTRAINT CK_MercadoIqvia_DiaUm CHECK (DAY(Mes) = 1)
);
