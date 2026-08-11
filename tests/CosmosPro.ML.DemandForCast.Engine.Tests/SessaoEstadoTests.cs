using CosmosPro.ML.DemandForCast.Engine.Entities;
using FluentAssertions;
using Xunit;

namespace CosmosPro.ML.DemandForCast.Engine.Tests;

public sealed class SessaoEstadoTests
{
    [Theory]
    [InlineData(SessaoStatus.AguardandoDados, SessaoStatus.ProcessandoDados, true)]
    [InlineData(SessaoStatus.ProcessandoDados, SessaoStatus.Treinando, true)]
    [InlineData(SessaoStatus.Treinando, SessaoStatus.Comparando, true)]
    [InlineData(SessaoStatus.Comparando, SessaoStatus.AguardandoQuestionario, true)]
    [InlineData(SessaoStatus.AguardandoQuestionario, SessaoStatus.Concluida, true)]
    [InlineData(SessaoStatus.AguardandoDados, SessaoStatus.Concluida, false)]
    [InlineData(SessaoStatus.Concluida, SessaoStatus.Treinando, false)]
    // Comparar não conclui mais a sessão: o questionário é a última fase, então o pulo direto
    // deixaria uma sessão "concluída" sem ninguém ter avaliado.
    [InlineData(SessaoStatus.Comparando, SessaoStatus.Concluida, false)]
    public void Transicoes_permitidas(SessaoStatus de, SessaoStatus para, bool permitida)
        => ComparacaoSessao.PodeTransicionar(de, para).Should().Be(permitida);

    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados)]
    [InlineData(SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Comparando)]
    public void Qualquer_fase_em_andamento_pode_falhar_ou_ficar_inviavel(SessaoStatus de)
    {
        ComparacaoSessao.PodeTransicionar(de, SessaoStatus.Falha).Should().BeTrue();
        ComparacaoSessao.PodeTransicionar(de, SessaoStatus.Inviavel).Should().BeTrue();
    }

    [Theory]
    [InlineData(SessaoStatus.Inviavel, SessaoStatus.AguardandoDados, true)]
    [InlineData(SessaoStatus.Falha, SessaoStatus.AguardandoDados, true)]
    public void Inviavel_ou_falha_permite_reenviar_zip_e_voltar_para_aguardando_dados(
        SessaoStatus de, SessaoStatus para, bool permitida)
        => ComparacaoSessao.PodeTransicionar(de, para).Should().Be(permitida);

    [Fact]
    public void Concluida_e_terminal_para_todos_os_estados()
    {
        foreach (var destino in Enum.GetValues<SessaoStatus>())
        {
            ComparacaoSessao.PodeTransicionar(SessaoStatus.Concluida, destino).Should()
                .BeFalse($"Concluida não deveria permitir transição para {destino}");
        }
    }

    [Fact]
    public void Nenhum_estado_transiciona_para_si_mesmo()
    {
        foreach (var estado in Enum.GetValues<SessaoStatus>())
        {
            ComparacaoSessao.PodeTransicionar(estado, estado).Should()
                .BeFalse($"{estado} não deveria transicionar para si mesmo — a tabela atual não prevê nenhuma auto-transição");
        }
    }

    [Theory]
    [InlineData(SessaoStatus.AguardandoDados)]
    [InlineData(SessaoStatus.AguardandoQuestionario)]
    [InlineData(SessaoStatus.Inviavel)]
    [InlineData(SessaoStatus.Falha)]
    public void Fora_das_fases_em_andamento_a_sessao_pode_ser_excluida(SessaoStatus status)
        => ComparacaoSessao.PodeExcluir(status).Should().BeTrue();

    /// <summary>
    /// A segunda recusa de <c>PodeExcluir</c>, por motivo diferente das fases em andamento:
    /// aqui não há job a proteger, há dado. Concluída significa que o comprador respondeu o
    /// questionário, e resposta de pesquisa não desaparece por clique. Vale notar que a
    /// equivalência "concluída ⟺ respondida" é o que a migration garante ao reclassificar as
    /// sessões que já estavam concluídas antes do questionário existir.
    /// </summary>
    [Fact]
    public void Concluida_recusa_exclusao_porque_a_resposta_esta_selada()
        => ComparacaoSessao.PodeExcluir(SessaoStatus.Concluida).Should().BeFalse();

    /// <summary>
    /// O questionário é fase de humano: nenhuma fila a reclama, então não há job que possa
    /// falhar nem pré-condição que possa torná-la inviável. Uma aresta para <c>Falha</c> aqui
    /// seria código morto — e pior, sugeriria a quem lê a tabela que existe um worker por trás.
    /// A sessão sai daqui por envio (<c>Concluida</c>) ou por exclusão.
    /// </summary>
    [Fact]
    public void Aguardando_questionario_so_sai_por_envio()
    {
        var destinos = Enum.GetValues<SessaoStatus>()
            .Where(d => ComparacaoSessao.PodeTransicionar(SessaoStatus.AguardandoQuestionario, d))
            .ToList();

        destinos.Should().Equal(SessaoStatus.Concluida);
    }

    /// <summary>
    /// A recusa protege o job, não o dado: nessas três fases existe uma carga, um treino ou
    /// uma comparação trabalhando pela sessão, e apagá-la deixaria o worker terminando no
    /// vazio — ou materializando resultado para uma sessão que não existe mais.
    /// </summary>
    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados)]
    [InlineData(SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Comparando)]
    public void Fase_em_andamento_recusa_exclusao(SessaoStatus status)
        => ComparacaoSessao.PodeExcluir(status).Should().BeFalse();

    /// <summary>
    /// Toda fase em andamento tem saída para um estado excluível — é isso que garante que
    /// nenhuma sessão fique impossível de excluir para sempre, e é por isso que
    /// <c>PodeExcluir</c> não precisa de um escape por "abandonada".
    /// </summary>
    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados)]
    [InlineData(SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Comparando)]
    public void Toda_fase_em_andamento_tem_saida_para_um_estado_excluivel(SessaoStatus status)
    {
        var destinos = Enum.GetValues<SessaoStatus>()
            .Where(d => ComparacaoSessao.PodeTransicionar(status, d))
            .ToList();

        destinos.Should().NotBeEmpty($"{status} precisa ter alguma transição de saída");
        destinos.Should().Contain(d => ComparacaoSessao.PodeExcluir(d),
            $"{status} precisa poder alcançar um estado excluível, senão a sessão fica presa para sempre");
    }
}
