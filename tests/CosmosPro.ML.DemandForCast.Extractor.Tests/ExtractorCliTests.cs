using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Saída de erro do modo linha de comando. Uma extração contra o PBS real morreu
/// imprimindo só "Unable to cast object of type 'System.Decimal' to type
/// 'System.Int32'." — sem query, sem coluna, sem pilha. O que estes testes fixam
/// é o mínimo que a mensagem tem de carregar para o operador não precisar
/// adivinhar entre uma dúzia de consultas.
/// </summary>
public sealed class ExtractorCliTests
{
    [Fact]
    public void Mensagem_nomeia_a_etapa_quando_a_falha_veio_da_extracao()
    {
        var erro = new EtapaFalhouException(
            new Etapa("sugestoes_compra_itens.csv", "sugestoes_compra_itens.sql"),
            new InvalidCastException("Unable to cast object of type 'System.Decimal' to type 'System.Int32'."));

        var texto = ExtractorCli.MensagemDeFalha(erro, comStackTrace: false);

        texto.Should()
            .Contain("sugestoes_compra_itens.sql")
            .And.Contain("System.Decimal");
    }

    [Fact]
    public void Mensagem_traz_o_tipo_da_causa_e_nao_o_do_embrulho()
    {
        // InvalidCastException é o que aponta para coluna sem CONVERT na query;
        // o tipo do embrulho não diria nada a quem lê o log.
        var erro = new EtapaFalhouException(new Etapa("produtos.csv", "produtos.sql"), new InvalidCastException("x"));

        ExtractorCli.MensagemDeFalha(erro, comStackTrace: false)
            .Should().Contain(typeof(InvalidCastException).FullName!);
    }

    [Fact]
    public void Sem_a_flag_a_mensagem_ensina_a_flag_em_vez_de_despejar_a_pilha()
    {
        var texto = ExtractorCli.MensagemDeFalha(new InvalidOperationException("falhou"), comStackTrace: false);

        texto.Should().Contain(CliParser.FlagStackTrace);
    }

    [Fact]
    public void Com_a_flag_a_pilha_entra_na_saida()
    {
        Exception capturada;
        try
        {
            throw new InvalidOperationException("falhou");
        }
        catch (Exception ex)
        {
            capturada = ex;
        }

        var texto = ExtractorCli.MensagemDeFalha(capturada, comStackTrace: true);

        texto.Should()
            .Contain(nameof(ExtractorCliTests))
            .And.NotContain(CliParser.FlagStackTrace, "com a pilha em mãos, sugerir a flag de novo é ruído");
    }

    [Fact]
    public void Erro_fora_da_extracao_ainda_diz_o_que_aconteceu()
    {
        var texto = ExtractorCli.MensagemDeFalha(new TimeoutException("o servidor não respondeu"), comStackTrace: false);

        texto.Should()
            .Contain("o servidor não respondeu")
            .And.Contain(typeof(TimeoutException).FullName!);
    }
}
