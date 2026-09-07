using CosmosPro.ML.DemandForCast.Web;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// A regra de valor da célula da tabulação, que é o formato pedido pelo patrocinador: nas
/// afirmações da Parte B o <b>número</b> da escala; nas perguntas de caracterização, o
/// <b>texto</b> da opção.
///
/// <para>
/// O discriminador é <c>OpcaoValor</c> nulo, que significa "esta pergunta não é ordinal" — e
/// nunca "grau zero". Exportar 0 numa pergunta nominal produziria média onde não existe média,
/// e exportar 0 numa ausência de resposta produziria a posição mais baixa da escala onde não
/// houve resposta nenhuma. As duas confusões são silenciosas: a planilha soma e fecha.
/// </para>
/// </summary>
public sealed class AvaliacaoTabuladaTests
{
    private static AvaliacaoTabulada Linha(params RespostaTabulada[] respostas) => new(
        SessaoId: Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-000000000001"),
        CriadoEm: DateTimeOffset.UnixEpoch,
        Status: "Concluida",
        SugestaoId: 125595,
        SugestaoDescricao: "Compra semanal",
        AvaliacaoVeredito: "Valido",
        AvaliacaoComentario: null,
        AvaliacaoEm: DateTimeOffset.UnixEpoch,
        Avaliador: "comprador@rede.com",
        QuestionarioEnviadoEm: DateTimeOffset.UnixEpoch,
        VersaoCatalogo: 4,
        Respondente: "comprador@rede.com",
        Respostas: respostas);

    private static RespostaTabulada Ordinal(string codigo, int valor) =>
        new(codigo, $"enunciado de {codigo}", $"{valor} – Concordo", valor, null);

    private static RespostaTabulada Nominal(string codigo, string texto) =>
        new(codigo, $"enunciado de {codigo}", texto, null, null);

    [Fact]
    public void Afirmacao_da_parte_B_exporta_o_numero_da_escala()
    {
        var linha = Linha(Ordinal("B1", 4), Ordinal("B11", 5));

        linha.Valor("B1").Should().Be("4");
        linha.Valor("B11").Should().Be("5");
    }

    [Fact]
    public void Pergunta_de_caracterizacao_exporta_o_texto_da_opcao()
    {
        var linha = Linha(Nominal("A1", "Comprador"), Nominal("A2", "Mais de 10 anos"));

        linha.Valor("A1").Should().Be("Comprador");
        linha.Valor("A2").Should().Be("Mais de 10 anos");
    }

    /// <summary>
    /// Ausência de resposta é célula vazia, e é o caso mais fácil de errar: "0" é um valor
    /// válido da escala e o mais baixo dela, então um zero aqui viraria "discordo totalmente"
    /// na análise de alguém que não respondeu.
    /// </summary>
    [Fact]
    public void Pergunta_sem_resposta_fica_vazia_e_nunca_zero()
    {
        var linha = Linha(Ordinal("B1", 4));

        linha.Valor("B7").Should().BeEmpty();
        linha.Valor("B7").Should().NotBe("0");
        linha.Valor("A1").Should().BeEmpty();
    }

    /// <summary>
    /// O 1 da escala existe e não pode ser confundido com ausência: se "discordo totalmente"
    /// virasse célula vazia, a análise perderia justamente a resposta mais negativa.
    /// </summary>
    [Fact]
    public void Discordo_totalmente_e_resposta_e_nao_ausencia()
    {
        var linha = Linha(Ordinal("B1", 1));

        linha.Valor("B1").Should().Be("1");
        linha.Valor("B1").Should().NotBeEmpty();
    }

    [Fact]
    public void Texto_livre_sai_em_coluna_propria_e_e_nulo_quando_nao_existe()
    {
        var linha = Linha(
            new("A1", "Qual a função?", "Outro:", null, "Coordenador de categoria"),
            Ordinal("B1", 4));

        linha.TextoLivre("A1").Should().Be("Coordenador de categoria");
        linha.TextoLivre("B1").Should().BeNull();
        linha.TextoLivre("B7").Should().BeNull("pergunta sem resposta nao tem texto livre");
    }

    /// <summary>
    /// Mais de uma versão do instrumento na mesma tabulação é aviso, não curiosidade: o mesmo
    /// código designa afirmação diferente entre versões, e somar a coluna inteira misturaria
    /// perguntas distintas sem produzir erro nenhum.
    /// </summary>
    [Fact]
    public void Versoes_presentes_saem_ordenadas_e_sem_repeticao()
    {
        var t = new TabulacaoView(1, ["B1"],
        [
            Linha(Ordinal("B1", 4)) with { VersaoCatalogo = 4 },
            Linha(Ordinal("B1", 5)) with { VersaoCatalogo = 3 },
            Linha(Ordinal("B1", 3)) with { VersaoCatalogo = 4 },
            Linha() with { VersaoCatalogo = null, QuestionarioEnviadoEm = null },
        ]);

        t.VersoesPresentes.Should().Equal([3, 4]);
        t.ComQuestionario.Should().Be(3, "a linha sem questionario nao conta");
        t.ComAvaliacao.Should().Be(4, "todas as quatro tem veredito");
    }

    /// <summary>
    /// Execução não avaliada continua na tabulação. É o denominador: "12 avaliações" não diz
    /// nada sem as execuções de onde saíram, e filtrar aqui esconderia a taxa de resposta.
    /// </summary>
    [Fact]
    public void Execucao_sem_avaliacao_e_sem_questionario_nao_e_filtrada()
    {
        var vazia = Linha() with
        {
            AvaliacaoVeredito = null,
            AvaliacaoEm = null,
            Avaliador = null,
            QuestionarioEnviadoEm = null,
            VersaoCatalogo = null,
            Respondente = null,
        };

        var t = new TabulacaoView(1, ["B1"], [vazia]);

        t.Linhas.Should().HaveCount(1);
        t.ComAvaliacao.Should().Be(0);
        t.ComQuestionario.Should().Be(0);
        vazia.Respondido.Should().BeFalse();
        vazia.Valor("B1").Should().BeEmpty();
    }
}
