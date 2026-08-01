namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class CliEnvironmentTests
{
    private static readonly Dictionary<string, string> Completo = new()
    {
        ["PBS_MSSQL_SERVER"] = "servidor.exemplo",
        ["PBS_MSSQL_DATABASE"] = "PBS_DADOS",
        ["PBS_MSSQL_USER"] = "leitor",
        ["PBS_MSSQL_PASSWORD"] = "segredo",
    };

    private static CliEnvironmentResult Resolver(
        IReadOnlyDictionary<string, string> ambiente, params string[] args)
    {
        var options = CliParser.Parse(args).Options!;
        return CliEnvironment.Resolve(options, nome => ambiente.GetValueOrDefault(nome));
    }

    [Fact]
    public void Prefixo_padrao_le_as_quatro_variaveis_obrigatorias()
    {
        var r = Resolver(Completo, "--list");

        r.Erro.Should().BeNull();
        r.Config!.Servidor.Should().Be("servidor.exemplo");
        r.Config.Banco.Should().Be("PBS_DADOS");
        r.Config.Usuario.Should().Be("leitor");
        r.Config.WindowsAuth.Should().BeFalse();
        r.Senha.Should().Be("segredo");
    }

    [Fact]
    public void Prefixo_da_linha_de_comando_monta_os_nomes_da_rede()
    {
        // É este esquema que permite uma segunda rede sem mudar código: o mesmo
        // sufixo, outro prefixo.
        var ambiente = new Dictionary<string, string>
        {
            ["NATUSFARMA_PBS_PROD_MSSQL_SERVER"] = "natusfarma.exemplo",
            ["NATUSFARMA_PBS_PROD_MSSQL_DATABASE"] = "PBS_NATUSFARMA_DADOS",
            ["NATUSFARMA_PBS_PROD_MSSQL_USER"] = "u",
            ["NATUSFARMA_PBS_PROD_MSSQL_PASSWORD"] = "s",
        };

        var r = Resolver(ambiente, "--list", "--env-prefix", "NATUSFARMA_PBS_PROD_");

        r.Erro.Should().BeNull();
        r.Config!.Servidor.Should().Be("natusfarma.exemplo");
        r.Config.Banco.Should().Be("PBS_NATUSFARMA_DADOS");
    }

    [Theory]
    [InlineData("PBS_MSSQL_SERVER")]
    [InlineData("PBS_MSSQL_DATABASE")]
    [InlineData("PBS_MSSQL_USER")]
    [InlineData("PBS_MSSQL_PASSWORD")]
    public void Variavel_faltando_e_nomeada_na_mensagem(string ausente)
    {
        var ambiente = Completo.Where(p => p.Key != ausente).ToDictionary();

        var r = Resolver(ambiente, "--list");

        r.Config.Should().BeNull();
        r.Erro.Should().Contain(ausente);
    }

    [Fact]
    public void Variavel_em_branco_conta_como_ausente()
    {
        var ambiente = new Dictionary<string, string>(Completo) { ["PBS_MSSQL_SERVER"] = "   " };

        Resolver(ambiente, "--list").Erro.Should().Contain("PBS_MSSQL_SERVER");
    }

    [Fact]
    public void Integrated_security_dispensa_usuario_e_senha()
    {
        var ambiente = new Dictionary<string, string>
        {
            ["PBS_MSSQL_SERVER"] = "servidor.exemplo",
            ["PBS_MSSQL_DATABASE"] = "PBS_DADOS",
        };

        var r = Resolver(ambiente, "--list", "--integrated-security");

        r.Erro.Should().BeNull();
        r.Config!.WindowsAuth.Should().BeTrue();
        r.Config.Usuario.Should().BeEmpty();
        r.Senha.Should().BeEmpty();
    }

    [Fact]
    public void Porta_vem_do_padrao_da_variavel_ou_da_flag_nesta_ordem()
    {
        Resolver(Completo, "--list").Config!.Porta.Should().Be(CliEnvironment.PortaPadrao);

        var comVariavel = new Dictionary<string, string>(Completo) { ["PBS_MSSQL_PORT"] = "1435" };
        Resolver(comVariavel, "--list").Config!.Porta.Should().Be(1435);

        Resolver(comVariavel, "--list", "--port", "1444").Config!.Porta.Should().Be(1444,
            "--port precede a variável de ambiente");
    }

    [Fact]
    public void Porta_invalida_na_variavel_e_recusada_nomeando_a_variavel()
    {
        var ambiente = new Dictionary<string, string>(Completo) { ["PBS_MSSQL_PORT"] = "não-é-porta" };

        var r = Resolver(ambiente, "--list");

        r.Config.Should().BeNull();
        r.Erro.Should().Contain("PBS_MSSQL_PORT");
    }

    [Fact]
    public void Application_name_vem_da_variavel_e_a_flag_precede()
    {
        var ambiente = new Dictionary<string, string>(Completo) { ["PBS_MSSQL_APP_NAME"] = "do-ambiente" };

        Resolver(Completo, "--list").Config!.ApplicationName.Should().BeEmpty();
        Resolver(ambiente, "--list").Config!.ApplicationName.Should().Be("do-ambiente");
        Resolver(ambiente, "--list", "--app-name", "da-flag").Config!.ApplicationName.Should().Be("da-flag");
    }

    [Fact]
    public void Connection_string_resultante_carrega_porta_usuario_e_senha()
    {
        var ambiente = new Dictionary<string, string>(Completo) { ["PBS_MSSQL_PORT"] = "1435" };
        var r = Resolver(ambiente, "--list");

        var connectionString = ConnectionStringFactory.Build(r.Config!, r.Senha);

        connectionString.Should()
            .Contain("servidor.exemplo,1435")
            .And.Contain("PBS_DADOS")
            .And.Contain("leitor")
            .And.Contain("segredo");
    }
}
