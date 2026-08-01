-- Lojas ativas do PBS. Não vai para o Stage: serve só à sanidade do "Testar
-- conexão" na interface gráfica, e o consumidor lê as duas colunas de forma tipada.
--
-- EMPRESA_USUARIA é numeric(15,0), como todo numérico do PBS, e o driver entrega
-- numeric como System.Decimal — sem o CONVERT a leitura tipada quebra.
SELECT
    LojaId = CONVERT(int, E.EMPRESA_USUARIA),
    Nome   = CONVERT(varchar(200), COALESCE(NULLIF(LTRIM(RTRIM(E.NOME_FANTASIA)), ''), E.NOME))
FROM dbo.EMPRESAS_USUARIAS E
WHERE E.ATIVO = 'S'
ORDER BY E.EMPRESA_USUARIA;
