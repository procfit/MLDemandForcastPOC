-- Stage.Lojas <- EMPRESAS_USUARIAS (+ ENDERECOS)
-- Regiao/Perfil saem NULL: REGIONAIS e TIPOS_REDES não existem em todas as
-- instalações do PBS (ausentes na Retiro). DiasOperacaoSemana e DataAbertura
-- não têm fonte no ERP.
-- UF/Cidade são NOT NULL no Stage; loja sem endereço cadastrado recebe 'NI'
-- e é reportada como aviso pelo extrator.
SELECT
    LojaId             = CONVERT(int, E.EMPRESA_USUARIA),
    Nome               = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(E.NOME_FANTASIA)), ''), E.NOME), 200),
    UF                 = CONVERT(varchar(2),  COALESCE(ADR.ESTADO, 'NI')),
    Cidade             = CONVERT(varchar(80), COALESCE(NULLIF(LTRIM(RTRIM(ADR.CIDADE)), ''), 'NI')),
    Regiao             = CONVERT(varchar(60), NULL),
    Perfil             = CONVERT(varchar(60), NULL),
    DiasOperacaoSemana = CONVERT(tinyint, 7),
    DataAbertura       = CONVERT(date, NULL),
    Ativo              = CAST(CASE WHEN E.ATIVO = 'S' THEN 1 ELSE 0 END AS bit),
    -- Sem máscara: o Stage guarda só dígitos, e é por eles que a loja casa com o
    -- painel de PDVs do relatório IQVIA (F16).
    Cnpj               = CONVERT(varchar(14), REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(ENT.CGC)), '.', ''), '/', ''), '-', ''))
FROM dbo.EMPRESAS_USUARIAS E
LEFT JOIN dbo.ENTIDADES ENT ON ENT.ENTIDADE = E.ENTIDADE
OUTER APPLY (
    SELECT TOP 1 A.ESTADO, A.CIDADE
    FROM dbo.ENDERECOS A
    WHERE A.ENTIDADE = E.ENTIDADE
    ORDER BY A.ENDERECOS
) ADR
WHERE E.EMPRESA_USUARIA IN ({{LOJAS}})
ORDER BY E.EMPRESA_USUARIA;
