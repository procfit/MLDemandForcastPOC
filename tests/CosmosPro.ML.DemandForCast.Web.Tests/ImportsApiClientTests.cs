using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using CosmosPro.ML.DemandForCast.Web;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

public sealed class ImportsApiClientTests
{
    private const int RedeDeTeste = 7;

    /// <summary>
    /// Escopo de rede fixo. O que importa nestes testes é que o client <b>envie</b>
    /// o redeId na query — a resolução real (claim, papel) é coberta em RedeContextTests.
    /// </summary>
    private sealed class RedeContextFixo(int redeId) : IRedeContext
    {
        public Task<int> GetRedeIdAtualAsync() => Task.FromResult(redeId);
        public Task<Guid> GetUsuarioIdAtualAsync() => Task.FromResult(Guid.Empty);
        public Task<bool> EhPowerUserAsync() => Task.FromResult(false);
        public Task<bool> PodeAcessarAsync(int id) => Task.FromResult(id == redeId);
        public Task SelecionarRedeAsync(int id) => Task.CompletedTask;
    }

    private static ImportsApiClient ClientReturning(HttpStatusCode status, string jsonBody)
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            return resp;
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        return new ImportsApiClient(http, new RedeContextFixo(RedeDeTeste));
    }

    [Fact]
    public async Task UploadAsync_em_resposta_202_retorna_Success_true_com_body()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"id\":\"{id}\",\"status\":\"Pendente\",\"dataAgendamento\":\"2026-05-20T15:00:00+00:00\"}}";
        var client = ClientReturning(HttpStatusCode.Accepted, json);

        await using var stream = new MemoryStream([1, 2, 3, 4]);
        var result = await client.UploadAsync(stream, "x.zip", 4);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeNull();
        result.Body.Should().NotBeNull();
        result.Body!.Id.Should().Be(id);
        result.Body.Status.Should().Be("Pendente");
    }

    [Fact]
    public async Task UploadAsync_em_resposta_400_retorna_Success_false_com_errors()
    {
        var json = "{\"errors\":[\"arquivo invalido\",\"falta vendas.csv\"]}";
        var client = ClientReturning(HttpStatusCode.BadRequest, json);

        await using var stream = new MemoryStream([0]);
        var result = await client.UploadAsync(stream, "x.zip", 1);

        result.Success.Should().BeFalse();
        result.Body.Should().BeNull();
        result.Errors.Should().BeEquivalentTo(new[] { "arquivo invalido", "falta vendas.csv" });
    }

    [Fact]
    public async Task ListAsync_chama_endpoint_correto_e_desserializa()
    {
        var id = Guid.NewGuid();
        var json = $"[{{\"id\":\"{id}\",\"status\":\"Concluida\",\"dataAgendamento\":\"2026-05-20T15:00:00+00:00\",\"dataInicioProcessamento\":null,\"dataConclusao\":null,\"nomeArquivoOriginal\":\"x.zip\",\"blobKey\":\"x.zip\",\"mensagemErro\":null,\"linhasImportadas\":18}}]";
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        var client = new ImportsApiClient(http, new RedeContextFixo(RedeDeTeste));

        var result = await client.ListAsync(take: 25);

        captured.Should().NotBeNull();
        captured!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/imports?take=25&redeId={RedeDeTeste}",
            "o client tem de enviar o escopo de rede resolvido pelo IRedeContext");
        result.Should().ContainSingle();
        result[0].LinhasImportadas.Should().Be(18);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
