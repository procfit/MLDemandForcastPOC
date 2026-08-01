-- Linhas do resultado da sugestão (origem: dbo.SUGESTOES_COMPRAS_RESULTADO, 120M
-- linhas na instância inspecionada).
--
-- DemandaDia é a PREVISÃO DE DEMANDA DO PRÓPRIO ERP. É o campo mais valioso desta
-- tabela: permite comparar previsão contra previsão (ERP vs ML vs venda real),
-- que é uma comparação de mesma grandeza e mesma data — bem mais limpa que
-- discutir quantidade de compra, que depende de arredondamento de embalagem e de
-- posição de estoque.
--
-- EstoqueSeguranca/EstoqueMaximo vêm ZERADOS quando TipoCalculo = 2: "Dias de
-- Reposição" não usa eSeg/eMax. Não é dado faltando — não tratar como erro.
--
-- CompraSugerida = o que o ERP mandou comprar.
-- CompraAutorizada = o que o comprador aprovou.
-- A diferença entre as duas mede a intervenção humana, e responde de forma
-- empírica se o "método atual" na prática é o ERP ou o ERP mais uma pessoa.
--
-- ATENÇÃO ao importar: as FKs compostas exigem que todo (RedeId, Sku) e
-- (RedeId, LojaId) citados já existam em Produtos/Lojas. O extrator precisa
-- garantir a união dos produtos citados aqui em produtos.csv, senão o
-- SqlBulkCopy estoura violação de FK.
CREATE TABLE dbo.SugestoesCompraItens
(
    RedeId              INT           NOT NULL,
    SugestaoId          BIGINT        NOT NULL,
    LojaId              INT           NOT NULL,
    Sku                 NVARCHAR(30)  NOT NULL,
    Curva               CHAR(1)       NULL,
    DemandaDia          DECIMAL(12,4) NOT NULL,
    DemandaDiaPonderada DECIMAL(15,4) NULL,
    EstoqueSaldo        DECIMAL(15,3) NOT NULL,
    EstoqueSeguranca    DECIMAL(15,3) NULL,
    EstoqueMaximo       DECIMAL(15,3) NULL,
    EstoqueMinimo       DECIMAL(15,3) NULL,
    DiasEstoque         SMALLINT      NOT NULL,
    PedidosPendentes    DECIMAL(15,3) NOT NULL,
    CompraSugerida      DECIMAL(15,3) NOT NULL,
    CompraAutorizada    DECIMAL(15,3) NOT NULL,
    PrecoCompra         DECIMAL(15,4) NULL,
    FatorEmbalagem      DECIMAL(7,2)  NULL,
    Falteiro            BIT           NOT NULL,

    CONSTRAINT PK_SugestoesCompraItens PRIMARY KEY (RedeId, SugestaoId, LojaId, Sku),
    CONSTRAINT FK_SugestoesCompraItens_Sugestoes FOREIGN KEY (RedeId, SugestaoId) REFERENCES dbo.SugestoesCompra(RedeId, SugestaoId),
    CONSTRAINT FK_SugestoesCompraItens_Produtos  FOREIGN KEY (RedeId, Sku)        REFERENCES dbo.Produtos(RedeId, Sku),
    CONSTRAINT FK_SugestoesCompraItens_Lojas     FOREIGN KEY (RedeId, LojaId)     REFERENCES dbo.Lojas(RedeId, LojaId),

    -- Comparação percorre por SKU dentro da rede.
    INDEX IX_SugestoesCompraItens_Sku NONCLUSTERED (RedeId, Sku, LojaId) INCLUDE (DemandaDia, CompraSugerida)
);
