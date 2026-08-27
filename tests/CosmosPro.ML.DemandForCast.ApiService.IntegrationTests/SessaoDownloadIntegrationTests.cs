using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using CosmosPro.ML.DemandForCast.Tests.Shared.Csv;
using CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Download do ZIP que uma sessão recebeu — o que permite repetir uma comparação sobre
/// exatamente o mesmo envio.
///
/// <para>
/// <b>A afirmação central é identidade byte a byte</b>, e não "baixa alguma coisa". Cada
/// import substitui o Stage inteiro da rede, então o arquivo original é a única entrada
/// que torna uma execução reprodutível: um ZIP que voltasse recomprimido, com CSV
/// reserializado ou com uma linha a mais valeria como cópia e não como o mesmo envio, e
/// duas execuções sobre entradas diferentes não se comparam. Por isso a asserção é sobre o
/// SHA-256, não sobre tamanho nem sobre "abre como ZIP".
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class SessaoDownloadIntegrationTests(AppHostFixture fixture)
{
    private const int LojaId = 9801;
    private const string Sku = "DL-A";
    private const long SugestaoId = 7201;
    private const byte TipoCalculo = 2;

    private static readonly DateOnly Inicio = new(2026, 1, 1);
    private static readonly DateOnly Fim = new(2026, 8, 31);
    private static readonly DateTime SugestaoDataHora = new(2026, 7, 1, 9, 30, 0);

    [Fact]
    public async Task O_zip_baixado_e_identico_ao_enviado()
    {
        var redeId = await EnsureRedeAsync("Rede Download Igual", "download-igual");
        var enviado = BuildZip().ToArray();
        var sessaoId = await CriarEEnviarAsync(redeId, enviado, "extracao-pbs_20260701-0930.zip");

        using var resp = await fixture.ComparacoesApi.DownloadDadosAsync(
            sessaoId, redeId, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");

        var baixado = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Convert.ToHexString(SHA256.HashData(baixado))
            .Should().Be(Convert.ToHexString(SHA256.HashData(enviado)),
                "repetir uma comparação exige a MESMA entrada; um ZIP recomprimido não é o mesmo envio");

        // O nome volta de CargaStage.NomeArquivoOriginal: quem baixa precisa reconhecer qual
        // extração está na mão, e "download.zip" tornaria dois envios indistinguíveis no disco.
        var nome = resp.Content.Headers.ContentDisposition?.FileNameStar
                   ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        nome.Should().Be("extracao-pbs_20260701-0930.zip");
    }

    /// <summary>
    /// Isolamento entre inquilinos, com o código de status que faz parte da regra: <b>404 e
    /// não 403</b>. Um 403 confirmaria a quem sondasse ids que a sessão existe em outra rede
    /// — e aqui o que está atrás dela é o dado comercial bruto do outro cliente, não um
    /// agregado.
    /// </summary>
    [Fact]
    public async Task Sessao_de_outra_rede_responde_404_e_nao_403()
    {
        var dona = await EnsureRedeAsync("Rede Download Dona", "download-dona");
        var vizinha = await EnsureRedeAsync("Rede Download Vizinha", "download-vizinha");
        var sessaoId = await CriarEEnviarAsync(dona, BuildZip().ToArray(), "envio-da-dona.zip");

        using var resp = await fixture.ComparacoesApi.DownloadDadosAsync(
            sessaoId, vizinha, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Sessão criada e ainda sem envio: 404, o mesmo código dos outros dois casos. E
    /// <c>DadosEnviados</c> falso é o que a tela usa para não oferecer um botão que só
    /// poderia falhar.
    /// </summary>
    [Fact]
    public async Task Sessao_sem_envio_responde_404_e_nao_anuncia_arquivo()
    {
        var redeId = await EnsureRedeAsync("Rede Download Vazia", "download-vazia");

        var criada = await fixture.ComparacoesApi.CreateAsync(
            new CreateSessaoRequest("Sem envio"), redeId, TestContext.Current.CancellationToken);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        criada.Content!.DadosEnviados.Should().BeFalse();

        using var resp = await fixture.ComparacoesApi.DownloadDadosAsync(
            criada.Content.Id, redeId, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> CriarEEnviarAsync(int redeId, byte[] zip, string nomeArquivo)
    {
        var criada = await fixture.ComparacoesApi.CreateAsync(
            new CreateSessaoRequest("Download do envio"), redeId, TestContext.Current.CancellationToken);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);

        using var stream = new MemoryStream(zip);
        var envio = await fixture.ComparacoesApi.UploadDadosAsync(
            criada.Content!.Id, new StreamPart(stream, nomeArquivo, "application/zip"), redeId,
            TestContext.Current.CancellationToken);
        envio.StatusCode.Should().Be(HttpStatusCode.Accepted, because: envio.Error?.Message ?? "sem detalhe");

        var depois = await fixture.ComparacoesApi.GetAsync(
            criada.Content.Id, redeId, TestContext.Current.CancellationToken);
        depois.Content!.DadosEnviados.Should().BeTrue("o envio é o que habilita o download na tela");

        return criada.Content.Id;
    }

    private async Task<int> EnsureRedeAsync(string nome, string slug)
    {
        var criacao = await fixture.RedesApi.CreateAsync(new CreateRedeRequest(nome, slug));
        if (criacao.IsSuccessStatusCode)
        {
            return criacao.Content!.Id;
        }

        criacao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "só conflito de slug é aceitável aqui; outro status é falha real");

        var lista = await fixture.RedesApi.ListAsync();
        lista.IsSuccessStatusCode.Should().BeTrue();
        var existente = lista.Content!.SingleOrDefault(r => r.Slug == slug);
        existente.Should().NotBeNull($"rede '{slug}' deu 409 mas não apareceu na listagem");
        return existente!.Id;
    }

    /// <summary>
    /// Dataset mínimo que o validador do upload aceita, com a sugestão do PBS e o manifesto —
    /// mesma forma de <c>SessaoManifestoIntegrationTests</c>. O conteúdo é indiferente aqui:
    /// o que se afirma é que os bytes voltam iguais, não o que eles significam.
    /// </summary>
    private static MemoryStream BuildZip()
    {
        var lojas = new List<LojaRow>
        {
            new(LojaId, "Loja Download", "SP", "São Paulo", "Sudeste", "rua", 7, new DateOnly(2020, 1, 1), true),
        };

        var produtos = new List<ProdutoRow>
        {
            new(Sku, "Produto Download", "Similar", "Analgésico", "ACME", "Dipirona Sódica", "20cp 500mg", null, null, null, null, true),
        };

        var vendas = new List<VendaRow>();
        for (var d = Inicio; d <= Fim; d = d.AddDays(1))
        {
            var qtd = 5m + d.Day % 7;
            vendas.Add(new VendaRow(d, LojaId, Sku, qtd, 10.50m, qtd * 10.50m));
        }

        return new CsvZipBuilder()
            .WithLojas(lojas)
            .WithProdutos(produtos)
            .WithVendas(vendas)
            .WithEstoquesDiarios([new(new DateOnly(2026, 2, 10), LojaId, Sku, 500m)])
            .WithCompras(new CompraFaker([LojaId], [Sku], Inicio, Fim, seed: 940).Generate(2))
            .WithPromocoes(new PromocaoFaker([LojaId], [Sku], Inicio, Fim, seed: 941).Generate(1))
            .ReplaceRaw("sugestoes_compra.csv", SugestaoCsv())
            .ReplaceRaw("sugestoes_compra_itens.csv", SugestaoItensCsv())
            .ReplaceRaw("manifesto.json", ManifestoJson())
            .Build();
    }

    private static string SugestaoCsv() =>
        "SugestaoId,Descricao,DataHora,TipoCalculo,LeadTimeDias,DiasCurvaA,DiasCurvaB,DiasCurvaC,DiasCurvaD,DiasCurvaE,Efetividade,ConsideraPedidosPendentes,IncluiEstoqueZerado\n" +
        $"{SugestaoId},Sugestao do download,{SugestaoDataHora:yyyy-MM-dd}T09:30:00,{TipoCalculo},7,15,15,15,15,15,100.00,1,0\n";

    private static string SugestaoItensCsv() =>
        "SugestaoId,LojaId,Sku,Curva,DemandaDia,DemandaDiaPonderada,EstoqueSaldo,EstoqueSeguranca," +
        "EstoqueMaximo,EstoqueMinimo,DiasEstoque,PedidosPendentes,CompraSugerida,CompraAutorizada," +
        "PrecoCompra,FatorEmbalagem,Falteiro\n" +
        $"{SugestaoId},{LojaId},{Sku},A,6.0000,,10.000,,,,7,0.000,32.000,32.000,3.5000,,0\n";

    private static string ManifestoJson() => string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "SugestaoId": {{SugestaoId}},
          "SugestaoDescricao": "Sugestao do download",
          "SugestaoDataHora": "{{SugestaoDataHora:yyyy-MM-dd}}T{{SugestaoDataHora:HH:mm:ss}}",
          "SugestaoTipoCalculo": {{TipoCalculo}},
          "JanelaInicio": "{{Inicio:yyyy-MM-dd}}",
          "JanelaFim": "{{Fim:yyyy-MM-dd}}",
          "VersaoExtractor": "0.17.0.0",
          "SkusSemCadastro": 0
        }
        """);
}
