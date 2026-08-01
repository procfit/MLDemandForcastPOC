-- Inquilinos do sistema (redes de farmácia). Mestre referencial de todo dado
-- staged: nenhuma linha de Stage existe sem uma rede dona.
-- RedeId NÃO é IDENTITY — o valor é atribuído pelo registro em engine.Redes e
-- projetado aqui pelo Worker no início de cada import. FK entre bancos não
-- existe no SQL Server; esta tabela é a âncora que viabiliza FK real nas
-- tabelas de dado.
CREATE TABLE dbo.Redes
(
    RedeId INT           NOT NULL,
    Nome   NVARCHAR(120) NOT NULL,
    Slug   VARCHAR(40)   NOT NULL,

    CONSTRAINT PK_Redes      PRIMARY KEY (RedeId),
    CONSTRAINT UQ_Redes_Slug UNIQUE (Slug)
);
