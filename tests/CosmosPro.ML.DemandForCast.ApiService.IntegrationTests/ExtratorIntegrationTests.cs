using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Bucket <c>extrator</c> é singleton (uma versão publicada por vez, para toda rede — ver
/// <c>ExtratorEndpoints</c> na apiservice), diferente do bucket <c>imports</c> onde cada
/// teste usa uma <c>CargaStage</c> com Id próprio. Sem chave por teste para isolar, cada
/// teste limpa o que criou — e o "não publicado" também limpa antes de rodar, porque o
/// container MinIO é persistente entre execuções (CLAUDE.md §4) e pode sobrar lixo de um
/// run anterior. Chaves e nome do bucket duplicados aqui (não há ProjectReference para a
/// apiservice neste projeto de teste, só HTTP) — precisam ficar em sincronia manual com
/// <c>ExtratorEndpoints.BucketName/ExecutavelKey/ManifestoKey</c>.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class ExtratorIntegrationTests(AppHostFixture fixture)
{
    private const string Bucket = "extrator";
    private const string ExecutavelKey = "extrator.exe";
    private const string ManifestoKey = "manifesto.json";

    [Fact]
    public async Task Versao_e_download_sem_publicacao_retornam_404_claro()
    {
        var minio = await fixture.GetMinioClientAsync();
        await LimparAsync(minio);

        var respVersao = await fixture.ExtratorApi.GetVersaoAsync();
        respVersao.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var respDownload = await fixture.ExtratorApi.DownloadAsync();
        respDownload.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Versao_e_download_com_publicacao_retornam_dados_e_bytes_corretos()
    {
        var minio = await fixture.GetMinioClientAsync();
        var conteudoFake = "conteudo-fake-do-executavel-para-teste-de-integracao"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(conteudoFake));
        const string versaoTeste = "9.9.9-teste-integracao";

        await PublicarAsync(minio, conteudoFake, versaoTeste, sha256);
        try
        {
            var respVersao = await fixture.ExtratorApi.GetVersaoAsync();
            respVersao.StatusCode.Should().Be(HttpStatusCode.OK);
            respVersao.Content.Should().NotBeNull();
            respVersao.Content!.Versao.Should().Be(versaoTeste);
            respVersao.Content.Sha256.Should().Be(sha256);

            var respDownload = await fixture.ExtratorApi.DownloadAsync();
            respDownload.StatusCode.Should().Be(HttpStatusCode.OK);
            var bytesBaixados = await respDownload.Content.ReadAsByteArrayAsync();
            bytesBaixados.Should().Equal(conteudoFake,
                "o download precisa devolver exatamente o que foi publicado, sem truncar nem alterar o stream");
        }
        finally
        {
            await LimparAsync(minio);
        }
    }

    private static async Task PublicarAsync(IMinioClient minio, byte[] conteudo, string versao, string sha256)
    {
        var existeBucket = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket));
        if (!existeBucket)
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket));
        }

        using (var ms = new MemoryStream(conteudo))
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket)
                .WithObject(ExecutavelKey)
                .WithStreamData(ms)
                .WithObjectSize(conteudo.Length)
                .WithContentType("application/octet-stream"));
        }

        var manifesto = $$"""
            {
              "Versao": "{{versao}}",
              "Sha256": "{{sha256}}",
              "PublicadoEm": "2026-01-01T00:00:00Z"
            }
            """;
        var manifestoBytes = Encoding.UTF8.GetBytes(manifesto);
        using (var ms = new MemoryStream(manifestoBytes))
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket)
                .WithObject(ManifestoKey)
                .WithStreamData(ms)
                .WithObjectSize(manifestoBytes.Length)
                .WithContentType("application/json"));
        }
    }

    private static async Task LimparAsync(IMinioClient minio)
    {
        foreach (var key in new[] { ExecutavelKey, ManifestoKey })
        {
            try
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(Bucket)
                    .WithObject(key));
            }
            catch (Exception ex) when (ex is BucketNotFoundException or ObjectNotFoundException)
            {
                // Já não existia — objetivo da limpeza já está satisfeito.
            }
        }
    }
}
