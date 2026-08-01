-- Histórico de compras / suprimento. DataRecebimento NULL = pedido em
-- trânsito. Lead time real = DATEDIFF(day, DataPedido, DataRecebimento)
-- por SKU x Fornecedor (calculado em view ou no consumidor).
CREATE TABLE dbo.Compras
(
    CompraId        BIGINT          IDENTITY(1,1) NOT NULL,
    RedeId          INT             NOT NULL,
    DataPedido      DATE            NOT NULL,
    DataRecebimento DATE            NULL,
    LojaId          INT             NOT NULL,
    Sku             NVARCHAR(30)    NOT NULL,
    Quantidade      DECIMAL(12,3)   NOT NULL,
    Fornecedor      NVARCHAR(120)   NULL,

    -- PK segue no CompraId (IDENTITY); RedeId entra como coluna e nas FKs
    -- compostas, que é o que amarra a linha ao inquilino.
    CONSTRAINT PK_Compras PRIMARY KEY (CompraId),
    CONSTRAINT FK_Compras_Produtos FOREIGN KEY (RedeId, Sku)    REFERENCES dbo.Produtos(RedeId, Sku),
    CONSTRAINT FK_Compras_Lojas    FOREIGN KEY (RedeId, LojaId) REFERENCES dbo.Lojas(RedeId, LojaId),

    INDEX IX_Compras_Sku_DataPedido      NONCLUSTERED (RedeId, Sku, DataPedido),
    INDEX IX_Compras_Sku_DataRecebimento NONCLUSTERED (RedeId, Sku, DataRecebimento) WHERE DataRecebimento IS NOT NULL
);
