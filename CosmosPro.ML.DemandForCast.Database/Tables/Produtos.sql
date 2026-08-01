-- Mestre de produtos / SKUs. Sku é o código interno da rede (não o EAN).
-- ListaControle: NULL para não-controlado; A1/A2/A3/B1/B2/C1/C2/etc para
-- medicamentos sob controle especial (Portaria 344/98 ANVISA).
-- Sku colide entre redes (é código de ERP), então entra na PK depois de RedeId.
CREATE TABLE dbo.Produtos
(
    RedeId              INT             NOT NULL,
    Sku                 NVARCHAR(30)    NOT NULL,
    Nome                NVARCHAR(200)   NOT NULL,
    Categoria           NVARCHAR(80)    NULL,
    Subcategoria        NVARCHAR(80)    NULL,
    Fabricante          NVARCHAR(120)   NULL,
    PrincipioAtivo      NVARCHAR(200)   NULL,
    Apresentacao        NVARCHAR(120)   NULL, -- ex.: "20cp 500mg"
    Ean                 VARCHAR(14)     NULL,
    RegistroAnvisa      VARCHAR(20)     NULL,
    ListaControle       VARCHAR(10)     NULL,
    ClasseTerapeutica   NVARCHAR(120)   NULL,
    Ativo               BIT             NOT NULL CONSTRAINT DF_Produtos_Ativo DEFAULT 1,

    CONSTRAINT PK_Produtos       PRIMARY KEY (RedeId, Sku),
    CONSTRAINT FK_Produtos_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId),

    -- RedeId como primeira coluna dos índices: sem isso a busca por categoria
    -- varre as outras redes.
    INDEX IX_Produtos_PrincipioAtivo NONCLUSTERED (RedeId, PrincipioAtivo) WHERE PrincipioAtivo IS NOT NULL,
    INDEX IX_Produtos_Categoria      NONCLUSTERED (RedeId, Categoria) WHERE Categoria IS NOT NULL
);
