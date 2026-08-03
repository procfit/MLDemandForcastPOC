using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Refit;

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

    /// <summary>
    /// O caminho que o operador usa em produção: publicar pelo endpoint, não arrastando
    /// arquivos no console do MinIO. Cobre o que o teste unitário não alcança — que o bucket
    /// é criado quando não existe, e que os dois objetos foram para as chaves que
    /// <c>versao</c> e <c>download</c> leem (<c>PutObjectArgs</c> não expõe bucket/objeto
    /// fora do assembly do Minio, então lá isso é invisível).
    /// </summary>
    [Fact]
    public async Task Publicar_pelo_endpoint_cria_o_bucket_e_torna_a_versao_baixavel()
    {
        var minio = await fixture.GetMinioClientAsync();
        await RemoverBucketAsync(minio);

        var conteudo = "executavel-publicado-pelo-endpoint"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(conteudo));

        try
        {
            var publicacao = await fixture.ExtratorApi.PublicarAsync(Pacote(conteudo, "1.2.3", sha));

            publicacao.StatusCode.Should().Be(HttpStatusCode.OK);
            publicacao.Content!.Versao.Should().Be("1.2.3");
            publicacao.Content.Sha256.Should().Be(sha);

            var versao = await fixture.ExtratorApi.GetVersaoAsync();
            versao.StatusCode.Should().Be(HttpStatusCode.OK);
            versao.Content!.Versao.Should().Be("1.2.3");
            versao.Content.Sha256.Should().Be(sha);

            var download = await fixture.ExtratorApi.DownloadAsync();
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(conteudo);
        }
        finally
        {
            await LimparAsync(minio);
        }
    }

    /// <summary>
    /// Uma publicação recusada não pode derrubar a que está no ar: o comprador que estiver
    /// baixando no momento em que o operador erra o arquivo não tem por que ver a página
    /// dizer "não publicado".
    /// </summary>
    [Fact]
    public async Task Publicar_com_par_incoerente_e_recusado_e_preserva_a_versao_anterior()
    {
        var minio = await fixture.GetMinioClientAsync();
        var conteudoBom = "versao-que-estava-no-ar"u8.ToArray();
        var shaBom = Convert.ToHexStringLower(SHA256.HashData(conteudoBom));

        await PublicarAsync(minio, conteudoBom, "1.0.0", shaBom);
        try
        {
            // O manifesto descreve o executável que já estava no ar, e o .exe dentro do
            // pacote é outro: é o pacote corrompido/remontado que a conferência existe para
            // pegar.
            var recusa = await fixture.ExtratorApi.PublicarAsync(
                Pacote("outro-executavel-qualquer"u8.ToArray(), "2.0.0", shaBom));

            recusa.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var versao = await fixture.ExtratorApi.GetVersaoAsync();
            versao.Content!.Versao.Should().Be("1.0.0");

            var download = await fixture.ExtratorApi.DownloadAsync();
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(conteudoBom,
                "o executável no bucket precisa continuar sendo o que o manifesto vigente descreve");
        }
        finally
        {
            await LimparAsync(minio);
        }
    }

    /// <summary>
    /// Monta o mesmo ZIP que o artefato <c>extrator</c> do CI entrega: os dois arquivos na
    /// raiz, com os nomes que a apiservice procura.
    /// </summary>
    private static StreamPart Pacote(byte[] executavel, string versao, string sha)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var destino = zip.CreateEntry(ExecutavelKey).Open())
            {
                destino.Write(executavel);
            }

            using (var destino = zip.CreateEntry(ManifestoKey).Open())
            {
                destino.Write(ManifestoBytes(versao, sha));
            }
        }

        buffer.Position = 0;
        return new StreamPart(buffer, "extrator.zip", "application/zip");
    }

    private static byte[] ManifestoBytes(string versao, string sha) => Encoding.UTF8.GetBytes(
        $"{{\"versao\":\"{versao}\",\"sha256\":\"{sha}\",\"publicadoEm\":\"2026-01-01T00:00:00Z\"}}");

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

    /// <summary>
    /// Apaga o bucket inteiro, e não só os objetos: é o estado de um ambiente recém-criado
    /// — que é exatamente o que aconteceu no primeiro deploy e o que o teste de publicação
    /// precisa reproduzir para provar que o endpoint cria o bucket em vez de estourar.
    /// </summary>
    private static async Task RemoverBucketAsync(IMinioClient minio)
    {
        await LimparAsync(minio);
        try
        {
            await minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(Bucket));
        }
        catch (BucketNotFoundException)
        {
        }
    }
}
