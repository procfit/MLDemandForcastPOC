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
    --
    -- INSCRICAO_FEDERAL, e não CGC. A versão anterior lia ENT.CGC, coluna que NÃO
    -- EXISTE em instalação nenhuma do PBS — o extrator 0.18.0 morreu na Retiro com
    -- "Invalid column name 'CGC'" (SQL 207) na primeira etapa. O nome tinha sido
    -- escrito por suposição e nunca exercitado: os testes do extrator são unitários
    -- e esta consulta só roda contra banco real.
    --
    -- Conferido em 2026-09-01 na instância da Natusfarma: das 139 lojas de
    -- EMPRESAS_USUARIAS, 138 têm INSCRICAO_FEDERAL com 18 caracteres no bruto e
    -- exatamente 14 dígitos depois da máscara — que é o CNPJ, e é a máscara que a
    -- cadeia de REPLACE abaixo remove. A que falta não tem inscrição cadastrada, e
    -- cai como CNPJ nulo (a loja importa normalmente e fica fora do sinal de mercado).
    Cnpj               = CONVERT(varchar(14), REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(ENT.INSCRICAO_FEDERAL)), '.', ''), '/', ''), '-', ''))
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
