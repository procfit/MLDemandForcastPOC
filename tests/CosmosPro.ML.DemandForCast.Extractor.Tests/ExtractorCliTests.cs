using CosmosPro.ML.DemandForCast.Extractor;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Saída de erro do modo linha de comando. Uma extração contra o PBS real morreu
/// imprimindo só "Unable to cast object of type 'System.Decimal' to type
/// 'System.Int32'." — sem query, sem coluna, sem pilha. O que estes testes fixam
/// é o mínimo que a mensagem tem de carregar para o operador não precisar
/// adivinhar entre uma dúzia de consultas, e que a pilha completa (chave
/// <see cref="ExtratorErro.ChaveDetalhe"/>) só aparece quando pedida.
/// </summary>
public sealed class ExtractorCliTests
{
    private static readonly Etapa EtapaQualquer = new("sugestoes_compra_itens.csv", "sugestoes_compra_itens.sql");

    private static ExtratorErro ErroDeConversao() => ClassificadorDeFalha.Classificar(
        new FalhaBruta(
            typeof(InvalidCastException),
            "Unable to cast object of type 'System.Decimal' to type 'System.Int32'.",
            SqlNumber: null,
            ConexaoJaAberta: false,
            DetalheCompleto: "System.InvalidCastException: pilha completa aqui"),
        EtapaQualquer,
        TimeSpan.FromSeconds(3));

    private static (int Codigo, string Saida) Falhar(ExtratorErro erro, bool comStackTrace)
    {
        var resultado = Result.Fail<object>(erro);

        var escritorOriginal = Console.Error;
        var escritor = new StringWriter();
        Console.SetError(escritor);
        try
        {
            var codigo = ExtractorCli.Falhar(resultado, comStackTrace);
            return (codigo, escritor.ToString());
        }
        finally
        {
            Console.SetError(escritorOriginal);
        }
    }

    [Fact]
    public void Mensagem_nomeia_a_etapa_quando_a_falha_veio_da_extracao()
    {
        var (_, saida) = Falhar(ErroDeConversao(), comStackTrace: false);

        saida.Should()
            .Contain("sugestoes_compra_itens.sql")
            .And.Contain("System.Decimal");
    }

    [Fact]
    public void Sem_a_flag_o_detalhe_completo_fica_de_fora_e_a_mensagem_ensina_a_flag()
    {
        var (_, saida) = Falhar(ErroDeConversao(), comStackTrace: false);

        saida.Should()
            .Contain(CliParser.FlagStackTrace)
            .And.NotContain(ExtratorErro.ChaveDetalhe, "a pilha só deve aparecer sob pedido explícito");
    }

    [Fact]
    public void Com_a_flag_o_detalhe_completo_entra_na_saida()
    {
        var (_, saida) = Falhar(ErroDeConversao(), comStackTrace: true);

        saida.Should()
            .Contain(ExtratorErro.ChaveDetalhe)
            .And.Contain("pilha completa aqui")
            .And.NotContain(CliParser.FlagStackTrace, "com a pilha em mãos, sugerir a flag de novo é ruído");
    }

    [Fact]
    public void Erro_fora_da_extracao_ainda_diz_o_que_aconteceu()
    {
        var erro = ClassificadorDeFalha.Classificar(
            new FalhaBruta(typeof(TimeoutException), "o servidor não respondeu", SqlNumber: null, ConexaoJaAberta: false, DetalheCompleto: "d"),
            EtapaQualquer,
            TimeSpan.FromSeconds(1));

        var (_, saida) = Falhar(erro, comStackTrace: false);

        saida.Should().Contain("o servidor não respondeu");
    }

    [Fact]
    public void O_codigo_de_saida_vem_do_mapa_e_nao_e_generico_para_toda_falha()
    {
        var (codigo, _) = Falhar(new SugestaoNaoEncontradaErro(1), comStackTrace: false);

        codigo.Should().Be(CliExitCode.SugestaoNaoEncontrada);
    }

    [Fact]
    public void Falha_sem_erro_tipado_nao_estoura_e_ainda_mostra_mensagem()
    {
        // Um IError genérico (fora do que os serviços deste projeto produzem) não
        // pode fazer .First() estourar InvalidOperationException.
        var resultado = Result.Fail<object>(new Error("falha genérica de outra origem"));

        var escritorOriginal = Console.Error;
        var escritor = new StringWriter();
        Console.SetError(escritor);
        try
        {
            var acao = () => ExtractorCli.Falhar(resultado, comStackTrace: false);

            acao.Should().NotThrow();
            escritor.ToString().Should().Contain("falha genérica de outra origem");
        }
        finally
        {
            Console.SetError(escritorOriginal);
        }
    }
}
