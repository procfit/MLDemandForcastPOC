using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CosmosPro.ML.DemandForCast.ApiService.Extrator;
using CosmosPro.ML.DemandForCast.ApiService.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using NSubstitute.ExceptionExtensions;

namespace CosmosPro.ML.DemandForCast.ApiService.Tests;

/// <summary>
/// Cobre a distinção central do endpoint: "ainda não publicado" (bucket/objeto ausente)
/// precisa virar 404 claro, e isso não pode ser o mesmo caminho de código que uma falha
/// de infraestrutura (MinIO fora do ar) — essa segunda precisa subir e virar 500 pelo
/// <c>UseExceptionHandler</c> do Program.cs, não ser mascarada de "não publicado".
/// </summary>
public sealed class ExtratorEndpointsTests
{
    [Fact]
    public async Task GetVersaoAsync_com_bucket_ausente_retorna_404_com_mensagem_acionavel()
    {
        var minio = Substitute.For<IMinioClient>();
        minio.GetObjectAsync(Arg.Any<GetObjectArgs>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new BucketNotFoundException());

        var resultado = await ExtratorEndpoints.GetVersaoAsync(minio, CancellationToken.None);

        var problema = resultado.Should().BeOfType<ProblemHttpResult>().Subject;
        problema.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        problema.ProblemDetails.Detail.Should().Contain("publicada ainda",
            "o comprador (ou quem lê o log) precisa de uma frase acionável, não um corpo 404 cru");
    }

    [Fact]
    public async Task GetVersaoAsync_com_objeto_ausente_retorna_404()
    {
        var minio = Substitute.For<IMinioClient>();
        minio.GetObjectAsync(Arg.Any<GetObjectArgs>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new ObjectNotFoundException());

        var resultado = await ExtratorEndpoints.GetVersaoAsync(minio, CancellationToken.None);

        resultado.Should().BeOfType<ProblemHttpResult>().Subject.StatusCode
            .Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A distinção que a tarefa pede: uma falha de conectividade com o MinIO não é
    /// "não publicado" — não pode ser engolida e virar um 404 enganoso. Ela precisa
    /// escapar do handler para o <c>UseExceptionHandler</c> global tratar como o que é.
    /// </summary>
    [Fact]
    public async Task GetVersaoAsync_com_falha_de_infraestrutura_nao_vira_404_e_propaga()
    {
        var minio = Substitute.For<IMinioClient>();
        minio.GetObjectAsync(Arg.Any<GetObjectArgs>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new HttpRequestException("conexão recusada"));

        var act = () => ExtratorEndpoints.GetVersaoAsync(minio, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>(
            "uma falha de infraestrutura precisa continuar sendo uma exceção não tratada aqui, " +
            "distinguível de 'não publicado' pelo status code que o handler global produz (500 x 404)");
    }

    [Fact]
    public async Task DownloadAsync_com_executavel_ausente_retorna_404_sem_chamar_GetObjectAsync()
    {
        var minio = Substitute.For<IMinioClient>();
        minio.StatObjectAsync(Arg.Any<StatObjectArgs>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new ObjectNotFoundException());

        var resultado = await ExtratorEndpoints.DownloadAsync(minio, CancellationToken.None);

        resultado.Should().BeOfType<ProblemHttpResult>().Subject.StatusCode
            .Should().Be(StatusCodes.Status404NotFound);

        // StatObject barra o caminho antes de qualquer tentativa de streaming: uma vez que
        // Results.Stream escrever o primeiro byte, o status 200 já foi enviado e não dá
        // mais para virar 404.
        await minio.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectArgs>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// <c>GetObjectArgs</c> não expõe <c>BucketName</c>/<c>ObjectName</c>/<c>CallBack</c>
    /// como membros acessíveis fora do assembly do Minio (a interface que os declara é
    /// interna) — não dá para inspecionar por aqui qual objeto foi pedido, nem provar que os
    /// bytes caem no corpo da resposta sem materializar; isso fica para o teste de
    /// integração, com MinIO real. O que é observável e vale a pena travar aqui:
    /// <c>DownloadAsync</c> só invoca <c>GetObjectAsync</c> — o passo que de fato inicia o
    /// streaming — depois que <c>Results.Stream</c> começa a executar a resposta, nunca
    /// antes (diferente de baixar o objeto inteiro primeiro e só então decidir o que
    /// responder).
    /// </summary>
    [Fact]
    public async Task DownloadAsync_com_executavel_publicado_so_chama_GetObjectAsync_ao_executar_a_resposta()
    {
        var minio = Substitute.For<IMinioClient>();
        minio.StatObjectAsync(Arg.Any<StatObjectArgs>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<ObjectStat>(null!));
        minio.GetObjectAsync(Arg.Any<GetObjectArgs>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<ObjectStat>(null!));

        var resultado = await ExtratorEndpoints.DownloadAsync(minio, CancellationToken.None);
        resultado.Should().NotBeOfType<ProblemHttpResult>();

        // Antes de executar a resposta, o streaming ainda não começou.
        _ = minio.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectArgs>(), Arg.Any<CancellationToken>());

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        using var corpo = new MemoryStream();
        httpContext.Response.Body = corpo;
        await resultado.ExecuteAsync(httpContext);

        _ = minio.Received(1).GetObjectAsync(Arg.Any<GetObjectArgs>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A conferência do hash é a rede contra um pacote corrompido ou montado à mão (o
    /// artefato do CI, lacrado, não erra). Se ela passasse batido, a tela mostraria ao
    /// comprador um checksum que o download não cumpre, e quem conferisse concluiria
    /// "executável adulterado". Recusar não basta: nada pode ter sido escrito no bucket até
    /// a conferência passar, senão uma publicação recusada derruba a que estava no ar.
    /// </summary>
    [Fact]
    public async Task PublicarAsync_com_sha_divergente_do_manifesto_recusa_sem_escrever_no_bucket()
    {
        var minio = Substitute.For<IMinioClient>();
        var pacote = Pacote("conteudo real"u8.ToArray(), "0.14.0", new string('a', 64));

        var resultado = await ExtratorEndpoints.PublicarAsync(
            pacote, minio, NullLogger<Program>.Instance, CancellationToken.None);

        var recusa = resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>().Subject;
        recusa.Value!.Errors.Should().ContainSingle()
              .Which.Should().Contain("não corresponde ao declarado");

        await minio.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
        await minio.DidNotReceive().MakeBucketAsync(Arg.Any<MakeBucketArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublicarAsync_com_arquivo_que_nao_e_zip_recusa()
    {
        var minio = Substitute.For<IMinioClient>();

        var resultado = await ExtratorEndpoints.PublicarAsync(
            Arquivo("extrator.zip", "isto nao e um zip"u8.ToArray()),
            minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>()
                 .Subject.Value!.Errors.Should().ContainSingle()
                 .Which.Should().Contain("não é um .zip válido");

        await minio.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublicarAsync_com_manifesto_ilegivel_recusa_sem_escrever_no_bucket()
    {
        var minio = Substitute.For<IMinioClient>();
        var pacote = Zip(("extrator.exe", "conteudo real"u8.ToArray()),
                         ("manifesto.json", "isto nao e json"u8.ToArray()));

        var resultado = await ExtratorEndpoints.PublicarAsync(
            pacote, minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>()
                 .Subject.Value!.Errors.Should().ContainSingle()
                 .Which.Should().Contain("não é um JSON válido");

        await minio.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("extrator.exe")]
    [InlineData("manifesto.json")]
    public async Task PublicarAsync_com_pacote_incompleto_recusa_nomeando_o_que_falta(string presente)
    {
        var minio = Substitute.For<IMinioClient>();
        var ausente = presente == "extrator.exe" ? "manifesto.json" : "extrator.exe";

        var resultado = await ExtratorEndpoints.PublicarAsync(
            Zip((presente, "conteudo"u8.ToArray())),
            minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>()
                 .Subject.Value!.Errors.Should().ContainSingle()
                 .Which.Should().Contain(ausente);

        await minio.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Um pacote com duas versões do executável não tem resposta certa — publicar "a
    /// primeira que aparecer" escolheria por acaso qual binário toda a base vai rodar.
    /// </summary>
    [Fact]
    public async Task PublicarAsync_com_executavel_duplicado_no_pacote_recusa_em_vez_de_escolher()
    {
        var minio = Substitute.For<IMinioClient>();
        var bytes = "conteudo real"u8.ToArray();
        var pacote = Zip(
            ("extrator.exe", bytes),
            ("antiga/extrator.exe", "outra versao"u8.ToArray()),
            ("manifesto.json", ManifestoBytes("0.14.0", Sha(bytes))));

        var resultado = await ExtratorEndpoints.PublicarAsync(
            pacote, minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>();
        await minio.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task PublicarAsync_com_sha_malformado_no_manifesto_recusa(string sha)
    {
        var minio = Substitute.For<IMinioClient>();

        var resultado = await ExtratorEndpoints.PublicarAsync(
            Pacote("x"u8.ToArray(), "0.14.0", sha),
            minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>()
                 .Subject.Value!.Errors.Should().ContainSingle()
                 .Which.Should().Contain("SHA-256 válido");

        await minio.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublicarAsync_sem_versao_no_manifesto_recusa()
    {
        var minio = Substitute.For<IMinioClient>();
        var bytes = "conteudo real"u8.ToArray();

        var resultado = await ExtratorEndpoints.PublicarAsync(
            Pacote(bytes, "   ", Sha(bytes)),
            minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>()
                 .Subject.Value!.Errors.Should().ContainSingle()
                 .Which.Should().Contain("versão");
    }

    [Fact]
    public async Task PublicarAsync_sem_pacote_recusa()
    {
        var minio = Substitute.For<IMinioClient>();

        var resultado = await ExtratorEndpoints.PublicarAsync(
            null, minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>()
                 .Subject.Value!.Errors.Should().ContainSingle()
                 .Which.Should().Contain("Nenhum pacote enviado");
    }

    /// <summary>
    /// O hash devolvido é o **calculado** aqui, não o texto que veio no manifesto: é o que
    /// garante que o valor gravado ao lado do executável descreve o executável gravado, sem
    /// depender da caixa em que o hash foi escrito.
    /// Qual objeto foi para qual chave — e em que ordem — não é observável daqui:
    /// <c>PutObjectArgs</c> não expõe bucket/objeto fora do assembly do Minio, mesma
    /// limitação anotada em <c>DownloadAsync</c> acima. Isso fica no teste de integração.
    /// </summary>
    [Fact]
    public async Task PublicarAsync_com_pacote_coerente_grava_os_dois_objetos_e_devolve_o_sha_recalculado()
    {
        var minio = Substitute.For<IMinioClient>();
        var bytes = "conteudo real do extrator"u8.ToArray();
        var esperado = Sha(bytes);

        var antes = DateTimeOffset.UtcNow;
        // Maiúsculas no manifesto de propósito: a conferência é por valor, não por texto.
        var resultado = await ExtratorEndpoints.PublicarAsync(
            Pacote(bytes, "0.14.0", esperado.ToUpperInvariant()),
            minio, NullLogger<Program>.Instance, CancellationToken.None);

        var ok = resultado.Should().BeOfType<Ok<ExtratorVersaoResponse>>().Subject;
        ok.Value!.Versao.Should().Be("0.14.0");
        ok.Value.Sha256.Should().Be(esperado, "o hash publicado é o do arquivo recebido, em minúsculas");
        ok.Value.PublicadoEm.Should().BeOnOrAfter(antes,
            "PublicadoEm é o instante da publicação, não a hora do build que veio no manifesto");

        await minio.Received(2).PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// O artefato do Actions traz os dois arquivos na raiz do ZIP, mas um pacote feito à mão
    /// a partir da pasta de publicação vem com prefixo de diretório. Recusar por causa disso
    /// mandaria o operador refazer o ZIP para nada.
    /// </summary>
    [Fact]
    public async Task PublicarAsync_aceita_os_arquivos_dentro_de_subpasta_no_zip()
    {
        var minio = Substitute.For<IMinioClient>();
        var bytes = "conteudo real"u8.ToArray();

        var resultado = await ExtratorEndpoints.PublicarAsync(
            Zip(("publish/extrator.exe", bytes),
                ("publish/manifesto.json", ManifestoBytes("0.14.0", Sha(bytes)))),
            minio, NullLogger<Program>.Instance, CancellationToken.None);

        resultado.Should().BeOfType<Ok<ExtratorVersaoResponse>>();
    }

    private static string Sha(byte[] conteudo) => Convert.ToHexStringLower(SHA256.HashData(conteudo));

    private static IFormFile Arquivo(string nome, byte[] conteudo) =>
        new FormFile(new MemoryStream(conteudo), 0, conteudo.Length, nome, nome);

    private static byte[] ManifestoBytes(string versao, string sha) => Encoding.UTF8.GetBytes(
        $"{{\"versao\":\"{versao}\",\"sha256\":\"{sha}\",\"publicadoEm\":\"2026-01-01T00:00:00Z\"}}");

    private static IFormFile Pacote(byte[] executavel, string versao, string sha) =>
        Zip(("extrator.exe", executavel), ("manifesto.json", ManifestoBytes(versao, sha)));

    private static IFormFile Zip(params (string Nome, byte[] Conteudo)[] entradas)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (nome, conteudo) in entradas)
            {
                using var destino = zip.CreateEntry(nome).Open();
                destino.Write(conteudo);
            }
        }

        buffer.Position = 0;
        return new FormFile(buffer, 0, buffer.Length, "pacote", "extrator.zip");
    }
}
