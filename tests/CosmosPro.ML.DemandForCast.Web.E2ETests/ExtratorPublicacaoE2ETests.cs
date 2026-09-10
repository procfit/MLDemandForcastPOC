using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Testing;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// Prova o único trecho da publicação automática do extrator que nenhum teste unitário
/// alcança: o fio entre o parâmetro Aspire <c>extrator-publish-token</c>, a env var
/// <c>Extrator__PublishToken</c> e a chave de configuração que o filtro lê. O nome da env
/// var não aparece em lugar nenhum do código da Web — quem o forma é o Aspire a partir do
/// <c>WithEnvironment</c> do AppHost —, então um erro de digitação ali compila, passa em
/// toda a suíte unitária e só apareceria como 503 no job do CI.
///
/// Também é o único teste que exercita o endpoint com o pipeline real da Web, e por isso o
/// que pega <c>UseAntiforgery</c> rejeitando o POST: um pedido de máquina não tem token de
/// antiforgery, e sem <c>DisableAntiforgery</c> no grupo a publicação seria recusada antes
/// de chegar ao handler.
///
/// Nada é publicado de propósito. O bucket <c>extrator</c> é singleton e o MinIO é
/// persistente entre execuções (CLAUDE.md §4), então um teste que publicasse deixaria um
/// executável falso de 20 bytes como versão vigente do ambiente de desenvolvimento. Para
/// provar que o pacote atravessa os dois saltos basta um pacote que a apiservice RECUSE:
/// a mensagem que volta é escrita por ela, do outro lado do salto.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class ExtratorPublicacaoE2ETests(AppHostFixture fixture)
{
    private const string Rota = "/extrator/publicacao";

    private HttpClient Cliente() => fixture.App.CreateHttpClient("webfrontend", endpointName: "https");

    private static void ComToken(HttpRequestMessage pedido, string token) =>
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task Get_sem_token_e_recusado()
    {
        using var http = Cliente();
        using var pedido = new HttpRequestMessage(HttpMethod.Get, Rota);

        using var resposta = await http.SendAsync(pedido, TestContext.Current.CancellationToken);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "sem token não há como distinguir o CI de quem achou a URL");
    }

    [Fact]
    public async Task Get_com_token_errado_e_recusado()
    {
        using var http = Cliente();
        using var pedido = new HttpRequestMessage(HttpMethod.Get, Rota);
        ComToken(pedido, AppHostFixture.ExtratorPublishToken + "-errado");

        using var resposta = await http.SendAsync(pedido, TestContext.Current.CancellationToken);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 200 aqui é a asserção sobre o fio: se a env var não chegasse à Web, o filtro
    /// responderia 503 ("publicação automática não configurada") em vez de aceitar o token.
    /// E o corpo tem de ser JSON com os três campos mesmo quando nada foi publicado — é
    /// dele que o job tira a versão que está no ar para decidir se publica.
    /// </summary>
    [Fact]
    public async Task Get_com_o_token_configurado_responde_200_com_o_estado_da_publicacao()
    {
        using var http = Cliente();
        using var pedido = new HttpRequestMessage(HttpMethod.Get, Rota);
        ComToken(pedido, AppHostFixture.ExtratorPublishToken);

        using var resposta = await http.SendAsync(pedido, TestContext.Current.CancellationToken);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK,
            "503 aqui significaria que Extrator__PublishToken não chegou à Web");

        var corpo = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(corpo);

        json.RootElement.TryGetProperty("versao", out _).Should().BeTrue(
            "o job lê `.versao` deste corpo — o campo tem de existir mesmo nulo");
        json.RootElement.TryGetProperty("sha256", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("publicadoEm", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Post_sem_token_e_recusado()
    {
        using var http = Cliente();
        using var pedido = new HttpRequestMessage(HttpMethod.Post, Rota)
        {
            Content = new ByteArrayContent("nao-e-um-zip"u8.ToArray()),
        };

        using var resposta = await http.SendAsync(pedido, TestContext.Current.CancellationToken);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "esta é a rota que troca o executável baixado pelos clientes");
    }

    /// <summary>
    /// O pacote é inválido de propósito (ver o comentário da classe). O que se afirma é a
    /// origem da recusa: "não é um .zip válido" é frase da apiservice, então recebê-la prova
    /// que o corpo cru atravessou a Web, virou multipart e foi lido do outro lado — e que o
    /// antiforgery não interceptou antes (a recusa dele tem outro corpo).
    /// </summary>
    [Fact]
    public async Task Post_com_token_atravessa_ate_a_apiservice_e_devolve_a_recusa_dela()
    {
        using var http = Cliente();
        using var pedido = new HttpRequestMessage(HttpMethod.Post, Rota)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("isto-nao-e-um-zip-de-verdade")),
        };
        ComToken(pedido, AppHostFixture.ExtratorPublishToken);

        using var resposta = await http.SendAsync(pedido, TestContext.Current.CancellationToken);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var corpo = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        corpo.Should().Contain(".zip",
            $"a recusa tem de ser a da apiservice, não a do antiforgery. Corpo real: <<<{corpo}>>>");
    }
}
