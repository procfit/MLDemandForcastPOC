-- Contagens de linhas e lojas dos ids que catalogo_sugestoes.sql ja devolveu.
-- A lista fechada de {{SUGESTOES}} e o que torna isto barato: cada id vira uma
-- busca pelo indice de SUGESTAO_COMPRA, em vez da varredura que a versao com
-- JOIN por faixa de datas provocava.
-- Uma sugestao sem nenhuma linha de resultado nao aparece aqui; quem consome
-- assume zero para os ids ausentes (ver CatalogoService.LerContagem).
-- As três colunas são lidas de forma tipada pelo consumidor, então as três
-- declaram o tipo — inclusive as contagens, para que a regra valha por inspeção
-- e não dependa de quem lê saber que COUNT devolve int.
-- DiasCoberturaMax vem daqui, e nao do cabecalho, porque o cabecalho mente.
-- Medido na base da NatusFarma em 2026-08-05: o `MAX(DIAS_CURVA_A..E)` do
-- cabecalho e o parametro do metodo 2 ("Dias de Reposicao") e fica **zerado em
-- 83% das sugestoes de eMax/eSeg** — que e justamente o metodo que este projeto
-- compara. Cobertura zero fazia a janela terminar no dia da sugestao, sem um
-- unico dia de gabarito, e a extracao seguia: 879 MB de dados que nao podiam
-- pontuar nada. Numa sugestao de metodo 2 os dois numeros tambem divergiram
-- (cabecalho 35, itens 65), entao o cabecalho nao serve nem la.
-- DIAS_ESTOQUE e por item, e e o mesmo numero que o DecisionComparer usa para
-- dimensionar a compra de cada item. O MAX responde "ate onde a janela precisa
-- ir para cobrir o item mais longo desta sugestao".
-- ATENCAO: o que DIAS_ESTOQUE significa em cada TipoCalculo nao esta confirmado
-- com quem mantem o PBS — na 21682 (eMax/eSeg) era 3 em todos os 17.226 itens,
-- o que parece periodicidade da rodada e nao cobertura pretendida. Pergunta em
-- aberto; o numero e usado porque e o que o ERP grava e o que o comparador le.
SELECT
    SugestaoId       = CONVERT(bigint, R.SUGESTAO_COMPRA),
    QtdLinhas        = CONVERT(int, COUNT(R.SUGESTAO_COMPRA_RESULTADO)),
    QtdLojas         = CONVERT(int, COUNT(DISTINCT R.FILIAL)),
    DiasCoberturaMax = CONVERT(int, MAX(R.DIAS_ESTOQUE))
FROM dbo.SUGESTOES_COMPRAS_RESULTADO R
WHERE R.SUGESTAO_COMPRA IN ({{SUGESTOES}})
GROUP BY R.SUGESTAO_COMPRA;
