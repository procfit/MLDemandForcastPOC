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

    // Sem bomba de mensagens: não é o cenário real (o form fechado gera Dispose em
    // cascata pelo próprio WinForms), mas descartar o controle na mão antes de chamar
    // Dispose() reproduz o mesmo ObjectDisposedException que dispara lá — o suficiente
    // para provar que um controle perdido não trava a restauração dos outros.
    [Fact]
    public void Dispose_restaura_os_demais_controles_mesmo_quando_um_ja_foi_descartado()
    {
        using var sobrevivente = new Button();
        var descartado = new Button();
        using var cancelar = new Button();
        using var progresso = new ProgressBar();
        using var status = new Label();

        var alvos = new AlvosDaOperacao([sobrevivente, descartado], cancelar, progresso, status);
        var escopo = OperacaoUi.Iniciar(alvos, "Testando", totalDeEtapas: null);

        sobrevivente.Enabled.Should().BeFalse();
        descartado.Dispose();

        var acao = () => escopo.Dispose();

        acao.Should().NotThrow();
        sobrevivente.Enabled.Should().BeTrue();
        cancelar.Enabled.Should().BeFalse();
    }
}
