using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O texto do rodapé é o que faltou quando o operador esperou 2min09 sem saber se a
/// operação estava andando. Ele é testado; o resto de OperacaoUi mexe em Control e
/// depende de bomba de mensagens, então fica pequeno de propósito.
/// </summary>
public sealed class OperacaoUiTests
{
    [Fact]
    public void Status_leva_o_relogio_para_lento_nao_parecer_travado()
    {
        var texto = OperacaoUi.TextoDeStatus("Carregando sugestões", TimeSpan.FromSeconds(12), detalhe: null);

        texto.Should().Contain("Carregando sugestões");
        texto.Should().Contain("12s");
    }

    [Fact]
    public void Status_passa_de_um_minuto_em_minutos_e_segundos()
    {
        OperacaoUi.TextoDeStatus("Extraindo", TimeSpan.FromSeconds(129), null).Should().Contain("2min09");
    }

    [Fact]
    public void Detalhe_da_etapa_entra_no_status_sem_esconder_o_relogio()
    {
        var texto = OperacaoUi.TextoDeStatus("Extraindo", TimeSpan.FromSeconds(5), "[3/9] vendas.csv — 25.000 linhas");

        texto.Should().Contain("[3/9] vendas.csv");
        texto.Should().Contain("5s");
    }

    [Fact]
    public void Zero_segundos_ainda_mostra_o_relogio()
    {
        OperacaoUi.TextoDeStatus("Testando conexão", TimeSpan.Zero, null).Should().Contain("0s");
    }

    private static (AlvosDaOperacao Alvos, ProgressBar Barra) Alvos()
    {
        var barra = new ProgressBar();
        return (new AlvosDaOperacao([new Button()], new Button(), barra, new Label()), barra);
    }

    [Fact]
    public void Barra_volta_a_zero_quando_a_operacao_termina()
    {
        // Uma extração cancelada no passo 5 de 9 deixava a barra parada em 55% para
        // sempre, afirmando que algo está meio pronto quando nada está rodando.
        var (alvos, barra) = Alvos();

        using (var escopo = OperacaoUi.Iniciar(alvos, "Extraindo", totalDeEtapas: 9))
        {
            escopo.Reportar("[5/9] compras.csv", 5);
            barra.Value.Should().Be(5);
        }

        barra.Value.Should().Be(barra.Minimum);
    }

    [Fact]
    public void Barra_para_de_animar_quando_a_operacao_termina()
    {
        var (alvos, barra) = Alvos();
        var estiloAntes = barra.Style;

        using (OperacaoUi.Iniciar(alvos, "Carregando sugestões", totalDeEtapas: null))
        {
            barra.Style.Should().Be(ProgressBarStyle.Marquee);
        }

        barra.Style.Should().Be(estiloAntes);
    }
}
