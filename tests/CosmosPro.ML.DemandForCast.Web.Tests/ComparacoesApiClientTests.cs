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
            "A", Itens: 10, ItensComPrevisaoMl: 4, ItensComVendaPositiva: 4,
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
        var fatia = new SessaoFatia("C", Itens: 900, ItensComPrevisaoMl: 0, ItensComVendaPositiva: 0,
            SomaDemandaRealDiaria: 0m, SomaErroAbsPbs: 0m, SomaErroAbsMl: 0m,
            VitoriasMl: 0, VitoriasPbs: 0);

        fatia.MaePbs.Should().BeNull("zero erro sobre zero item não é acerto perfeito");
        fatia.WapeMl.Should().BeNull();
        fatia.MlPerde.Should().BeFalse("sem métrica não há como afirmar que alguém perdeu");
    }

    /// <summary>
    /// O recorte "so onde o ML ficou pior" viaja na query string com o mesmo nome que a
    /// apiservice le. Sao dois processos: um nome divergente aqui devolveria a tabela inteira
    /// sem erro nenhum, e o link do bloco abriria um recorte que nao recorta nada.
    /// </summary>
    [Fact]
    public void Filtro_de_ml_pior_viaja_na_query_string_e_conta_como_recorte()
    {
        var filtro = new FiltroDeItens(SomenteMlPior: true);

        filtro.ParaQueryString().Should().Be("&somenteMlPior=true");
        filtro.Algum.Should().BeTrue("a tela precisa mostrar que ha um recorte ativo");
        FiltroDeItens.Nenhum.ParaQueryString().Should().NotContain("somenteMlPior");
    }

    /// <summary>
    /// <b>"Calculou" e "mandou comprar" sao numeros diferentes</b>, e a falta do segundo gerou
    /// reclamacao do patrocinador em 27/08/2026: ele leu "2.106 registros com calculo do ML"
    /// como 2.106 itens comprados, quando o ML mandou comprar acima de zero em algumas dezenas.
    /// O rotulo antigo ja dizia "com calculo", mas nao existia o numero que ele queria — e
    /// decidir comprar ZERO e uma decisao, nao uma ausencia.
    /// </summary>
    [Fact]
    public void Compra_positiva_do_ML_e_contada_a_parte_de_quem_apenas_teve_calculo()
    {
        var t = Totais(
            sobraPbsTotal: 4194m, sobraPbsComparavel: 3692m, sobraMl: 3693m,
            itensComCompraMl: 2106, itensComCompraMlPositiva: 40);

        t.ItensComCompraMl.Should().Be(2106, "o ML calculou para todos esses");
        t.ItensComCompraMlPositiva.Should().Be(40, "mas so mandou comprar nesses");
    }

    /// <summary>
    /// <b>A exportacao tem de honrar TODOS os filtros.</b> A rota do Excel lia apenas tres
    /// deles e montava a query a mao; o resultado era a planilha ignorando o recorte da tela em
    /// silencio -- o comprador filtrava "so com alerta", exportava, e recebia a sugestao
    /// inteira. Agora os dois caminhos usam ParaQueryString, que e o formato de fio unico, e
    /// este caso afirma que nenhum filtro fica de fora dele.
    /// </summary>
    [Fact]
    public void Formato_de_fio_do_filtro_carrega_todos_os_recortes()
    {
        var cheio = new FiltroDeItens(
            LojaId: 18, Categoria: "MIP/OTC", Curva: "A",
            SomenteComAlerta: true, SomenteMlPior: true,
            Fabricante: "EMS", Alerta: "Ruptura", AnaliseRapida: "Vermelho",
            MaisPerto: "ML", IndiceAbaixoDe: 0.5m, Preco: "RedeMenor");

        var q = cheio.ParaQueryString();

        foreach (var chave in new[]
                 {
                     "lojaId", "categoria", "curva", "somenteComAlerta", "somenteMlPior",
                     "fabricante", "alerta", "analiseRapida", "maisPerto", "indiceAbaixoDe", "preco",
                 })
        {
            q.Should().Contain($"&{chave}=", $"'{chave}' precisa atravessar para a exportacao");
        }
    }

    // --- Filtros novos --------------------------------------------------------

    /// <summary>
    /// Os seis filtros pedidos pelo patrocinador viajam na query string com os nomes que a
    /// apiservice le. Sao dois processos: um nome divergente aqui devolveria a tabela inteira
    /// sem erro nenhum, e o comprador acreditaria estar olhando um recorte.
    /// </summary>
    [Fact]
    public void Filtros_novos_viajam_na_query_string()
    {
        var f = new FiltroDeItens(
            Fabricante: "EMS",
            Alerta: "Ruptura",
            AnaliseRapida: "Vermelho",
            MaisPerto: "ML",
            IndiceAbaixoDe: 0.5m,
            Preco: "RedeMenor");

        var q = f.ParaQueryString();

        q.Should().Contain("&fabricante=EMS");
        q.Should().Contain("&alerta=Ruptura");
        q.Should().Contain("&analiseRapida=Vermelho");
        q.Should().Contain("&maisPerto=ML");
        q.Should().Contain("&preco=RedeMenor");
        // Ponto e nao virgula: a query string nao carrega cultura, e "0,5" viraria dois
        // parametros ou um numero recusado pelo servidor.
        q.Should().Contain("&indiceAbaixoDe=0.5");
        f.Algum.Should().BeTrue();
    }

    /// <summary>
    /// Cada um dos seis conta como recorte por si. Se algum ficasse fora de <c>Algum</c>, a tela
    /// mostraria "todos os itens" enquanto exibisse um subconjunto, e o botao de limpar filtro
    /// nem apareceria.
    /// </summary>
    [Theory]
    [InlineData("fabricante")]
    [InlineData("alerta")]
    [InlineData("analise")]
    [InlineData("maisPerto")]
    [InlineData("indice")]
    [InlineData("preco")]
    public void Cada_filtro_novo_conta_como_recorte(string qual)
    {
        var f = qual switch
        {
            "fabricante" => new FiltroDeItens(Fabricante: "EMS"),
            "alerta" => new FiltroDeItens(Alerta: "Ruptura"),
            "analise" => new FiltroDeItens(AnaliseRapida: "Vermelho"),
            "maisPerto" => new FiltroDeItens(MaisPerto: "ML"),
            "indice" => new FiltroDeItens(IndiceAbaixoDe: 0.5m),
            _ => new FiltroDeItens(Preco: "RedeMenor"),
        };

        f.Algum.Should().BeTrue($"'{qual}' recorta a tabela");
    }

    // --- Placar de preco no painel de mercado ---------------------------------

    /// <summary>
    /// O comparativo de precos que o patrocinador pediu para o painel de mercado.
    ///
    /// <para>
    /// <b>Sao TRES grupos e nao dois.</b> No exemplo dele os dois lados somavam exatamente o
    /// total ("dos 11.765 comparados, 5.765 IQVIA menor e 6.000 Retiro menor"), sem sobra. Na
    /// pratica existe um terceiro: o item em que a rede nao vendeu nada e por isso nao tem preco
    /// a comparar. Ele nao e "mais caro" nem "mais barato", e jogá-lo em qualquer um dos dois
    /// lados inventaria um resultado. Sem o terceiro numero a soma nao fecha e a tela parece
    /// errada.
    /// </para>
    /// </summary>
    [Fact]
    public void Placar_de_preco_declara_tambem_os_itens_sem_comparacao()
    {
        var t = Totais(
            sobraPbsTotal: 4194m, sobraPbsComparavel: 3692m, sobraMl: 3693m,
            comMercado: 11_765, comPrecoComparavel: 8_000,
            precoRedeMenor: 3_400, precoRedeMaior: 4_500);

        t.ItensSemPrecoComparavel.Should().Be(3_765,
            "dos 11.765 com medicao, 8.000 tem preco nos dois lados");
        t.ItensComPrecoEmpatado.Should().Be(100, "8.000 - 3.400 - 4.500");
    }

    /// <summary>
    /// Sem medicao de mercado no recorte, nao ha placar — e nao um placar de zero a zero, que
    /// leria como "os precos estao iguais".
    /// </summary>
    [Fact]
    public void Sem_medicao_de_mercado_nao_ha_placar_de_preco()
    {
        var t = Totais(
            sobraPbsTotal: 4194m, sobraPbsComparavel: 3692m, sobraMl: 3693m,
            comMercado: 0, comPrecoComparavel: 0, precoRedeMenor: 0, precoRedeMaior: 0);

        t.TemPlacarDePreco.Should().BeFalse();
    }

    // --- Preco: praticado pela rede contra a referencia do mercado (Opcao B) ---

    /// <summary>
    /// O lado da rede e o preco PRATICADO, da base de vendas dela; o lado do mercado e o preco
    /// de REFERENCIA da IQVIA (valor / unidades).
    ///
    /// <para>
    /// <b>A versao anterior tirava os dois lados da IQVIA</b>, e o comentario do teste afirmava
    /// que isso era o que "torna a comparacao entre eles legitima". Era o oposto: a IQVIA
    /// normaliza preco entre os participantes do painel, entao `valor / unidades` devolve o
    /// mesmo numero para qualquer bandeira — 37.410 pares medidos em agosto, zero diferenca. O
    /// patrocinador viu as duas colunas iguais na tela em 09/09/2026 e aprovou a Opcao B.
    /// </para>
    /// </summary>
    [Fact]
    public void Preco_da_rede_e_o_praticado_e_o_do_mercado_e_a_referencia()
    {
        var item = ComMercado(unidadesRede: 12m, valorRede: 240m,
                              unidadesConc: 988m, valorConc: 19_760m,
                              precoPraticado: 25m);

        item.PrecoMedioRede.Should().Be(25m, "vem da base de vendas, nao da IQVIA");
        item.PrecoMedioConcorrentes.Should().Be(20m, "19.760 / 988 = referencia de 20");
        item.RedeMaisCaraQueOMercado.Should().BeTrue("25 praticado contra 20 de referencia");
    }

    /// <summary>
    /// <b>A regressao que este teste existe para impedir.</b> Com valor e unidades da IQVIA no
    /// lado da rede, a versao anterior calculava um preco (240/12 = 20) que era, por
    /// construcao, identico ao da referencia — e a seta na tela nunca acendia. Agora sem venda
    /// da rede no mes nao ha preco praticado, e a celula diz isso.
    /// </summary>
    [Fact]
    public void Preco_da_rede_nao_sai_mais_do_valor_da_IQVIA()
    {
        var item = ComMercado(unidadesRede: 12m, valorRede: 240m,
                              unidadesConc: 988m, valorConc: 19_760m,
                              precoPraticado: null);

        item.PrecoMedioRede.Should().BeNull(
            "a IQVIA tem valor e unidades da bandeira da rede, e isso NAO e preco praticado");
        item.PrecoMedioRede.Should().NotBe(20m, "20 e a referencia; devolve-la seria a tautologia antiga");
        item.PrecoMedioConcorrentes.Should().Be(20m);
        item.RedeMaisCaraQueOMercado.Should().BeNull("com um lado ausente nao ha comparacao");
    }

    /// <summary>
    /// Sem venda da rede no mes comparado nao ha preco praticado. Zero afirmaria que ela vendeu
    /// de graca, e esta e uma coluna pela qual o comprador ordena.
    /// </summary>
    [Fact]
    public void Sem_venda_da_rede_no_mes_nao_ha_preco_praticado()
    {
        var item = ComMercado(unidadesRede: 0m, valorRede: 0m,
                              unidadesConc: 988m, valorConc: 19_760m,
                              precoPraticado: null);

        item.PrecoMedioRede.Should().BeNull();
        item.PrecoMedioRede.Should().NotBe(0m);
        item.PrecoMedioConcorrentes.Should().Be(20m);
        item.RedeMaisCaraQueOMercado.Should().BeNull();
    }

    /// <summary>
    /// Preco praticado sem referencia tambem nao compara: a rede vendeu, e a IQVIA nao reportou
    /// o item naquele bairro. Falso ali significaria "esta abaixo da referencia", que e outra
    /// afirmacao.
    /// </summary>
    [Fact]
    public void Preco_praticado_sem_referencia_nao_compara()
    {
        var item = ComMercado(unidadesRede: null, valorRede: null,
                              unidadesConc: null, valorConc: null,
                              precoPraticado: 25m);

        item.PrecoMedioRede.Should().Be(25m);
        item.PrecoMedioConcorrentes.Should().BeNull();
        item.RedeMaisCaraQueOMercado.Should().BeNull();
    }

    [Fact]
    public void Sem_dado_de_mercado_nao_ha_preco_nenhum()
    {
        var item = ComMercado(null, null, null, null);

        item.PrecoMedioRede.Should().BeNull();
        item.PrecoMedioConcorrentes.Should().BeNull();
        item.RedeMaisCaraQueOMercado.Should().BeNull();
    }

    private static SessaoItem ComMercado(
        decimal? unidadesRede, decimal? valorRede,
        decimal? unidadesConc, decimal? valorConc,
        decimal? precoPraticado = null) => new(
        LojaId: 1, Sku: "SKU-1", NomeProduto: "Produto", Curva: "A",
        CompraSugeridaPbs: 100m, CompraSugeridaMl: null, VendidoNaJanela: 0m,
        SobraPbsUnidades: 0m, SobraMlUnidades: null, SobraPbsValor: null,
        JanelaAlemDoHistorico: false,
        MercadoUnidadesRede: unidadesRede,
        MercadoUnidadesConcorrentes: unidadesConc,
        MercadoValorRede: valorRede,
        MercadoValorConcorrentes: valorConc,
        PrecoVendaPraticado: precoPraticado);

    // --- Cobertura e analise rapida -------------------------------------------

    /// <summary>
    /// A regua do patrocinador (05/09/2026): vermelho acima de 60 dias de cobertura, amarelo
    /// entre 30 e 59, verde abaixo de 30. A cobertura divide o <b>estoque do fim do periodo</b>
    /// — resposta 1A — pela venda media diaria de 120 dias.
    /// </summary>
    [Theory]
    [InlineData(20, 0.25, 80, "Vermelho")]   // 80 dias parados
    [InlineData(12, 0.25, 48, "Amarelo")]    // 48 dias
    [InlineData(5, 0.25, 20, "Verde")]       // 20 dias
    [InlineData(15, 0.25, 60, "Vermelho")]   // exatamente 60 ja e vermelho
    [InlineData(7.5, 0.25, 30, "Amarelo")]   // exatamente 30 ja e amarelo
    public void Analise_rapida_segue_a_regua_de_60_e_30_dias(
        double estoqueFim, double media, double coberturaEsperada, string cor)
    {
        var item = Linha(estoqueNoFim: (decimal)estoqueFim, vendaMedia: (decimal)media);

        item.Cobertura.Should().BeApproximately((decimal)coberturaEsperada, 0.01m);
        item.AnaliseRapida.Should().Be(cor);
    }

    /// <summary>
    /// <b>A quarta cor — resposta 2B.</b> Item com estoque na prateleira e venda zero em 120
    /// dias nao tem cobertura: a divisao nao existe. Mas "nao da conta" nao e "esta tudo bem" —
    /// e provavelmente o pior item da lista, dinheiro parado que nao gira. Ele ganha estado
    /// proprio para nao se confundir nem com encalhado nem com sem dado.
    /// </summary>
    [Fact]
    public void Estoque_parado_com_venda_zero_ganha_estado_proprio_e_nao_some()
    {
        var item = Linha(estoqueNoFim: 40m, vendaMedia: 0m);

        item.Cobertura.Should().BeNull("nao existe divisao por zero");
        item.AnaliseRapida.Should().Be("SemGiro");
    }

    /// <summary>
    /// Prateleira vazia e o oposto de encalhe: cobertura zero, verde. Nao pode cair no
    /// <c>SemGiro</c> so porque a venda media tambem e zero — nao ha capital parado nenhum.
    /// </summary>
    [Fact]
    public void Prateleira_vazia_e_verde_mesmo_sem_venda_media()
    {
        var item = Linha(estoqueNoFim: 0m, vendaMedia: 0m);

        item.Cobertura.Should().Be(0m);
        item.AnaliseRapida.Should().Be("Verde");
    }

    /// <summary>
    /// Sem estoque medido ou sem historico para medir a media, nao ha cor: a celula diz "sem
    /// dado", que e diferente das quatro cores. Sessao materializada antes destas colunas cai
    /// aqui, e pintar de verde afirmaria que esta tudo bem com um item que ninguem avaliou.
    /// </summary>
    [Theory]
    [InlineData(null, 0.25)]
    [InlineData(40.0, null)]
    [InlineData(null, null)]
    public void Sem_estoque_ou_sem_media_nao_ha_cor(double? estoqueFim, double? media)
    {
        var item = Linha(
            estoqueNoFim: (decimal?)estoqueFim,
            vendaMedia: (decimal?)media);

        item.Cobertura.Should().BeNull();
        item.AnaliseRapida.Should().BeNull("nulo e ausencia de avaliacao, nao aprovacao");
    }

    private static SessaoItem Linha(decimal? estoqueNoFim, decimal? vendaMedia) => new(
        LojaId: 1,
        Sku: "SKU-1",
        NomeProduto: "Produto",
        Curva: "A",
        CompraSugeridaPbs: 100m,
        CompraSugeridaMl: null,
        VendidoNaJanela: 0m,
        SobraPbsUnidades: 0m,
        SobraMlUnidades: null,
        SobraPbsValor: null,
        JanelaAlemDoHistorico: false,
        EstoqueNoFimDoPeriodo: estoqueNoFim,
        VendaMediaDiaria: vendaMedia);

    // --- Totais: a comparacao so vale sobre a mesma populacao -----------------

    /// <summary>
    /// A diferenca de sobra sai do <b>subconjunto comparavel</b>, nunca do total geral do PBS
    /// contra a soma do ML.
    ///
    /// <para>
    /// Este era o defeito: o PBS somava os 20.153 itens do recorte e o ML somava os 2.106 em
    /// que ele foi calculado, e a tela chamava a subtracao de "diferenca de sobra". O numero
    /// nao media metodo nenhum — media 18 mil itens a menos na conta —, e a leitura natural
    /// dele ("o ML deixaria 501 unidades a menos encalhadas") era falsa.
    /// </para>
    /// </summary>
    [Fact]
    public void Diferenca_de_sobra_compara_o_PBS_e_o_ML_sobre_o_mesmo_subconjunto()
    {
        var t = Totais(sobraPbsTotal: 4194m, sobraPbsComparavel: 3800m, sobraMl: 3693m);

        t.DiferencaSobraUnidades.Should().Be(3693m - 3800m,
            "o lado do PBS tem de ser o do subconjunto em que o ML foi calculado");
        t.DiferencaSobraUnidades.Should().NotBe(3693m - 4194m,
            "somar o total geral do PBS contra o subconjunto do ML foi exatamente o defeito");
    }

    [Fact]
    public void Sem_braco_de_ML_nao_existe_diferenca_a_declarar()
    {
        var t = Totais(sobraPbsTotal: 4194m, sobraPbsComparavel: null, sobraMl: null);

        t.DiferencaSobraUnidades.Should().BeNull("zero afirmaria que os dois metodos empataram");
        t.DiferencaSobraValor.Should().BeNull();
    }

    /// <summary>
    /// Em reais o subconjunto e mais estreito ainda: so entra item que tenha preco de compra
    /// nos <b>dois</b> bracos. Reaproveitar o comparavel de unidades traria itens sem preco
    /// para um lado da conta e nao para o outro.
    /// </summary>
    [Fact]
    public void Diferenca_em_reais_usa_o_subconjunto_com_preco_nos_dois_bracos()
    {
        var t = Totais(
            sobraPbsTotal: 4194m, sobraPbsComparavel: 3800m, sobraMl: 3693m,
            valorPbsTotal: 261235.11m, valorPbsComparavel: 240000m, valorMl: 222215.79m);

        t.DiferencaSobraValor.Should().Be(222215.79m - 240000m);
        t.DiferencaSobraValor.Should().NotBe(222215.79m - 261235.11m);
    }

    /// <summary>
    /// <b>A compra tambem tem lado comparavel</b>, e ela ficou de fora quando a sobra e o valor
    /// ganharam o deles: a faixa de comparacao exibia a compra do PBS somada sobre TODOS os
    /// itens (207 un.) ao lado da compra do ML sobre os 2.106 calculados (68 un.). Mesmo
    /// defeito da diferenca de sobra, numa coluna que ninguem tinha olhado — quem revelou foi
    /// o mockup do patrocinador, que pede "Quantidade de compra — mesmos itens".
    /// </summary>
    [Fact]
    public void Compra_do_PBS_na_faixa_comparavel_soma_apenas_os_itens_com_calculo_do_ML()
    {
        var t = Totais(
            sobraPbsTotal: 4194m, sobraPbsComparavel: 3692m, sobraMl: 3693m,
            compraPbsTotal: 207m, compraPbsComparavel: 66m, compraMl: 68m);

        t.CompraPbsComparavelUnidades.Should().Be(66m);
        t.CompraPbsUnidades.Should().Be(207m, "o total da sugestao continua existindo, fora do confronto");
        t.DiferencaCompraUnidades.Should().Be(68m - 66m);
        t.DiferencaCompraUnidades.Should().NotBe(68m - 207m,
            "comparar a compra do ML com o total do PBS mede populacao, nao metodo");
    }

    [Fact]
    public void Sem_braco_de_ML_a_compra_comparavel_e_a_diferenca_sao_nulas()
    {
        var t = Totais(
            sobraPbsTotal: 4194m, sobraPbsComparavel: null, sobraMl: null,
            compraPbsTotal: 207m, compraPbsComparavel: null, compraMl: null);

        t.CompraPbsComparavelUnidades.Should().BeNull();
        t.DiferencaCompraUnidades.Should().BeNull("zero afirmaria que os dois compraram igual");
    }

    private static TotaisDosItens Totais(
        decimal sobraPbsTotal,
        decimal? sobraPbsComparavel,
        decimal? sobraMl,
        decimal? valorPbsTotal = null,
        decimal? valorPbsComparavel = null,
        decimal? valorMl = null,
        decimal compraPbsTotal = 207m,
        decimal? compraPbsComparavel = 66m,
        decimal? compraMl = 68m,
        int itensComCompraMl = 2106,
        int itensComCompraMlPositiva = 40,
        int comMercado = 0,
        int comPrecoComparavel = 0,
        int precoRedeMenor = 0,
        int precoRedeMaior = 0) => new(
        Itens: 20153,
        CompraPbsUnidades: compraPbsTotal,
        CompraPbsComparavelUnidades: sobraMl is null ? null : compraPbsComparavel,
        CompraMlUnidades: sobraMl is null ? null : compraMl,
        ItensComCompraMl: sobraMl is null ? 0 : itensComCompraMl,
        ItensComCompraMlPositiva: sobraMl is null ? 0 : itensComCompraMlPositiva,
        VendidoNaJanela: 1000m,
        SobraPbsUnidades: sobraPbsTotal,
        SobraPbsComparavelUnidades: sobraPbsComparavel,
        SobraMlUnidades: sobraMl,
        ItensComSobraMl: sobraMl is null ? 0 : 2106,
        SobraPbsValor: valorPbsTotal,
        ItensComValorPbs: valorPbsTotal is null ? 0 : 18000,
        SobraPbsComparavelValor: valorPbsComparavel,
        SobraMlValor: valorMl,
        ItensComValorMl: valorMl is null ? 0 : 2106,
        ItensComDadoDeMercado: comMercado,
        ItensComPrecoComparavel: comPrecoComparavel,
        ItensComPrecoRedeMenor: precoRedeMenor,
        ItensComPrecoRedeMaior: precoRedeMaior);

    // --- Leitura de WAPE e MAE ------------------------------------------------

    /// <summary>
    /// A explicação de WAPE e MAE fecha com os números desta execução, e o veredito sai
    /// desses números. O risco que estes casos cobrem não é a redação: é a frase declarar um
    /// vencedor quando não há métrica apurada, ou quando os dois erraram igual.
    /// </summary>
    [Fact]
    public void Leitura_do_wape_nomeia_quem_errou_menos_com_os_numeros_da_execucao()
    {
        var fatia = Global(somaDemanda: 20m, erroPbs: 2m, erroMl: 6m);

        fatia.LeituraDoWape.Should()
            .Contain(0.10.ToString("P1")).And.Contain(0.30.ToString("P1"))
            .And.Contain("seu ERP");
    }

    [Fact]
    public void Leitura_do_mae_nomeia_quem_errou_menos_em_unidades_por_dia()
    {
        var fatia = Global(somaDemanda: 20m, erroPbs: 6m, erroMl: 2m);

        fatia.LeituraDoMae.Should()
            .Contain(1.50.ToString("N2")).And.Contain(0.50.ToString("N2"))
            .And.Contain("ML");
    }

    /// <summary>
    /// Erro igual é empate, e empate não é vitória de ninguém — a mesma regra que
    /// <c>SessaoItem.Empate</c> já segue para a sobra.
    /// </summary>
    [Fact]
    public void Leitura_de_erro_empatado_nao_declara_vencedor()
    {
        var fatia = Global(somaDemanda: 20m, erroPbs: 4m, erroMl: 4m);

        fatia.LeituraDoWape.Should().Contain("empate")
            .And.NotContain("portanto");
    }

    /// <summary>
    /// Sem métrica apurada a frase não pode eleger vencedor: "não apurado" e "empate" são
    /// afirmações diferentes, e nenhuma das duas é "o ML ganhou".
    /// </summary>
    [Fact]
    public void Leitura_sem_metrica_apurada_nao_declara_vencedor()
    {
        var fatia = Global(somaDemanda: 0m, erroPbs: 0m, erroMl: 0m, medidos: 0);

        fatia.LeituraDoWape.Should().NotContain("portanto").And.NotContain("empate");
        fatia.LeituraDoMae.Should().NotContain("portanto").And.NotContain("empate");
    }

    private static SessaoFatia Global(
        decimal somaDemanda, decimal erroPbs, decimal erroMl, int medidos = 4) => new(
        Chave: null, Itens: 10, ItensComPrevisaoMl: medidos, ItensComVendaPositiva: medidos,
        SomaDemandaRealDiaria: somaDemanda, SomaErroAbsPbs: erroPbs, SomaErroAbsMl: erroMl,
        VitoriasMl: 1, VitoriasPbs: 3);

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
                new("A", 10, 4, 3, 20m, 2m, 6m, 1, 3),
                new("B", 20, 6, 5, 30m, 3m, 3m, 4, 2),
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
        analise.Global.ItensComVendaPositiva.Should().Be(8,
            "some tambem os itens com venda de verdade; sem isso a manchete nao diz se o "
            + "WAPE global fala de itens que venderam ou de itens que nao venderam");
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
