-- Mestre de lojas (pontos de venda da rede).
-- LojaId é o código interno do ERP da rede e colide entre redes — por isso
-- entra na PK depois de RedeId, nunca como chave única global.
CREATE TABLE dbo.Lojas
(
    RedeId             INT             NOT NULL,
    LojaId             INT             NOT NULL,
    Nome               NVARCHAR(120)   NOT NULL,
    UF                 CHAR(2)         NOT NULL,
    Cidade             NVARCHAR(100)   NOT NULL,
    Regiao             NVARCHAR(50)    NULL,
    Perfil             NVARCHAR(30)    NULL, -- 'rua', 'shopping', 'popular', 'premium'
    DiasOperacaoSemana TINYINT         NOT NULL CONSTRAINT DF_Lojas_DiasOperacaoSemana DEFAULT 7,
    DataAbertura       DATE            NULL,
    Ativo              BIT             NOT NULL CONSTRAINT DF_Lojas_Ativo DEFAULT 1,

    CONSTRAINT PK_Lojas       PRIMARY KEY (RedeId, LojaId),
    CONSTRAINT FK_Lojas_Redes FOREIGN KEY (RedeId) REFERENCES dbo.Redes(RedeId)
);
