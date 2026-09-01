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

    [Fact]
    public void Empresa_diverge_de_filial_e_erro_de_argumento()
    {
        // Mesma categoria dos outros erros de recorte: a instalação do PBS não sustenta
        // a garantia que o --stores pediu, e o remédio é escolher de novo (sem o recorte),
        // não repetir o mesmo comando esperando um resultado diferente.
        CliExitCodeMap.De(new EmpresaDivergeDeFilialErro(3)).Should().Be(CliExitCode.ArgumentosInvalidos);
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
    /// <summary>
    /// O lado legível por máquina do bug de 2026-09-01. Antes da correção, um
    /// <c>Invalid column name</c> (SQL 207) era classificado como conexão perdida e saía com
    /// <see cref="CliExitCode.FalhaDeConexao"/> — código que um script de automação trata
    /// retentando. Erro de consulta não melhora com retentativa.
    /// </summary>
    [Fact]
    public void Erro_de_consulta_nao_sai_como_falha_de_conexao()
    {
        var erro = ClassificadorDeFalha.Classificar(
            new FalhaBruta(typeof(InvalidOperationException), "Invalid column name 'CGC'.",
                           SqlNumber: 207, ConexaoJaAberta: true, DetalheCompleto: "detalhe",
                           SqlSeveridade: 16),
            new Etapa("lojas.csv", "lojas.sql"),
            TimeSpan.FromSeconds(1));

        CliExitCodeMap.De(erro).Should().NotBe(CliExitCode.FalhaDeConexao,
            "quem dirige o CLI retenta em falha de conexão, e retentar 207 chega à mesma recusa");
        CliExitCodeMap.De(erro).Should().Be(CliExitCode.FalhaNaExtracao);
    }

}
