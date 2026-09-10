using System.Text;
using CosmosPro.ML.DemandForCast.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// O extrator é um asset da imagem, colocado em <c>Assets/extrator/</c> pelo CI antes de o
/// container ser montado. O que estes testes protegem é o comportamento na **ausência** —
/// que é o estado normal do F5 e de qualquer build local, e que antes vinha de um 404 da
/// apiservice consultando o MinIO.
///
/// A distinção que importa: manifesto ilegível ou incompleto tem de virar "não há extrator",
/// nunca exceção no startup. O resto da aplicação não depende do extrator, e derrubá-la
/// inteira por causa de um asset trocaria uma tela degradada por uma aplicação morta.
/// </summary>
public sealed class ExtratorEmbutidoTests : IDisposable
{
    private readonly string _raiz = Path.Combine(
        Path.GetTempPath(), "extrator-embutido-" + Guid.NewGuid().ToString("N"));

    private string PastaDoAsset => Path.Combine(_raiz, ExtratorEmbutido.PastaRelativa);

    private ExtratorEmbutido Criar()
    {
        var ambiente = Substitute.For<IWebHostEnvironment>();
        ambiente.ContentRootPath.Returns(_raiz);
        return new ExtratorEmbutido(ambiente, NullLogger<ExtratorEmbutido>.Instance);
    }

    /// <param name="comBom">
    /// `Encoding.UTF8` do .NET emite BOM, e a sobrecarga de `JsonSerializer.Deserialize`
    /// sobre bytes não o pula — foi assim que este teste pegou o manifesto sumindo em
    /// silêncio. O default aqui é COM BOM de propósito: é a entrada mais hostil das duas.
    /// </param>
    private void Semear(string? manifesto, bool comExecutavel, bool comBom = true)
    {
        Directory.CreateDirectory(PastaDoAsset);
        if (manifesto is not null)
        {
            File.WriteAllText(
                Path.Combine(PastaDoAsset, ExtratorEmbutido.NomeManifesto),
                manifesto,
                comBom ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : new UTF8Encoding(false));
        }
        if (comExecutavel)
        {
            File.WriteAllBytes(Path.Combine(PastaDoAsset, ExtratorEmbutido.NomeExecutavel), [0x4D, 0x5A]);
        }
    }

    private const string ManifestoValido =
        """{"versao":"0.18.2","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","geradoEm":"2026-09-10T02:00:00+00:00"}""";

    [Fact]
    public void Sem_a_pasta_do_asset_nao_ha_extrator_e_nao_lanca()
    {
        var extrator = Criar();

        extrator.Versao.Should().BeNull("F5 e build local não têm o asset — quem o coloca é o CI");
        extrator.Disponivel.Should().BeFalse();
    }

    [Fact]
    public void Com_o_par_completo_le_versao_e_checksum_do_manifesto()
    {
        Semear(ManifestoValido, comExecutavel: true);

        var extrator = Criar();

        extrator.Disponivel.Should().BeTrue();
        extrator.Versao!.Versao.Should().Be("0.18.2");
        extrator.Versao.Sha256.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        extrator.Versao.GeradoEm.Should().Be(new DateTimeOffset(2026, 9, 10, 2, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Manifesto_sem_BOM_tambem_e_lido()
    {
        Semear(ManifestoValido, comExecutavel: true, comBom: false);

        var extrator = Criar();

        extrator.Disponivel.Should().BeTrue("é o que `Set-Content -Encoding utf8` do pwsh escreve hoje");
        extrator.Versao!.Versao.Should().Be("0.18.2");
    }

    /// <summary>
    /// O caso que o passo do CI confere e que este teste cobre do outro lado: manifesto sem
    /// o executável ao lado. `Disponivel` tem de ser falso mesmo com a versão lida, senão a
    /// tela habilitaria um botão de download que responde 404 — pior do que dizer que não há
    /// extrator.
    /// </summary>
    [Fact]
    public void Com_manifesto_mas_sem_executavel_nao_esta_disponivel()
    {
        Semear(ManifestoValido, comExecutavel: false);

        var extrator = Criar();

        extrator.Versao.Should().NotBeNull();
        extrator.Disponivel.Should().BeFalse();
    }

    [Theory]
    [InlineData("{ isto nao e json")]
    [InlineData("""{"versao":"","sha256":"abc","geradoEm":"2026-09-10T02:00:00+00:00"}""")]
    [InlineData("""{"sha256":"abc","geradoEm":"2026-09-10T02:00:00+00:00"}""")]
    [InlineData("""{"versao":"0.18.2","geradoEm":"2026-09-10T02:00:00+00:00"}""")]
    public void Manifesto_ilegivel_ou_incompleto_vira_ausencia_e_nao_excecao(string manifesto)
    {
        Semear(manifesto, comExecutavel: true);

        var criar = Criar;

        criar.Should().NotThrow("um asset quebrado tira o download do ar, não a aplicação");
        Criar().Versao.Should().BeNull();
        Criar().Disponivel.Should().BeFalse();
    }

    [Fact]
    public void Download_sem_extrator_responde_404_acionavel()
    {
        var resultado = ExtratorEndpoints.Download(Criar());

        var problema = resultado.Should().BeOfType<ProblemHttpResult>().Subject;
        problema.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        problema.ProblemDetails.Detail.Should().Contain("suporte técnico");
    }

    /// <summary>
    /// `enableRangeProcessing` não é enfeite: são ~118 MB indo para um servidor de farmácia,
    /// e sem ele um download interrompido recomeça do zero.
    /// </summary>
    [Fact]
    public void Download_com_extrator_devolve_o_arquivo_com_range_habilitado()
    {
        Semear(ManifestoValido, comExecutavel: true);

        var resultado = ExtratorEndpoints.Download(Criar());

        var arquivo = resultado.Should().BeOfType<PhysicalFileHttpResult>().Subject;
        arquivo.FileName.Should().Be(Path.Combine(PastaDoAsset, ExtratorEmbutido.NomeExecutavel));
        arquivo.FileDownloadName.Should().Be("extrator.exe");
        arquivo.ContentType.Should().Be("application/octet-stream");
        arquivo.EnableRangeProcessing.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }
}
