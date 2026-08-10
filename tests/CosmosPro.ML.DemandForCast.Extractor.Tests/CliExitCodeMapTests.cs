using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O código de saída é contrato: quem chama o extrator de um script decide o que
/// fazer olhando o número. Ele vem do erro tipado, num mapa só, para a tela e o
/// script nunca discordarem sobre o que aconteceu.
/// </summary>
public sealed class CliExitCodeMapTests
{
    private static readonly Etapa Qualquer = new("catálogo", "catalogo_sugestoes.sql");

    [Fact]
    public void Falha_de_conexao()
    {
        CliExitCodeMap.De(new ConexaoErro("x")).Should().Be(CliExitCode.FalhaDeConexao);
    }

    [Fact]
    public void Logon_trigger_tambem_e_falha_de_conexao()
    {
        CliExitCodeMap.De(new LogonTriggerErro()).Should().Be(CliExitCode.FalhaDeConexao);
    }

    [Fact]
    public void Queda_no_meio_e_falha_de_conexao()
    {
        CliExitCodeMap.De(new ConexaoPerdidaErro(Qualquer, TimeSpan.FromSeconds(129)))
            .Should().Be(CliExitCode.FalhaDeConexao);
    }

    [Fact]
    public void Sugestao_inexistente_tem_codigo_proprio()
    {
        CliExitCodeMap.De(new SugestaoNaoEncontradaErro(1)).Should().Be(CliExitCode.SugestaoNaoEncontrada);
    }

    [Fact]
    public void Sugestao_sem_itens_e_sugestao_nao_encontrada_para_quem_chama()
    {
        // Do lado do script o desfecho é o mesmo: este id não dá para extrair.
        CliExitCodeMap.De(new SugestaoSemItensErro(1)).Should().Be(CliExitCode.SugestaoNaoEncontrada);
    }

    [Fact]
    public void Janela_inviavel_tem_codigo_proprio()
    {
        CliExitCodeMap.De(new JanelaInviavelErro("m")).Should().Be(CliExitCode.JanelaInviavel);
    }

    [Fact]
    public void Recorte_de_lojas_invalido_e_erro_de_argumento()
    {
        // Entrada errada do operador, não falha de infraestrutura: o remédio é digitar
        // outro número, e um script que veja 6 tentaria de novo à toa.
        CliExitCodeMap.De(new LojasNaoSelecionadasErro()).Should().Be(CliExitCode.ArgumentosInvalidos);
        CliExitCodeMap.De(new LojaForaDaSugestaoErro([99])).Should().Be(CliExitCode.ArgumentosInvalidos);
    }

    [Theory]
    [InlineData(typeof(ContratoErro))]
    [InlineData(typeof(EscritaErro))]
    [InlineData(typeof(TempoExcedidoErro))]
    [InlineData(typeof(EtapaErro))]
    [InlineData(typeof(ConcorrenciaErro))]
    [InlineData(typeof(InesperadoErro))]
    public void O_resto_e_falha_na_extracao(Type tipo)
    {
        ExtratorErro erro = tipo.Name switch
        {
            nameof(ContratoErro) => new ContratoErro("vendas.csv", "d"),
            nameof(EscritaErro) => new EscritaErro("C:\\x", "d"),
            nameof(TempoExcedidoErro) => new TempoExcedidoErro(Qualquer, TimeSpan.FromSeconds(30)),
            nameof(EtapaErro) => new EtapaErro(Qualquer, "d"),
            nameof(ConcorrenciaErro) => new ConcorrenciaErro(Qualquer),
            _ => new InesperadoErro(typeof(FormatException), "d"),
        };

        CliExitCodeMap.De(erro).Should().Be(CliExitCode.FalhaNaExtracao);
    }
}
