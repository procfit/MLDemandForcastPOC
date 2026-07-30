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
    [InlineData(SessaoStatus.Comparando, SessaoStatus.Concluida, true)]
    [InlineData(SessaoStatus.AguardandoDados, SessaoStatus.Concluida, false)]
    [InlineData(SessaoStatus.Concluida, SessaoStatus.Treinando, false)]
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
}
