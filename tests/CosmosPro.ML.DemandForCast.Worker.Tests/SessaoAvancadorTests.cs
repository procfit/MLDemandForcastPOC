using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// A decisão de avanço da sessão é pura de propósito: sem ela isolada, verificar o fluxo
/// exigiria subir banco, fila e três workers para afirmar uma transição.
/// </summary>
public sealed class SessaoAvancadorTests
{
    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados, JobResultado.Concluido, SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Treinando, JobResultado.Concluido, SessaoStatus.Comparando)]
    [InlineData(SessaoStatus.Comparando, JobResultado.Concluido, SessaoStatus.Concluida)]
    [InlineData(SessaoStatus.Treinando, JobResultado.Falhou, SessaoStatus.Falha)]
    [InlineData(SessaoStatus.Treinando, JobResultado.EmAndamento, SessaoStatus.Treinando)]
    public void Proximo_estado(SessaoStatus atual, JobResultado r, SessaoStatus esperado)
        => SessaoAvancador.ProximoEstado(atual, r).Should().Be(esperado);

    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados)]
    [InlineData(SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Comparando)]
    public void Job_que_falha_em_qualquer_fase_termina_a_sessao_em_falha(SessaoStatus atual)
        => SessaoAvancador.ProximoEstado(atual, JobResultado.Falhou).Should().Be(SessaoStatus.Falha);

    [Theory]
    [InlineData(SessaoStatus.ProcessandoDados)]
    [InlineData(SessaoStatus.Treinando)]
    [InlineData(SessaoStatus.Comparando)]
    public void Job_em_andamento_deixa_a_sessao_onde_esta(SessaoStatus atual)
        => SessaoAvancador.ProximoEstado(atual, JobResultado.EmAndamento).Should().Be(atual);

    /// <summary>
    /// Terminal é terminal: uma sessão concluída não pode ser arrastada de volta ao fluxo
    /// por um job antigo mudando de estado, e <c>Inviavel</c>/<c>Falha</c> só saem dali por
    /// reenvio do usuário (ver <see cref="ComparacaoSessao.PodeTransicionar"/>).
    /// </summary>
    [Theory]
    [InlineData(SessaoStatus.AguardandoDados)]
    [InlineData(SessaoStatus.Concluida)]
    [InlineData(SessaoStatus.Inviavel)]
    [InlineData(SessaoStatus.Falha)]
    public void Estado_fora_das_fases_intermediarias_nao_se_move(SessaoStatus atual)
    {
        foreach (var resultado in Enum.GetValues<JobResultado>())
        {
            SessaoAvancador.ProximoEstado(atual, resultado).Should().Be(atual,
                $"{atual} não é fase intermediária e não tem job de fase para observar");
        }
    }

    /// <summary>
    /// Amarra a decisão à máquina de estados em vez de deixar as duas evoluírem soltas: um
    /// destino que <c>PodeTransicionar</c> rejeita viraria UPDATE de zero linha, e a sessão
    /// ficaria parada para sempre sem ninguém reclamar.
    /// </summary>
    [Fact]
    public void Todo_avanco_proposto_e_aceito_pela_maquina_de_estados()
    {
        foreach (var atual in Enum.GetValues<SessaoStatus>())
        {
            foreach (var resultado in Enum.GetValues<JobResultado>())
            {
                var proximo = SessaoAvancador.ProximoEstado(atual, resultado);
                if (proximo == atual) continue;

                ComparacaoSessao.PodeTransicionar(atual, proximo).Should().BeTrue(
                    $"o avanço {atual} -> {proximo} (job {resultado}) precisa ser uma transição permitida");
            }
        }
    }
}
