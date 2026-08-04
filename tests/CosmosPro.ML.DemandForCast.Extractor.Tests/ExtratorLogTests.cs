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

    [Theory]
    [InlineData("""Data Source=x;User ID=dev;Password="Secret;Value";Encrypt=True""", "Value")]
    [InlineData("""Data Source=x;Password='Outra;Teste';Encrypt=True""", "Teste")]
    public void Senha_entre_aspas_com_ponto_e_virgula_nao_deixa_fragmento(string connectionString, string fragmento)
    {
        // SqlConnectionStringBuilder coloca a senha entre aspas quando ela contém ';',
        // e a redação que parava no primeiro ';' deixava o resto do segredo no log.
        ExtratorLog.Redigir(connectionString).Should().NotContain(fragmento);
    }

    [Fact]
    public void Senha_com_aspa_dupla_interna_escapada_nao_deixa_fragmento()
    {
        var texto = ExtratorLog.Redigir(
            """Data Source=x;Password="Sec""re't";Encrypt=True""");

        texto.Should().NotContain("re't");
    }

    [Fact]
    public void Senha_com_aspa_simples_interna_escapada_nao_deixa_fragmento()
    {
        var texto = ExtratorLog.Redigir(
            """Data Source=x;Password='Sec''re"t';Encrypt=True""");

        texto.Should().NotContain("""re"t""");
    }

    [Fact]
    public void Nao_redige_alem_da_senha_quando_ha_outra_palavra_chave_entre_aspas_depois()
    {
        var texto = ExtratorLog.Redigir("""Password="a";Other="b" """);

        // O `(?:[^"]|"")*` guloso não pode atravessar `a";Other="b`: os dois `"` ali não
        // são adjacentes (nem casam `""`, nem sobram como `[^"]`), então o motor
        // retrocede para `"a"` e devolve `Other="b"` intacto.
        texto.Should().Contain("""Other="b" """);
    }

    [Fact]
    public void Aspa_nao_fechada_apos_password_ainda_e_redigida()
    {
        var texto = ExtratorLog.Redigir("""Data Source=x;Password="NuncaFecha;Encrypt=True""");

        texto.Should().NotContain("NuncaFecha");
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
