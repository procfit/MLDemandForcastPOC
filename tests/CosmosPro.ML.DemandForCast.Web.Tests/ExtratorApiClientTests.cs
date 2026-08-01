using System.Net;
using System.Net.Http;
using System.Text;
using CosmosPro.ML.DemandForCast.Web;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// Diferente de <c>ImportsApiClientTests</c>: nenhuma chamada aqui carrega <c>redeId</c>
/// nem depende de <c>IRedeContext</c> — o extrator não é dado de inquilino.
/// </summary>
public sealed class ExtratorApiClientTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    [Fact]
    public async Task GetVersaoAsync_em_200_desserializa_versao_e_checksum()
    {
        var json = "{\"versao\":\"0.14.0\",\"sha256\":\"abc123\",\"publicadoEm\":\"2026-07-30T12:00:00+00:00\"}";
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
        var client = new ExtratorApiClient(http);

        var result = await client.GetVersaoAsync();

        captured.Should().NotBeNull();
        captured!.RequestUri!.PathAndQuery.Should().Be("/api/extrator/versao",
            "a chamada não deve carregar redeId — o extrator não é dado de inquilino");
        result.Should().NotBeNull();
        result!.Versao.Should().Be("0.14.0");
        result.Sha256.Should().Be("abc123");
    }

    /// <summary>
    /// 404 é o estado normal de um ambiente sem publicação ainda — o client devolve
    /// <c>null</c> em vez de lançar, mesmo padrão de <c>ImportsApiClient.GetAsync</c>.
    /// </summary>
    [Fact]
    public async Task GetVersaoAsync_em_404_retorna_null()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"title\":\"Extrator não publicado\"}", Encoding.UTF8, "application/problem+json"),
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        var client = new ExtratorApiClient(http);

        var result = await client.GetVersaoAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetVersaoAsync_em_500_lanca_em_vez_de_devolver_null()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        var client = new ExtratorApiClient(http);

        var act = () => client.GetVersaoAsync();

        // Diferente do 404 (estado normal), uma falha de infraestrutura não pode virar
        // silenciosamente "não publicado" — precisa continuar visível como erro.
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task AbrirDownloadAsync_chama_o_endpoint_correto_e_devolve_a_resposta_crua()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream([1, 2, 3])),
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        var client = new ExtratorApiClient(http);

        using var resp = await client.AbrirDownloadAsync();

        captured.Should().NotBeNull();
        captured!.RequestUri!.PathAndQuery.Should().Be("/api/extrator/download");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
