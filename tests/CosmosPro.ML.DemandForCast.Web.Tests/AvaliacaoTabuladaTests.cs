using CosmosPro.ML.DemandForCast.Web;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// A regra de valor da célula da tabulação, que é o formato pedido pelo patrocinador: nas
/// afirmações da Parte B o <b>número</b> da escala; nas perguntas de caracterização, o
/// <b>texto</b> da opção.
///
/// <para>
/// <b>Quem decide o formato é o catálogo</b>, via <c>PerguntaDef.TabularTexto</c>. A regra
/// anterior deduzia do dado — "tem <c>OpcaoValor</c>? exporta número" — e errou o A2, cujas
/// faixas de experiência têm ordem real e por isso carregam valor, mas cuja leitura útil é
/// "Entre 2 e 5 anos". Deduzir confundia duas perguntas diferentes: <i>a escala é ordenada?</i>
/// e <i>o que vai na planilha?</i>.
/// </para>
///
/// <para>
/// O que continua valendo: <c>OpcaoValor</c> nulo significa "esta pergunta não é ordinal", e
/// nunca "grau zero"; e célula vazia é ausência de resposta, nunca 0. Exportar 0 numa nominal
/// produziria média onde não existe média, e 0 numa ausência viraria "discordo totalmente" de
/// quem não respondeu. As duas confusões são silenciosas: a planilha soma e fecha.
/// </para>
///
/// <para>
/// <b>As respostas vivem em <see cref="QuestionarioDoComprador"/>, e não mais na execução</b>
/// (19/09/2026): o questionário é um por comprador. Os testes de versão saíram junto — não há
/// mais uma coluna de versão a somar errado, porque não há mais instrumentos convivendo.
/// </para>
/// </summary>
public sealed class AvaliacaoTabuladaTests
{
    private static QuestionarioDoComprador Respondido(params RespostaTabulada[] respostas) => new(
        Respondente: "P01",
        EnviadoEm: DateTimeOffset.UnixEpoch,
        VersaoCatalogo: 4,
        Respostas: respostas);

    private static ExecucaoAvaliada Execucao(string? veredito = "Valido", string? avaliador = "P01") => new(
        SessaoId: Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-000000000001"),
        CriadoEm: DateTimeOffset.UnixEpoch,
        Status: "Concluida",
        SugestaoId: 125595,
        SugestaoDescricao: "Compra semanal",
        AvaliacaoVeredito: veredito,
        AvaliacaoComentario: null,
        AvaliacaoEm: veredito is null ? null : DateTimeOffset.UnixEpoch,
        Avaliador: avaliador);

    private static RespostaTabulada Ordinal(string codigo, int valor) =>
        new(codigo, $"enunciado de {codigo}", $"{valor} – Concordo", valor, null);

    private static RespostaTabulada Nominal(string codigo, string texto) =>
        new(codigo, $"enunciado de {codigo}", texto, null, null);

    [Fact]
    public void Afirmacao_da_parte_B_exporta_o_numero_da_escala()
    {
        var q = Respondido(Ordinal("B1", 4), Ordinal("B11", 5));

        q.Valor("B1").Should().Be("4");
        q.Valor("B11").Should().Be("5");
    }

    [Fact]
    public void Pergunta_de_caracterizacao_exporta_o_texto_da_opcao()
    {
        var q = Respondido(Nominal("A1", "Comprador"), Nominal("A2", "Mais de 10 anos"));

        q.Valor("A1").Should().Be("Comprador");
        q.Valor("A2").Should().Be("Mais de 10 anos");
    }

    /// <summary>
    /// Célula vazia é ausência de resposta, e <b>nunca zero</b>: zero seria uma posição na
    /// escala, e a mais baixa dela — "discordo totalmente" de quem não respondeu.
    /// </summary>
    [Fact]
    public void Pergunta_sem_resposta_fica_vazia_e_nunca_zero()
    {
        var q = Respondido(Ordinal("B1", 4));

        q.Valor("B2").Should().BeEmpty();
        q.Valor("B2").Should().NotBe("0");
    }

    /// <summary>
    /// O oposto do caso acima, e a razão de ele importar: 1 é resposta de verdade — "discordo
    /// totalmente" — e tem de sair como 1, não como vazio.
    /// </summary>
    [Fact]
    public void Discordo_totalmente_e_resposta_e_nao_ausencia()
    {
        var q = Respondido(Ordinal("B1", 1));

        q.Valor("B1").Should().Be("1");
    }

    /// <summary>
    /// A2 tem escala ordenada <b>e</b> exporta texto. A regra anterior deduzia o formato do
    /// dado e confundia duas perguntas diferentes: <i>a escala é ordenada?</i> e <i>o que vai
    /// na planilha?</i>. Quem decide agora é o catálogo (<c>PerguntaDef.TabularTexto</c>), e a
    /// ordem continua gravada para quem quiser calcular com ela.
    /// </summary>
    [Fact]
    public void Pergunta_marcada_como_texto_exporta_o_rotulo_mesmo_tendo_escala()
    {
        var q = Respondido(Ordinal("A2", 2), Ordinal("B1", 4));

        // A resposta TEM valor de escala...
        q.Respostas.Single(r => r.PerguntaCodigo == "A2").OpcaoValor.Should().Be(2);

        // ...e ainda assim a celula traz o texto, porque o catalogo pediu.
        q.Valor("A2", comoTexto: true).Should().Be("2 – Concordo",
            "o texto da opcao e o que vai na planilha; aqui o helper de teste rotula assim");
        q.Valor("A2").Should().Be("2", "sem a marca, a regra de escala continua valendo");
        q.Valor("B1", comoTexto: false).Should().Be("4");
    }

    [Fact]
    public void EhTexto_sai_do_catalogo_e_nao_do_formato_do_dado()
    {
        var t = new TabulacaoView(1, ["A1", "A2", "B1"], ["A2"], Participantes: 0, [], []);

        t.EhTexto("A2").Should().BeTrue();
        t.EhTexto("B1").Should().BeFalse();
        t.EhTexto("A1").Should().BeFalse(
            "A1 nao precisa da marca: ela nao tem escala, entao a regra padrao ja devolve texto");
    }

    [Fact]
    public void Texto_livre_sai_em_coluna_propria_e_e_nulo_quando_nao_existe()
    {
        var q = Respondido(
            new("A1", "Qual a função?", "Outro:", null, "Coordenador de categoria"),
            Ordinal("B1", 4));

        q.TextoLivre("A1").Should().Be("Coordenador de categoria");
        q.TextoLivre("B1").Should().BeNull();
        q.TextoLivre("B7").Should().BeNull("pergunta sem resposta nao tem texto livre");
    }

    /// <summary>
    /// <b>Os dois blocos contam coisas diferentes, e é por isso que são dois.</b> O mesmo
    /// comprador avalia várias execuções e responde UM questionário; numa tabela só, as
    /// respostas dele apareceriam repetidas em cada execução e qualquer contagem sobre a coluna
    /// sairia multiplicada pelo número de execuções.
    /// </summary>
    [Fact]
    public void Execucoes_e_questionarios_sao_contados_separadamente()
    {
        var t = new TabulacaoView(1, ["B1"], [], Participantes: 1,
            Execucoes: [Execucao(), Execucao(), Execucao(veredito: null, avaliador: null)],
            Questionarios: [Respondido(Ordinal("B1", 4))]);

        t.Execucoes.Should().HaveCount(3);
        t.ComAvaliacao.Should().Be(2, "a terceira execucao nao foi avaliada");
        t.Respondidos.Should().Be(1,
            "duas execucoes avaliadas pela mesma pessoa produzem UM questionario, nao dois");
    }

    /// <summary>
    /// Execução não avaliada continua na tabulação. É o denominador: "12 avaliações" não diz
    /// nada sem as execuções de onde saíram, e filtrar aqui esconderia a taxa de resposta.
    /// </summary>
    [Fact]
    public void Execucao_sem_avaliacao_nao_e_filtrada()
    {
        var vazia = Execucao(veredito: null, avaliador: null);

        var t = new TabulacaoView(1, ["B1"], [], Participantes: 1, [vazia], []);

        t.Execucoes.Should().HaveCount(1);
        t.ComAvaliacao.Should().Be(0);
        vazia.Avaliada.Should().BeFalse();
    }

    /// <summary>
    /// Rascunho não conta como respondido. <c>EnviadoEm</c> é a única autoridade sobre selado —
    /// não há coluna de situação, aqui nem na tabela.
    /// </summary>
    [Fact]
    public void Rascunho_nao_conta_como_respondido()
    {
        var rascunho = Respondido(Ordinal("B1", 4)) with { EnviadoEm = null };

        var t = new TabulacaoView(1, ["B1"], [], Participantes: 1, [], [rascunho]);

        rascunho.Respondido.Should().BeFalse();
        t.Respondidos.Should().Be(0);
        rascunho.Valor("B1").Should().Be("4", "o rascunho tem respostas, só não foi selado");
    }
}
