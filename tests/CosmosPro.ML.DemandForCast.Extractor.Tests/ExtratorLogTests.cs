using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// O operador roda o extrator num terminal do cliente e a única coisa que ele pode
/// reportar é o que sobrou escrito. E o que sobra escrito não pode conter a senha
/// do ERP de produção.
/// </summary>
public sealed class ExtratorLogTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "extrator-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    private ExtratorLog Log(Action<string>? tela = null, DateTime? dia = null) =>
        new(_pasta, tela, () => dia ?? new DateTime(2026, 8, 4, 11, 6, 36));

    [Fact]
    public void Redige_a_senha_da_connection_string()
    {
        var texto = ExtratorLog.Redigir(
            "Data Source=natusfarma.procfit.com.br,1435;Initial Catalog=PBS;User ID=dev;Password=SenhaSuperSecreta;Encrypt=True");

        texto.Should().NotContain("SenhaSuperSecreta");
        texto.Should().Contain("natusfarma.procfit.com.br,1435");
        texto.Should().Contain("User ID=dev");
    }

    [Theory]
    [InlineData("Password=x;")]
    [InlineData("password=x;")]
    [InlineData("PWD=x;")]
    [InlineData("Pwd = x ;")]
    public void Redige_todas_as_grafias_de_senha(string trecho)
    {
        ExtratorLog.Redigir("a;" + trecho + "b=1").Should().NotContain("x");
    }

    [Fact]
    public void Texto_sem_senha_passa_intacto()
    {
        ExtratorLog.Redigir("19.581 sugestões em 0,3s").Should().Be("19.581 sugestões em 0,3s");
    }

    [Fact]
    public void Escreve_na_tela_e_no_arquivo()
    {
        var tela = new List<string>();
        var log = Log(tela.Add);

        log.Escrever("Carregando sugestões...");

        tela.Should().ContainSingle().Which.Should().Contain("Carregando sugestões...");
        File.ReadAllText(log.CaminhoDeHoje).Should().Contain("Carregando sugestões...");
    }

    [Fact]
    public void A_linha_leva_a_hora()
    {
        var log = Log();

        log.Escrever("x");

        File.ReadAllText(log.CaminhoDeHoje).Should().Contain("11:06:36");
    }

    [Fact]
    public void Detalhe_completo_vai_so_para_o_arquivo()
    {
        var tela = new List<string>();
        var log = Log(tela.Add);

        log.EscreverSoNoArquivo("System.InvalidCastException: pilha inteira aqui");

        tela.Should().BeEmpty();
        File.ReadAllText(log.CaminhoDeHoje).Should().Contain("pilha inteira aqui");
    }

    [Fact]
    public void Linhas_sucessivas_sao_acrescentadas_e_nao_sobrescritas()
    {
        var log = Log();

        log.Escrever("primeira");
        log.Escrever("segunda");

        var conteudo = File.ReadAllText(log.CaminhoDeHoje);
        conteudo.Should().Contain("primeira");
        conteudo.Should().Contain("segunda");
    }

    [Fact]
    public void Um_arquivo_por_dia()
    {
        ExtratorLog.NomeDoArquivo(new DateTime(2026, 8, 4)).Should().Be("extrator-log-2026-08-04.txt");
        ExtratorLog.NomeDoArquivo(new DateTime(2026, 12, 31)).Should().Be("extrator-log-2026-12-31.txt");
    }

    [Fact]
    public void A_senha_e_redigida_antes_de_chegar_a_qualquer_um_dos_dois_destinos()
    {
        var tela = new List<string>();
        var log = Log(tela.Add);

        log.Escrever("conectando com Password=Secreta123;");

        tela.Single().Should().NotContain("Secreta123");
        File.ReadAllText(log.CaminhoDeHoje).Should().NotContain("Secreta123");
    }

    [Fact]
    public void Pasta_inacessivel_nao_derruba_a_operacao()
    {
        // Perder o log é ruim; perder a extração porque o log falhou é pior.
        var tela = new List<string>();
        var log = new ExtratorLog(Path.Combine("Z:", "nao", "existe"), tela.Add, () => DateTime.Now);

        var acao = () => log.Escrever("x");

        acao.Should().NotThrow();
        tela.Should().ContainSingle();
    }
}
