using CosmosPro.ML.DemandForCast.Extractor;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class ConnectionStringFactoryTests
{
    private static AppConfig Config() => new()
    {
        Servidor = "natusfarma.procfit.com.br",
        Porta = 1435,
        Banco = "PBS_NATUSFARMA_DADOS",
        Usuario = "dev",
    };

    private static SqlConnectionStringBuilder Construir(AppConfig config, string senha = "s") =>
        new(ConnectionStringFactory.Build(config, senha));

    [Fact]
    public void Declara_resiliencia_de_conexao()
    {
        // Reconecta conexão ociosa quebrada. Não salva comando em execução -- para
        // isso existe Retentativa -- mas é barato e cobre a abertura.
        var builder = Construir(Config());

        builder.ConnectRetryCount.Should().Be(3);
        builder.ConnectRetryInterval.Should().Be(10);
    }

    [Fact]
    public void Porta_diferente_da_padrao_entra_no_data_source()
    {
        Construir(Config()).DataSource.Should().Be("natusfarma.procfit.com.br,1435");
    }

    [Fact]
    public void Timeout_de_conexao_vem_da_configuracao()
    {
        var config = Config();
        config.TimeoutConexaoSegundos = 40;

        Construir(config).ConnectTimeout.Should().Be(40);
    }

    [Fact]
    public void Timeout_absurdo_na_configuracao_cai_no_padrao()
    {
        // O arquivo é editado à mão; um zero ali não pode virar espera infinita.
        var config = Config();
        config.TimeoutConexaoSegundos = 0;

        Construir(config).ConnectTimeout.Should().Be(15);
    }

    [Fact]
    public void Windows_auth_nao_manda_usuario_nem_senha()
    {
        var config = Config();
        config.WindowsAuth = true;

        var builder = Construir(config);

        builder.IntegratedSecurity.Should().BeTrue();
        builder.UserID.Should().BeEmpty();
    }
}
