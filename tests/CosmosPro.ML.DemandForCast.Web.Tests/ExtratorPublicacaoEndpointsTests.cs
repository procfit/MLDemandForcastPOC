using System.Net;
using System.Net.Http;
using System.Text;
using CosmosPro.ML.DemandForCast.Web;
using CosmosPro.ML.DemandForCast.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// Cobre a rota que o CI usa para publicar o extrator sem passar pela UI. As duas coisas
/// que precisam estar certas aqui não são a mesma: o token (é o que impede alguém que ache
/// a URL de trocar o executável que os clientes baixam) e o **status** das respostas (é por
/// ele que o job distingue "nada publicado" de "a Web no ar é anterior a este commit").
/// </summary>
public sealed class ExtratorPublicacaoEndpointsTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static ExtratorApiClient Cliente(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHttpMessageHandler(responder)) { BaseAddress = new Uri("http://api.test/") });

    private const string Token = "s3cr3t-de-publicacao-do-extrator";

    /// <summary>
    /// O furo que este teste fecha: token não configurado no servidor comparado com token
    /// não enviado dá "igual" em qualquer comparação ingênua, e a rota que substitui o
    /// executável ficaria aberta a quem descobrisse a URL. Vale para os três jeitos de
    /// "vazio" que a configuração produz — chave ausente, string vazia e só espaços.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TokenConfere_sem_token_configurado_recusa_qualquer_pedido(string? esperado)
    {
        ExtratorEndpoints.TokenConfere(esperado, $"Bearer {Token}").Should().BeFalse();
        ExtratorEndpoints.TokenConfere(esperado, "Bearer ").Should().BeFalse();
        ExtratorEndpoints.TokenConfere(esperado, null).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic s3cr3t-de-publicacao-do-extrator")]
    [InlineData("s3cr3t-de-publicacao-do-extrator")]
    [InlineData("Bearer s3cr3t-de-publicacao-do-extratorX")]
    [InlineData("Bearer s3cr3t-de-publicacao-do-extrato")]
    [InlineData("Bearer S3CR3T-DE-PUBLICACAO-DO-EXTRATOR")]
    public void TokenConfere_recusa_cabecalho_que_nao_e_exatamente_o_token(string? cabecalho)
    {
        ExtratorEndpoints.TokenConfere(Token, cabecalho).Should().BeFalse();
    }

    [Theory]
    [InlineData("Bearer s3cr3t-de-publicacao-do-extrator")]
    [InlineData("bearer s3cr3t-de-publicacao-do-extrator")]
    public void TokenConfere_aceita_o_token_com_esquema_em_qualquer_caixa(string cabecalho)
    {
        ExtratorEndpoints.TokenConfere(Token, cabecalho).Should().BeTrue(
            "o esquema é insensível a caixa por especificação de HTTP; o token não é");
    }

    /// <summary>
    /// 200 com nulos, não 404. O job usa o status para saber onde está: 404 significa que a
    /// Web no ar não tem esta rota (build anterior a este commit), e se "nada publicado"
    /// também respondesse 404 as duas situações ficariam indistinguíveis.
    /// </summary>
    [Fact]
    public async Task VersaoPublicadaAsync_sem_nada_publicado_responde_200_com_campos_nulos()
    {
        var api = Cliente(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var resultado = await ExtratorEndpoints.VersaoPublicadaAsync(api, CancellationToken.None);

        var ok = resultado.Should().BeOfType<Ok<PublicacaoExtratorView>>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value!.Versao.Should().BeNull();
        ok.Value.Sha256.Should().BeNull();
        ok.Value.PublicadoEm.Should().BeNull();
    }

    [Fact]
    public async Task VersaoPublicadaAsync_com_versao_no_ar_devolve_versao_e_checksum()
    {
        var api = Cliente(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"versao":"0.18.1","sha256":"abc","publicadoEm":"2026-09-09T12:00:00+00:00"}""",
                Encoding.UTF8, "application/json"),
        });

        var resultado = await ExtratorEndpoints.VersaoPublicadaAsync(api, CancellationToken.None);

        var ok = resultado.Should().BeOfType<Ok<PublicacaoExtratorView>>().Subject;
        ok.Value!.Versao.Should().Be("0.18.1");
        ok.Value.Sha256.Should().Be("abc");
    }

    [Fact]
    public async Task PublicarAsync_repassa_o_corpo_do_pedido_para_a_apiservice()
    {
        var pacote = Encoding.UTF8.GetBytes("PK-conteudo-do-zip");
        string? enviado = null;

        var api = Cliente(req =>
        {
            enviado = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"versao":"0.18.2","sha256":"def","publicadoEm":"2026-09-09T12:00:00+00:00"}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        var resultado = await ExtratorEndpoints.PublicarAsync(
            PedidoCom(pacote), api, NullLoggerFactory.Instance, CancellationToken.None);

        enviado.Should().Contain("-conteudo-do-zip", "o corpo cru do pedido é o pacote");
        var ok = resultado.Should().BeOfType<Ok<PublicacaoExtratorView>>().Subject;
        ok.Value!.Versao.Should().Be("0.18.2");
        ok.Value.Sha256.Should().Be("def");
    }

    /// <summary>
    /// A recusa da apiservice (pacote sem manifesto, SHA que não casa) tem de chegar ao log
    /// do job com o texto dela. Um 500 genérico ou um corpo vazio deixaria quem lê o Actions
    /// sem a única frase que diz o que estava errado no pacote.
    /// </summary>
    [Fact]
    public async Task PublicarAsync_repassa_a_recusa_da_apiservice_com_os_erros_dela()
    {
        var api = Cliente(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"errors":["O pacote não contém um manifesto.json."]}""",
                Encoding.UTF8, "application/json"),
        });

        var resultado = await ExtratorEndpoints.PublicarAsync(
            PedidoCom([1, 2, 3]), api, NullLoggerFactory.Instance, CancellationToken.None);

        var recusa = resultado.Should().BeOfType<BadRequest<ValidationErrorResponse>>().Subject;
        recusa.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        recusa.Value!.Errors.Should().ContainSingle()
              .Which.Should().Contain("manifesto.json");
    }

    private static HttpRequest PedidoCom(byte[] corpo)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Body = new MemoryStream(corpo);
        contexto.Request.ContentLength = corpo.Length;
        contexto.Request.ContentType = "application/zip";
        return contexto.Request;
    }
}
