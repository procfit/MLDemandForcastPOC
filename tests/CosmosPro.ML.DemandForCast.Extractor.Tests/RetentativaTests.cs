using CosmosPro.ML.DemandForCast.Extractor;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Retry silencioso é a mesma desonestidade que este trabalho existe para tirar,
/// com outro nome: o operador precisa ver que houve segunda tentativa.
/// </summary>
public sealed class RetentativaTests
{
    private static readonly Etapa Qualquer = new("catálogo", "catalogo_sugestoes.sql");

    private static ExtratorErro Transitorio() => new ConexaoPerdidaErro(Qualquer, TimeSpan.FromSeconds(3));

    private static ExtratorErro Definitivo() => new SugestaoNaoEncontradaErro(1);

    [Fact]
    public void Sucesso_de_primeira_nao_dorme_nem_registra_tentativa()
    {
        var dormidas = new List<TimeSpan>();
        var log = new List<string>();

        var resultado = Retentativa.Executar(() => Result.Ok(7), 3, log.Add, dormidas.Add);

        resultado.Value.Should().Be(7);
        dormidas.Should().BeEmpty();
        log.Should().BeEmpty();
    }

    [Fact]
    public void Falha_transitoria_seguida_de_sucesso_devolve_o_sucesso()
    {
        var chamadas = 0;
        var log = new List<string>();

        var resultado = Retentativa.Executar(
            () => ++chamadas == 1 ? Result.Fail<int>(Transitorio()) : Result.Ok(42),
            3, log.Add, _ => { });

        resultado.IsSuccess.Should().BeTrue();
        resultado.Value.Should().Be(42);
        chamadas.Should().Be(2);
    }

    [Fact]
    public void A_retentativa_aparece_no_log()
    {
        var log = new List<string>();

        Retentativa.Executar(() => Result.Fail<int>(Transitorio()), 3, log.Add, _ => { });

        log.Should().HaveCount(2);
        log[0].Should().Contain("tentativa 2 de 3");
        log[1].Should().Contain("tentativa 3 de 3");
    }

    [Fact]
    public void Falha_transitoria_persistente_devolve_o_ultimo_erro_depois_das_tentativas()
    {
        var chamadas = 0;

        var resultado = Retentativa.Executar(
            () => { chamadas++; return Result.Fail<int>(Transitorio()); }, 3, _ => { }, _ => { });

        chamadas.Should().Be(3);
        resultado.IsFailed.Should().BeTrue();
        resultado.Errors.Single().Should().BeOfType<ConexaoPerdidaErro>();
    }

    [Fact]
    public void Falha_definitiva_nao_e_retentada()
    {
        var chamadas = 0;

        var resultado = Retentativa.Executar(
            () => { chamadas++; return Result.Fail<int>(Definitivo()); }, 3, _ => { }, _ => { });

        chamadas.Should().Be(1);
        resultado.Errors.Single().Should().BeOfType<SugestaoNaoEncontradaErro>();
    }

    [Fact]
    public void Espera_entre_tentativas_e_a_declarada()
    {
        var dormidas = new List<TimeSpan>();

        Retentativa.Executar(() => Result.Fail<int>(Transitorio()), 3, _ => { }, dormidas.Add);

        dormidas.Should().Equal(Retentativa.EsperaEntreTentativas, Retentativa.EsperaEntreTentativas);
    }

    [Fact]
    public void Uma_tentativa_so_nunca_dorme()
    {
        var dormidas = new List<TimeSpan>();

        Retentativa.Executar(() => Result.Fail<int>(Transitorio()), 1, _ => { }, dormidas.Add);

        dormidas.Should().BeEmpty();
    }
}
