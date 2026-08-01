-- Vendas agregadas por (Data, LojaId, Sku). Assume granularidade diária —
-- detalhe de cupom não entra aqui (irrelevante para forecast).
-- Quantidade em DECIMAL(12,3) para suportar venda fracionada (manipulação,
-- fracionamento de blister, etc).
CREATE TABLE dbo.Vendas
(
    RedeId          INT             NOT NULL,
    Data            DATE            NOT NULL,
    LojaId          INT             NOT NULL,
    Sku             NVARCHAR(30)    NOT NULL,
    Quantidade      DECIMAL(12,3)   NOT NULL,
    PrecoUnitario   DECIMAL(12,4)   NOT NULL,
    ValorTotal      DECIMAL(14,4)   NOT NULL,

    CONSTRAINT PK_Vendas PRIMARY KEY (RedeId, Data, LojaId, Sku),
    -- FKs compostas amarram a linha à rede transitivamente, dispensando uma FK
    -- direta para Redes no caminho do SqlBulkCopy.
    CONSTRAINT FK_Vendas_Produtos FOREIGN KEY (RedeId, Sku)    REFERENCES dbo.Produtos(RedeId, Sku),
    CONSTRAINT FK_Vendas_Lojas    FOREIGN KEY (RedeId, LojaId) REFERENCES dbo.Lojas(RedeId, LojaId),

    -- Padrão de acesso típico de feature extraction: por SKU em janela temporal.
    INDEX IX_Vendas_Sku_Data NONCLUSTERED (RedeId, Sku, Data) INCLUDE (LojaId, Quantidade)
);
