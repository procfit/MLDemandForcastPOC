using CosmosPro.ML.DemandForCast.Worker.Sessoes;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// O divisor da venda média que alimenta a cobertura.
///
/// <para>
/// Vale um arquivo só porque ele <b>escala todas as coberturas da tela</b> e erra em silêncio:
/// um divisor grande demais reduz a média, infla a cobertura e pinta de vermelho item que gira
/// bem. Nada no caminho denuncia — a coluna renderiza, a tabela ordena e o número parece uma
/// medição.
/// </para>
/// </summary>
public sealed class DiasDeHistoricoTests
{
    private static readonly DateOnly Corte = new(2026, 7, 1);

    /// <summary>
    /// Histórico longo: a janela dos 120 dias manda, e o intervalo é semiaberto — conta até a
    /// véspera do corte, como a consulta filtra.
    /// </summary>
    [Fact]
    public void Historico_mais_longo_que_a_janela_usa_os_120_dias()
    {
        SessaoResultadoMaterializador.DiasDeHistorico(Corte, new DateOnly(2025, 1, 1))
            .Should().Be(120);
    }

    /// <summary>
    /// <b>O caso que motiva o helper.</b> Histórico curto: o divisor encolhe junto. Dividir por
    /// 120 aqui faria a média sair a um terço do real.
    /// </summary>
    [Fact]
    public void Historico_mais_curto_que_a_janela_usa_o_que_existe()
    {
        // 22/05 a 30/06 = 40 dias até a véspera do corte.
        SessaoResultadoMaterializador.DiasDeHistorico(Corte, new DateOnly(2026, 5, 22))
            .Should().Be(40);
    }

    /// <summary>
    /// Sem venda importada nenhuma não há como saber o alcance do histórico, e o padrão é a
    /// janela cheia — a consulta simplesmente não encontra linhas e o numerador vai a zero.
    /// </summary>
    [Fact]
    public void Sem_inicio_de_historico_usa_a_janela_cheia()
    {
        SessaoResultadoMaterializador.DiasDeHistorico(Corte, null).Should().Be(120);
    }

    /// <summary>
    /// Histórico que começa no próprio dia da sugestão, ou depois dele, não deixa dia nenhum
    /// para medir. Tem de dar zero, e não negativo: quem chama usa <c>&gt; 0</c> para decidir
    /// entre gravar a média e gravar nulo, e um negativo passaria por essa guarda produzindo
    /// uma média com sinal trocado.
    /// </summary>
    [Theory]
    [InlineData(2026, 7, 1)]
    [InlineData(2026, 8, 15)]
    public void Historico_que_comeca_no_corte_ou_depois_nao_deixa_dias_para_medir(int a, int m, int d)
    {
        SessaoResultadoMaterializador.DiasDeHistorico(Corte, new DateOnly(a, m, d))
            .Should().Be(0);
    }
}
