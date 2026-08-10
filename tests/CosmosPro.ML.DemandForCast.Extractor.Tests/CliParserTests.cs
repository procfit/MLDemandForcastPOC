namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void Sem_modo_pede_list_ou_extract()
    {
        var r = CliParser.Parse([]);

        r.Options.Should().BeNull();
        r.Erro.Should().Contain("--list").And.Contain("--extract");
    }

    [Fact]
    public void Help_vence_qualquer_outro_argumento()
    {
        // Quem digitou --help quer a ajuda, mesmo tendo errado o resto da linha.
        CliParser.Parse(["--extract", "--help"]).Options!.Command.Should().Be(CliCommand.Help);
        CliParser.Parse(["-h"]).Options!.Command.Should().Be(CliCommand.Help);
        CliParser.Parse(["--nao-existe", "--help"]).Erro.Should().NotBeNull(
            "argumento desconhecido antes de --help ainda é erro de digitação");
    }

    [Fact]
    public void List_usa_os_padroes_documentados()
    {
        var o = CliParser.Parse(["--list"]).Options!;

        o.Command.Should().Be(CliCommand.List);
        o.EnvPrefix.Should().Be(CliEnvironment.PrefixoPadrao);
        o.MesesRetroativos.Should().Be(CliParser.MesesRetroativosPadrao);
        o.Porta.Should().BeNull("sem --port a porta vem da variável de ambiente ou do padrão");
        o.IntegratedSecurity.Should().BeFalse();
        o.Tsv.Should().BeFalse();
    }

    [Fact]
    public void Extract_completo_e_aceito()
    {
        var o = CliParser.Parse([
            "--extract", "--suggestion-id", "12345", "--output", @"C:\extracoes",
            "--env-prefix", "NATUSFARMA_PBS_PROD_", "--port", "1435", "--app-name", "PBS",
            "--months-back", "24",
        ]).Options!;

        o.Command.Should().Be(CliCommand.Extract);
        o.SugestaoId.Should().Be(12345);
        o.OutputDirectory.Should().Be(@"C:\extracoes");
        o.EnvPrefix.Should().Be("NATUSFARMA_PBS_PROD_");
        o.Porta.Should().Be(1435);
        o.ApplicationName.Should().Be("PBS");
        o.MesesRetroativos.Should().Be(24);
    }

    [Fact]
    public void Extract_exige_id_e_pasta()
    {
        CliParser.Parse(["--extract", "--output", @"C:\x"]).Erro.Should().Contain("--suggestion-id");
        CliParser.Parse(["--extract", "--suggestion-id", "1"]).Erro.Should().Contain("--output");
    }

    [Fact]
    public void Flag_de_um_modo_nao_e_aceita_no_outro()
    {
        // Aceitar e ignorar em silêncio deixaria o operador achando que pediu algo
        // que não aconteceu.
        CliParser.Parse(["--list", "--suggestion-id", "1"]).Erro.Should().Contain("--suggestion-id");
        CliParser.Parse(["--list", "--output", @"C:\x"]).Erro.Should().Contain("--output");
        CliParser.Parse(["--list", "--stores", "12"]).Erro.Should().Contain("--stores");
        CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", @"C:\x", "--tsv"]).Erro.Should().Contain("--tsv");
    }

    [Fact]
    public void Dois_modos_juntos_sao_recusados()
    {
        CliParser.Parse(["--list", "--extract"]).Erro.Should().Contain("apenas um modo");
        CliParser.Parse(["--list", "--list"]).Erro.Should().Contain("mais de uma vez");
    }

    [Fact]
    public void Argumento_desconhecido_e_nomeado_na_mensagem()
    {
        CliParser.Parse(["--list", "--verbose"]).Erro.Should().Contain("--verbose");
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "abc")]
    [InlineData("--suggestion-id", "0")]
    [InlineData("--suggestion-id", "-1")]
    [InlineData("--suggestion-id", "abc")]
    [InlineData("--months-back", "0")]
    [InlineData("--months-back", "-3")]
    public void Valores_fora_da_faixa_sao_recusados_nomeando_a_flag(string flag, string valor)
    {
        var r = CliParser.Parse(["--list", flag, valor]);

        r.Options.Should().BeNull();
        r.Erro.Should().Contain(flag).And.Contain(valor);
    }

    [Fact]
    public void Flag_no_fim_sem_valor_e_recusada()
    {
        CliParser.Parse(["--list", "--env-prefix"]).Erro.Should().Contain("exige um valor");
        CliParser.Parse(["--list", "--port"]).Erro.Should().Contain("exige um valor");
    }

    [Fact]
    public void Stack_trace_vale_nos_dois_modos_e_e_desligada_por_padrao()
    {
        // Diagnóstico não é modo de operação: a falha que se quer investigar pode
        // estar tanto na listagem quanto na extração.
        CliParser.Parse(["--list"]).Options!.StackTrace.Should().BeFalse();
        CliParser.Parse(["--list", CliParser.FlagStackTrace]).Options!.StackTrace.Should().BeTrue();
        CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", @"C:\x", CliParser.FlagStackTrace])
            .Options!.StackTrace.Should().BeTrue();
    }

    [Fact]
    public void Integrated_security_e_tsv_sao_chaves_sem_valor()
    {
        var o = CliParser.Parse(["--list", "--integrated-security", "--tsv"]).Options!;

        o.IntegratedSecurity.Should().BeTrue();
        o.Tsv.Should().BeTrue();
    }

    [Theory]
    [InlineData("--list")]
    [InlineData("--extract")]
    [InlineData("--suggestion-id")]
    [InlineData("--output")]
    [InlineData("--months-back")]
    [InlineData("--stores")]
    [InlineData("--tsv")]
    [InlineData("--env-prefix")]
    [InlineData("--port")]
    [InlineData("--app-name")]
    [InlineData("--integrated-security")]
    [InlineData("--stack-trace")]
    public void Toda_flag_aceita_aparece_no_help(string flag)
    {
        // A ajuda é o único lugar onde o operador descobre a flag; flag nova sem
        // linha na ajuda é flag que ninguém acha.
        CliParser.HelpText.Should().Contain(flag);
    }

    [Fact]
    public void Sem_stores_significa_todas_as_lojas_da_sugestao()
    {
        var options = CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x"]).Options!;

        options.LojaIds.Should().BeNull();
    }

    [Fact]
    public void Stores_le_a_lista_de_ids()
    {
        var options = CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", "12,45,78"]).Options!;

        options.LojaIds.Should().Equal(12, 45, 78);
    }

    [Fact]
    public void Stores_tolera_espaco_e_id_repetido()
    {
        var options = CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", " 12 , 45 , 12 "]).Options!;

        options.LojaIds.Should().Equal(12, 45);
    }

    [Fact]
    public void Stores_com_id_nao_numerico_e_argumento_invalido()
    {
        CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", "12,abc"]).Erro
            .Should().NotBeNull().And.Subject.As<string>().Should().Contain("abc");
    }

    [Fact]
    public void Stores_vazio_e_argumento_invalido()
    {
        // Lista vazia nao significa "todas": e engano de digitacao, e tratar como todas
        // exportaria o oposto do pedido.
        CliParser.Parse(["--extract", "--suggestion-id", "1", "--output", "C:\\x", "--stores", "  "]).Erro
            .Should().NotBeNull();
    }

    [Fact]
    public void Help_documenta_as_variaveis_e_os_codigos_de_saida()
    {
        CliParser.HelpText.Should()
            .Contain(CliEnvironment.PrefixoPadrao + CliEnvironment.SufixoServidor)
            .And.Contain(CliEnvironment.PrefixoPadrao + CliEnvironment.SufixoBanco)
            .And.Contain(CliEnvironment.PrefixoPadrao + CliEnvironment.SufixoUsuario)
            .And.Contain(CliEnvironment.PrefixoPadrao + CliEnvironment.SufixoSenha)
            .And.Contain(CliEnvironment.PrefixoPadrao + CliEnvironment.SufixoPorta)
            .And.Contain(CliEnvironment.PrefixoPadrao + CliEnvironment.SufixoApplicationName)
            .And.Contain("CÓDIGOS DE SAÍDA");
    }
}
