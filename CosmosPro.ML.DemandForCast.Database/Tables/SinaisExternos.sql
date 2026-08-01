-- Sinais exógenos regionais por dia. Formato longo (EAV leve) para acomodar
-- vários tipos sem mudar schema: Tipo='Clima' (temperatura °C), 'Gripe' (índice
-- de incidência 0..~120). Geografia = UF (poderia ser município/região no futuro).
--
-- Semântica de disponibilidade (consumida no feature engineering, F5):
--   * Clima: conhecido do futuro (previsão do tempo cobre o lead time) → feature do dia-alvo D.
--   * Gripe: defasado (reporte epidemiológico atrasa) → feature até D - lead time.
-- A tabela só guarda o valor por (Data, Geografia, Tipo); a regra de defasagem é
-- aplicada por quem lê.
-- RedeId pelo mesmo motivo de MercadoIqvia: clima e gripe por UF são idênticos
-- entre redes, mas compartilhar faria o import de uma mexer no dado da outra.
CREATE TABLE dbo.SinaisExternos
(
    RedeId      INT           NOT NULL,
    Data        DATE          NOT NULL,
    Geografia   VARCHAR(40)   NOT NULL,
    Tipo        VARCHAR(20)   NOT NULL,
    Valor       DECIMAL(10,4) NOT NULL,

    CONSTRAINT PK_SinaisExternos       PRIMARY KEY (RedeId, Data, Geografia, Tipo),
    CONSTRAINT FK_SinaisExternos_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId),

    INDEX IX_SinaisExternos_Tipo_Geo_Data NONCLUSTERED (RedeId, Tipo, Geografia, Data)
);
