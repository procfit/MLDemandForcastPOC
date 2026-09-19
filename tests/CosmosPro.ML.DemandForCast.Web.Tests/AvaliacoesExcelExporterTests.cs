using ClosedXML.Excel;

using CosmosPro.ML.DemandForCast.Web;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// A planilha de tabulação das avaliações.
///
/// <para>
/// <b>Este exportador estava sem teste nenhum</b> desde que foi escrito, e o alinhamento entre
/// cabeçalho e célula já quebrou duas vezes neste repositório em exportadores irmãos — é uma
/// falha silenciosa: a planilha abre, as colunas não batem, e quem tabula descobre na
/// conclusão. O teste central aqui é justamente contar cabeçalhos contra células escritas.
/// </para>
///
/// <para>
/// Lê a planilha de volta com ClosedXML em vez de inspecionar o objeto em memória: o que
/// interessa é o arquivo que o patrocinador abre.
/// </para>
/// </summary>
public sealed class AvaliacoesExcelExporterTests
{
    private static readonly DateTimeOffset Quando = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static RespostaTabulada Ordinal(string codigo, int valor) =>
        new(codigo, $"enunciado de {codigo}", $"{valor} – Concordo", valor, null);

    private static ExecucaoAvaliada Execucao(
        string sufixo, string? veredito = "Valido", string? avaliador = "P01") => new(
        SessaoId: Guid.Parse($"0199a1b2-c3d4-7e5f-8a9b-00000000000{sufixo}"),
        CriadoEm: Quando,
        Status: "Concluida",
        SugestaoId: 125595,
        SugestaoDescricao: "Compra semanal",
        AvaliacaoVeredito: veredito,
        AvaliacaoComentario: veredito is null ? null : "pontos fortes e fracos",
        AvaliacaoEm: veredito is null ? null : Quando,
        Avaliador: avaliador);

    private static TabulacaoView Tabulacao(
        IReadOnlyList<ExecucaoAvaliada>? execucoes = null,
        IReadOnlyList<QuestionarioDoComprador>? questionarios = null) => new(
        RedeId: 3,
        Codigos: ["A1", "A2", "B1", "B11"],
        CodigosDeTexto: ["A2"],
        Participantes: 1,
        Execucoes: execucoes ?? [Execucao("1"), Execucao("2")],
        Questionarios: questionarios ??
        [
            new("P01", Quando, 4,
            [
                new("A1", "Qual a função?", "Comprador", null, null),
                new("A2", "Há quantos anos?", "Entre 6 e 10 anos", 3, null),
                Ordinal("B1", 4),
                Ordinal("B11", 5),
            ]),
        ]);

    private static XLWorkbook Gerar(TabulacaoView t)
    {
        var bytes = AvaliacoesExcelExporter.Gerar(t, "Rede Retiro", Quando);
        return new XLWorkbook(new MemoryStream(bytes));
    }

    /// <summary>
    /// Quatro abas, e os nomes importam: a capa declara o recorte, e as DUAS abas de dado
    /// medem coisas diferentes — seção G por execução, questionário por comprador.
    /// </summary>
    [Fact]
    public void A_planilha_tem_as_quatro_abas_nomeadas()
    {
        using var wb = Gerar(Tabulacao());

        wb.Worksheets.Select(w => w.Name).Should()
            .Equal("Recorte", "Execucoes", "Questionarios", "Perguntas");
    }

    /// <summary>
    /// <b>O teste que este arquivo existe para ter.</b> Cabeçalho e célula têm de ter a mesma
    /// contagem em cada linha: quando divergem, a planilha abre e as colunas ficam deslocadas
    /// em silêncio, e o erro só aparece na análise.
    /// </summary>
    [Fact]
    public void Cada_linha_escreve_exatamente_uma_celula_por_cabecalho()
    {
        using var wb = Gerar(Tabulacao());

        foreach (var nome in new[] { "Execucoes", "Questionarios" })
        {
            var ws = wb.Worksheet(nome);
            var cabecalhos = ws.Row(1).CellsUsed().Count();

            foreach (var linha in ws.RowsUsed().Skip(1))
            {
                linha.LastCellUsed().Address.ColumnNumber.Should().BeLessThanOrEqualTo(cabecalhos,
                    $"a aba {nome} escreveu célula além do último cabeçalho");
            }

            cabecalhos.Should().BeGreaterThan(0, $"a aba {nome} precisa de cabeçalho");
        }
    }

    /// <summary>
    /// O questionário é UM por comprador, e a aba dele tem UMA linha por comprador — mesmo com
    /// duas execuções avaliadas pela mesma pessoa. Repetir a resposta por execução faria
    /// qualquer contagem sair multiplicada.
    /// </summary>
    [Fact]
    public void O_questionario_aparece_uma_vez_mesmo_com_duas_execucoes()
    {
        using var wb = Gerar(Tabulacao());

        wb.Worksheet("Execucoes").RowsUsed().Count().Should().Be(3, "cabeçalho + 2 execuções");
        wb.Worksheet("Questionarios").RowsUsed().Count().Should().Be(2, "cabeçalho + 1 comprador");
    }

    /// <summary>
    /// Afirmação da Parte B entra como NÚMERO, para o Excel poder somar; pergunta marcada como
    /// texto entra como texto, mesmo tendo escala. Quem decide é o catálogo, via
    /// <c>CodigosDeTexto</c>.
    /// </summary>
    [Fact]
    public void Escala_entra_como_numero_e_pergunta_de_texto_entra_como_texto()
    {
        using var wb = Gerar(Tabulacao());
        var ws = wb.Worksheet("Questionarios");

        var coluna = (string titulo) => ws.Row(1).CellsUsed()
            .First(c => c.GetString() == titulo).Address.ColumnNumber;

        ws.Cell(2, coluna("B1")).Value.IsNumber.Should().BeTrue("a escala tem de ser somável");
        ws.Cell(2, coluna("B1")).GetDouble().Should().Be(4);

        ws.Cell(2, coluna("A2")).GetString().Should().Be("Entre 6 e 10 anos",
            "A2 tem escala ordenada mas o catálogo pede o texto");
    }

    /// <summary>
    /// <b>Célula vazia é ausência, e nunca zero.</b> Zero seria a posição mais baixa da escala
    /// — "discordo totalmente" de quem não respondeu.
    /// </summary>
    [Fact]
    public void Pergunta_sem_resposta_sai_em_branco_e_nao_como_zero()
    {
        var t = Tabulacao(questionarios:
        [
            new("P01", Quando, 4, [Ordinal("B1", 4)]),
        ]);

        using var wb = Gerar(t);
        var ws = wb.Worksheet("Questionarios");
        var colunaB11 = ws.Row(1).CellsUsed()
            .First(c => c.GetString() == "B11").Address.ColumnNumber;

        ws.Cell(2, colunaB11).GetString().Should().BeEmpty();
        ws.Cell(2, colunaB11).Value.IsNumber.Should().BeFalse("zero aqui viraria uma resposta");
    }

    /// <summary>
    /// A execução não avaliada fica na planilha. É o denominador da taxa de resposta, e
    /// filtrá-la faria "12 avaliações" perder o de-quantas.
    /// </summary>
    [Fact]
    public void Execucao_nao_avaliada_continua_na_planilha()
    {
        var t = Tabulacao(execucoes: [Execucao("1"), Execucao("2", veredito: null, avaliador: null)]);

        using var wb = Gerar(t);
        var ws = wb.Worksheet("Execucoes");

        ws.RowsUsed().Count().Should().Be(3, "cabeçalho + as duas execuções");

        var colunaVeredito = ws.Row(1).CellsUsed()
            .First(c => c.GetString() == "Avaliação (seção G)").Address.ColumnNumber;
        ws.Cell(3, colunaVeredito).GetString().Should().BeEmpty();
    }

    /// <summary>
    /// A identidade não sai do banco, então também não pode sair na planilha. O teste varre a
    /// pasta inteira procurando arroba: um e-mail em qualquer célula seria vazamento.
    /// </summary>
    [Fact]
    public void Nenhuma_celula_carrega_identidade()
    {
        using var wb = Gerar(Tabulacao());

        foreach (var ws in wb.Worksheets)
        {
            foreach (var c in ws.CellsUsed())
            {
                c.GetString().Should().NotContain("@",
                    $"a célula {ws.Name}!{c.Address} não pode carregar e-mail");
            }
        }
    }

    /// <summary>
    /// A legenda sai do <b>retrato gravado com a resposta</b>, e não do catálogo em memória: é o
    /// enunciado que o participante de fato leu.
    /// </summary>
    [Fact]
    public void A_legenda_traz_o_enunciado_gravado_com_a_resposta()
    {
        using var wb = Gerar(Tabulacao());
        var ws = wb.Worksheet("Perguntas");

        var enunciados = ws.RowsUsed().Skip(1)
            .Select(r => r.Cell(2).GetString())
            .ToList();

        enunciados.Should().Contain("Qual a função?");
        enunciados.Should().Contain("enunciado de B11");
    }

    /// <summary>
    /// Ordem natural do código, e não alfabética: sem isso B11 vem antes de B1 e a legenda de
    /// um instrumento de onze afirmações fica fora da ordem em que ele é aplicado.
    /// </summary>
    [Fact]
    public void A_legenda_ordena_B1_antes_de_B11()
    {
        using var wb = Gerar(Tabulacao());
        var ws = wb.Worksheet("Perguntas");

        var codigos = ws.RowsUsed().Skip(1).Select(r => r.Cell(1).GetString()).ToList();

        codigos.IndexOf("B1").Should().BeLessThan(codigos.IndexOf("B11"));
        codigos.IndexOf("A1").Should().BeLessThan(codigos.IndexOf("B1"));
    }

    /// <summary>
    /// Sem questionário nenhum a planilha continua válida e <b>diz</b> que está vazia — folha em
    /// branco não distingue "ninguém respondeu" de "a exportação falhou".
    /// </summary>
    [Fact]
    public void Sem_questionario_a_planilha_declara_a_ausencia()
    {
        var t = Tabulacao(questionarios: []);

        using var wb = Gerar(t);

        wb.Worksheet("Questionarios").Cell(2, 1).GetString()
            .Should().Contain("Nenhum comprador respondeu");
        wb.Worksheet("Perguntas").Cell(2, 1).GetString()
            .Should().Contain("Nenhum questionário respondido");
    }
}
