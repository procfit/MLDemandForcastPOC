-- Snapshots diários de estoque por (Data, LojaId, Sku). Usado para identificar
-- ruptura (QuantidadeEmEstoque <= 0 ou abaixo de threshold) — crítico para
-- evitar viés "venda=0 ⇒ demanda=0" no treino.
CREATE TABLE dbo.EstoquesDiarios
(
    RedeId              INT             NOT NULL,
    Data                DATE            NOT NULL,
    LojaId              INT             NOT NULL,
    Sku                 NVARCHAR(30)    NOT NULL,
    QuantidadeEmEstoque DECIMAL(12,3)   NOT NULL,

    CONSTRAINT PK_EstoquesDiarios PRIMARY KEY (RedeId, Data, LojaId, Sku),
    CONSTRAINT FK_EstoquesDiarios_Produtos FOREIGN KEY (RedeId, Sku)    REFERENCES dbo.Produtos(RedeId, Sku),
    CONSTRAINT FK_EstoquesDiarios_Lojas    FOREIGN KEY (RedeId, LojaId) REFERENCES dbo.Lojas(RedeId, LojaId),

    INDEX IX_EstoquesDiarios_Sku_Data NONCLUSTERED (RedeId, Sku, Data) INCLUDE (LojaId, QuantidadeEmEstoque)
);
