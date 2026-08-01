using System.Net;
using System.Text;

using CosmosPro.ML.DemandForCast.Web;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// A sessão guiada de F14: escopo de rede das leituras novas e, sobretudo, a leitura dos
/// agregados da manchete.
///
/// <para>
/// O JSON destes casos é escrito à mão de propósito: o que está sob prova aqui é a
/// <b>decisão</b> que a tela toma a partir do payload (exibir número ou exibir explicação),
/// e um caso rápido por decisão vale mais do que um único caso montado com os tipos do
/// Worker. O contrato real — os nomes dos campos que o Worker grava — é atravessado de ponta
/// a ponta pelo E2E da tela, que serializa <c>SessaoResultado</c> de verdade.
/// </para>
/// </summary>
public sealed class ComparacoesApiClientTests
{
    private const int RedeDeTeste = 11;

    private static readonly Guid SessaoId = new("0199f14a-0000-7000-8000-0000000000aa");

    private sealed class RedeContextFixo(int redeId) : IRedeContext
    {
        public Task<int> GetRedeIdAtualAsync() => Task.FromResult(redeId);
        public Task<Guid> GetUsuarioIdAtualAsync() => Task.FromResult(Guid.Empty);
        public Task<bool> EhPowerUserAsync() => Task.FromResult(false);
        public Task<bool> PodeAcessarAsync(int id) => Task.FromResult(id == redeId);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static (ComparacoesApiClient Client, Func<HttpRequestMessage?> Captured) ClientReturning(
        HttpStatusCode status, string jsonBody)
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        return (new ComparacoesApiClient(http, new RedeContextFixo(RedeDeTeste)), () => captured);
    }

    // --- O estado real de hoje: manchete sem coluna de ML ---------------------

    /// <summary>
    /// <b>O caso mais importante desta tela.</b> A cobertura corrente do ERP é de 15 a 30
    /// dias e o pipeline prevê 7, então hoje nenhuma sessão real tem coluna de ML. A tela
    /// precisa saber disso pelo payload e ter uma frase para pôr no lugar dos números — traço,
    /// zero ou célula vazia leriam como "o ML concordou" ou "o ML mandaria não comprar nada".
    /// </summary>
    [Fact]
    public void Resultado_sem_confronto_nao_oferece_coluna_de_ML_e_explica_a_ausencia()
    {
        var resultado = ComparacoesApiClient.ParseResultado(ResultadoSemMl());

        resultado.Should().NotBeNull();
        resultado!.Confronto.Should().BeNull("nenhum item teve os dois braços");
        resultado.TemColunaMl.Should().BeFalse(
            "sem confronto a tela não pode exibir número nenhum do braço de ML");
        resultado.ItensComDecisaoMl.Should().Be(0);

        resultado.ExplicacaoSemColunaMl.Should().Be(MotivoDoWorker,
            "o texto que ocupa o lugar dos números vem do resultado, em português de comprador");
        resultado.ExplicacaoSemColunaMl.Should().NotBe("—");
        resultado.ExplicacaoSemColunaMl.Should().NotBeNullOrWhiteSpace();

        resultado.ItensComPrevisaoMl.Should().Be(4,
            "a previsão da camada A costuma existir exatamente onde a decisão não existe");
        resultado.Pbs!.SobraUnidades.Should().Be(75m,
            "o braço do ERP vale por si mesmo, mesmo sem contraparte de ML");
    }

    /// <summary>
    /// Payload antigo, sem o motivo gravado. A tela continua devendo uma frase: calar aqui
    /// deixaria a coluna do ML vazia, que é justamente a leitura proibida.
    /// </summary>
    [Fact]
    public void Resultado_sem_confronto_e_sem_motivo_registrado_ainda_explica_em_vez_de_calar()
    {
        var resultado = ComparacoesApiClient.ParseResultado(
            ResultadoSemMl(motivo: null))!;

        resultado.TemColunaMl.Should().BeFalse();
        resultado.ExplicacaoSemColunaMl.Should().NotBeNullOrWhiteSpace();
        resultado.ExplicacaoSemColunaMl.Should().Contain("não registrou o motivo");
        resultado.ExplicacaoSemColunaMl.Should().Contain("o que de fato aconteceu",
            "sem o motivo, o que sobra a afirmar é que o lado do ERP continua válido");
    }

    /// <summary>
    /// Confronto presente mas sobre zero item: bem formado e vazio. Exibir os dois braços
    /// zerados lado a lado leria como empate entre os métodos.
    /// </summary>
    [Fact]
    public void Confronto_sobre_zero_item_nao_habilita_a_coluna_de_ML()
    {
        var json = ResultadoSemMl().Replace(
            "\"confronto\":null",
            "\"confronto\":{\"itens\":0,\"pbs\":{\"compraUnidades\":0,\"compraValor\":0,\"sobraUnidades\":0,\"sobraValor\":0},\"ml\":{\"compraUnidades\":0,\"compraValor\":0,\"sobraUnidades\":0,\"sobraValor\":0}}");

        ComparacoesApiClient.ParseResultado(json)!.TemColunaMl.Should().BeFalse(
            "zero item confrontado não é empate: é ausência de comparação");
    }

    [Fact]
    public void Confronto_com_itens_habilita_a_coluna_de_ML_com_o_denominador_dele()
    {
        var json = ResultadoSemMl().Replace(
            "\"confronto\":null",
            "\"confronto\":{\"itens\":3,\"pbs\":{\"compraUnidades\":100,\"compraValor\":350,\"sobraUnidades\":40,\"sobraValor\":140},\"ml\":{\"compraUnidades\":70,\"compraValor\":245,\"sobraUnidades\":10,\"sobraValor\":35}}");

        var resultado = ComparacoesApiClient.ParseResultado(json)!;

        resultado.TemColunaMl.Should().BeTrue();
        resultado.Confronto!.Itens.Should().Be(3);
        resultado.Confronto.Ml!.SobraUnidades.Should().Be(10m);
        resultado.Confronto.Pbs!.SobraUnidades.Should().Be(40m,
            "o braço do ERP do confronto é restrito aos mesmos itens, e é com ele que o ML se compara");
    }

    /// <summary>
    /// Figuras em R$ com itens sem preço de compra: a manchete tem de poder dizer que o
    /// total está subestimado, senão ele aparece com cara de figura completa.
    /// </summary>
    [Fact]
    public void Itens_sem_preco_de_compra_marcam_as_figuras_em_reais_como_incompletas()
    {
        ComparacoesApiClient.ParseResultado(ResultadoSemMl())!
            .ValoresSubestimados.Should().BeTrue("um item da amostra não tem preço de compra");

        ComparacoesApiClient.ParseResultado(ResultadoSemMl(itensSemPrecoCompra: 0))!
            .ValoresSubestimados.Should().BeFalse();
    }

    /// <summary>
    /// Ruptura observada só é interpretável com a cobertura do snapshot ao lado: zero dia
    /// zerado numa cobertura sem snapshot é "não sabemos", não "não faltou".
    /// </summary>
    [Fact]
    public void Cobertura_do_snapshot_acompanha_a_ruptura_observada()
    {
        var resultado = ComparacoesApiClient.ParseResultado(ResultadoSemMl())!;

        resultado.Ruptura!.DiasSemEstoque.Should().Be(1);
        resultado.CoberturaDoSnapshot.Should().BeApproximately(5d / 60d, 1e-9,
            "5 dias com snapshot em 60 dias-item de cobertura");
    }

    /// <summary>
    /// <c>ResultadoJson</c> é dado já gravado: toda sessão materializada antes de o recorte por
    /// curva e a compra em R$ saírem do resultado continua com esses membros no payload — e a
    /// amostra usada por estes testes é justamente uma dessas. Abrir a tela dessas sessões não
    /// pode virar "não consigo ler isto": membro desconhecido é ignorado, não recusado.
    /// </summary>
    [Fact]
    public void ParseResultado_de_payload_anterior_ignora_o_que_saiu_do_resultado()
    {
        var json = ResultadoSemMl();
        json.Should().Contain("porCurva").And.Contain("compraValor",
            "a amostra tem de continuar sendo um payload da versão anterior, senão o teste não prova nada");

        var resultado = ComparacoesApiClient.ParseResultado(json);

        resultado.Should().NotBeNull("payload antigo abre normalmente");
        resultado!.Pbs!.SobraValor.Should().Be(192.5m, "o que a tela lê continua sendo lido");
        resultado.ItensAvaliados.Should().Be(2);
    }

    [Fact]
    public void ParseResultado_de_json_ilegivel_devolve_nulo_em_vez_de_estourar_no_render()
    {
        ComparacoesApiClient.ParseResultado("{\"geradoEm\":").Should().BeNull();
        ComparacoesApiClient.ParseResultado("").Should().BeNull();
        ComparacoesApiClient.ParseResultado(null).Should().BeNull();
    }

    // --- Linha do detalhe -----------------------------------------------------

    /// <summary>
    /// Linha sem braço de ML não declara vencedor. Nulo tem de virar texto na tela: "empate"
    /// e "não calculado" são afirmações opostas.
    /// </summary>
    [Fact]
    public void Linha_sem_braco_de_ML_nao_declara_vencedor()
    {
        var item = Linha(sobraPbs: 55m, sobraMl: null);

        item.MlFicouMaisPerto.Should().BeNull();
        item.Empate.Should().BeFalse("sem braço de ML não há empate a declarar");
    }

    [Theory]
    [InlineData(55, 10, true)]
    [InlineData(10, 55, false)]
    public void Linha_com_braco_de_ML_declara_quem_ficou_mais_perto(
        int sobraPbs, int sobraMl, bool mlMaisPerto)
    {
        var item = Linha(sobraPbs, sobraMl);

        item.MlFicouMaisPerto.Should().Be(mlMaisPerto);
        item.Empate.Should().BeFalse();
    }

    /// <summary>
    /// Sobras iguais empatam, e empate é um estado próprio — nem vitória do ML, nem do ERP.
    /// </summary>
    [Fact]
    public void Sobras_iguais_empatam_sem_declarar_vencedor()
    {
        var item = Linha(sobraPbs: 20m, sobraMl: 20m);

        item.Empate.Should().BeTrue();
        item.MlFicouMaisPerto.Should().BeFalse("empate não é o ML ter ficado mais perto");
    }

    // --- Métricas derivadas da análise ---------------------------------------

    /// <summary>
    /// MAE e WAPE saem das somas cruas, e a fatia sabe dizer quando o ML perde nela — é o
    /// que impede a média global de esconder a regressão local (CLAUDE.md §6).
    /// </summary>
    [Fact]
    public void Fatia_deriva_mae_e_wape_das_somas_e_marca_onde_o_ML_perde()
    {
        var fatia = new SessaoFatia(
            "A", Itens: 10, ItensComPrevisaoMl: 4,
            SomaDemandaRealDiaria: 20m, SomaErroAbsPbs: 2m, SomaErroAbsMl: 6m,
            VitoriasMl: 1, VitoriasPbs: 3);

        fatia.MaePbs.Should().BeApproximately(0.5, 1e-9);
        fatia.MaeMl.Should().BeApproximately(1.5, 1e-9);
        fatia.WapePbs.Should().BeApproximately(0.10, 1e-9);
        fatia.WapeMl.Should().BeApproximately(0.30, 1e-9);
        fatia.MlPerde.Should().BeTrue();
    }

    [Fact]
    public void Fatia_sem_item_medido_nao_apura_metrica_em_vez_de_apurar_zero()
    {
        var fatia = new SessaoFatia("C", Itens: 900, ItensComPrevisaoMl: 0,
            SomaDemandaRealDiaria: 0m, SomaErroAbsPbs: 0m, SomaErroAbsMl: 0m,
            VitoriasMl: 0, VitoriasPbs: 0);

        fatia.MaePbs.Should().BeNull("zero erro sobre zero item não é acerto perfeito");
        fatia.WapeMl.Should().BeNull();
        fatia.MlPerde.Should().BeFalse("sem métrica não há como afirmar que alguém perdeu");
    }

    /// <summary>
    /// O global sai da soma das fatias por curva, e não de uma consulta à parte: cada item
    /// cai em exatamente uma curva, então a soma é o total — e dois caminhos para o mesmo
    /// número seriam duas versões dele.
    /// </summary>
    [Fact]
    public void Global_da_analise_e_a_soma_das_fatias_por_curva()
    {
        var analise = new SessaoAnalise(
            Itens: 30,
            PorCurva:
            [
                new("A", 10, 4, 20m, 2m, 6m, 1, 3),
                new("B", 20, 6, 30m, 3m, 3m, 4, 2),
            ],
            PorLoja: [],
            ItensComDecisaoMl: 0,
            ItensComSobraMlMaior: 0,
            SobraExtraMlUnidades: 0m,
            SobraExtraMlValor: 0m,
            PioresNaCompra: [],
            PioresNaPrevisao: []);

        analise.ItensComPrevisaoMl.Should().Be(10);
        analise.VitoriasMl.Should().Be(5);
        analise.VitoriasPbs.Should().Be(5);
        analise.Empates.Should().Be(0);
        analise.Global.Itens.Should().Be(30, "o denominador é a população inteira da sessão");
        analise.Global.ItensComPrevisaoMl.Should().Be(10);
        analise.Global.WapeMl.Should().BeApproximately((double)(9m / 50m), 1e-9);
    }

    // --- Escopo de rede das leituras novas ------------------------------------

    [Fact]
    public async Task GetItensAsync_envia_o_escopo_de_rede_do_IRedeContext_com_a_paginacao()
    {
        var (client, captured) = ClientReturning(
            HttpStatusCode.OK,
            "{\"total\":0,\"orderBy\":\"SobraPbsUnidades\",\"desc\":true,\"itens\":[]}");

        await client.GetItensAsync(SessaoId, skip: 50, take: 25, orderBy: "SobraPbsValor", desc: false);

        captured()!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/comparacoes/{SessaoId}/itens?redeId={RedeDeTeste}&skip=50&take=25&desc=false&orderBy=SobraPbsValor",
            "o escopo de inquilino vem do IRedeContext, nunca de rota/query da página");
    }

    [Fact]
    public async Task GetItensAsync_omite_orderBy_quando_nenhuma_coluna_foi_pedida()
    {
        var (client, captured) = ClientReturning(
            HttpStatusCode.OK,
            "{\"total\":0,\"orderBy\":\"SobraPbsUnidades\",\"desc\":true,\"itens\":[]}");

        await client.GetItensAsync(SessaoId, skip: 0, take: 25, orderBy: null, desc: true);

        captured()!.RequestUri!.PathAndQuery.Should().NotContain("orderBy",
            "sem coluna pedida o servidor aplica o padrão dele, e a resposta diz qual foi");
    }

    [Fact]
    public async Task GetAnaliseAsync_devolve_nulo_em_404_em_vez_de_estourar()
    {
        var (client, captured) = ClientReturning(HttpStatusCode.NotFound, "");

        (await client.GetAnaliseAsync(SessaoId)).Should().BeNull();
        captured()!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/comparacoes/{SessaoId}/analise?redeId={RedeDeTeste}");
    }

    // --- Amostras -------------------------------------------------------------

    /// <summary>Motivo tal como o Worker o escreve para <c>ForaDoHorizonteMl</c>.</summary>
    private const string MotivoDoWorker =
        "A compra desta sugestão cobre mais dias do que o método de ML consegue prever hoje, então não é " +
        "possível dizer quanto ele mandaria comprar sem inventar a demanda dos dias que faltam. A comparação " +
        "de previsão de demanda, dia a dia, continua valendo e está na área técnica.";

    /// <summary>
    /// Agregados no formato que o <c>SessaoResultadoMaterializador</c> grava (Web defaults,
    /// enum como texto), no desfecho esperado hoje: sem confronto e com o motivo ao lado.
    /// </summary>
    private static string ResultadoSemMl(
        string? motivo = MotivoDoWorker, int itensSemPrecoCompra = 1) =>
        $$"""
        {
          "geradoEm": "2026-07-31T12:00:00+00:00",
          "comparacaoPbsId": "0199f14a-0000-7000-8000-0000000000bb",
          "sugestaoDataHora": "2026-07-01T09:30:00",
          "tipoCalculo": 2,
          "itensAvaliados": 2,
          "vendidoNaJanelaUnidades": 60,
          "pbs": {"compraUnidades":120,"compraValor":350,"sobraUnidades":75,"sobraValor":192.5},
          "confronto":null,
          "motivoMlIndisponivel": {{(motivo is null ? "null" : $"\"{motivo}\"")}},
          "itensComDecisaoMl": 0,
          "itensComPrevisaoMl": 4,
          "utilidadeDecisaoMl": "ForaDoHorizonteMl",
          "ruptura": {"itensComDiaSemEstoque":1,"diasSemEstoque":1,"diasComSnapshot":5,"diasNaJanela":60},
          "itensComJanelaAlemDoHistorico": 0,
          "itensSemPrecoCompra": {{itensSemPrecoCompra}},
          "skusSemCadastro": 3,
          "porCurva": [{"curva":"A","itens":1,"itensComDecisaoMl":0,"sobraPbsUnidades":55,"sobraPbsValor":192.5}],
          "ressalvaTreinoServe": "Ressalva de teste."
        }
        """;

    private static SessaoItem Linha(decimal sobraPbs, decimal? sobraMl) => new(
        LojaId: 1,
        Sku: "SKU-1",
        NomeProduto: "Produto",
        Curva: "A",
        CompraSugeridaPbs: 100m,
        CompraSugeridaMl: sobraMl is null ? null : 70m,
        VendidoNaJanela: 60m,
        SobraPbsUnidades: sobraPbs,
        SobraMlUnidades: sobraMl,
        SobraPbsValor: null,
        JanelaAlemDoHistorico: false);
}
