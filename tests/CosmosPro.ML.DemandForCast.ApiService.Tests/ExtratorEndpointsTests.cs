using CosmosPro.ML.DemandForCast.ApiService.Extrator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
}
