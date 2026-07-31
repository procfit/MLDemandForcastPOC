using System.Net;
using System.Text;

using CosmosPro.ML.DemandForCast.Web;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

public sealed class ComparisonApiClientTests
{
    private const int RedeDeTeste = 7;

    /// <summary>
    /// Escopo de rede fixo — mesmo padrão de <see cref="ImportsApiClientTests"/>. O que
    /// importa aqui é que o client <b>envie</b> o redeId resolvido pelo IRedeContext, e
    /// nunca um valor vindo de rota, query ou formulário da página.
    /// </summary>
    private sealed class RedeContextFixo(int redeId) : IRedeContext
    {
        public Task<int> GetRedeIdAtualAsync() => Task.FromResult(redeId);
        public Task<Guid> GetUsuarioIdAtualAsync() => Task.FromResult(Guid.Empty);
        public Task<bool> EhPowerUserAsync() => Task.FromResult(false);
        public Task<bool> PodeAcessarAsync(int id) => Task.FromResult(id == redeId);
        public Task SelecionarRedeAsync(int id) => Task.CompletedTask;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static (ComparisonApiClient Client, Func<HttpRequestMessage?> Captured) ClientReturning(
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
        return (new ComparisonApiClient(http, new RedeContextFixo(RedeDeTeste)), () => captured);
    }

    // --- Escopo de rede -------------------------------------------------------

    [Fact]
    public async Task ListAsync_envia_o_escopo_de_rede_resolvido_pelo_IRedeContext()
    {
        var (client, captured) = ClientReturning(HttpStatusCode.OK, "[]");

        await client.ListAsync(take: 25);

        captured()!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/comparison?take=25&redeId={RedeDeTeste}",
            "o escopo de inquilino vem do IRedeContext, nunca de rota/query da página");
    }

    [Fact]
    public async Task GetAsync_envia_o_escopo_de_rede_resolvido_pelo_IRedeContext()
    {
        var id = Guid.NewGuid();
        var json = $$"""
        {"id":"{{id}}","status":"Concluido","dataAgendamento":"2026-07-20T12:00:00+00:00",
         "dataInicioProcessamento":null,"dataConclusao":null,"treinoJobId":"{{Guid.Empty}}",
         "janelaInicio":"2026-07-01","janelaFim":"2026-07-15","tipoCalculo":2,
         "mensagemErro":null,"resultadoJson":null}
        """;
        var (client, captured) = ClientReturning(HttpStatusCode.OK, json);

        var view = await client.GetAsync(id);

        captured()!.RequestUri!.PathAndQuery.Should().Be($"/api/comparison/{id}?redeId={RedeDeTeste}");
        view.Should().NotBeNull();
        view!.TipoCalculo.Should().Be(2);
    }

    [Fact]
    public async Task EnqueueAsync_em_202_retorna_Success_true_e_posta_no_endpoint_escopado()
    {
        var id = Guid.NewGuid();
        var treino = Guid.NewGuid();
        var json = $$"""
        {"id":"{{id}}","status":"Pendente","dataAgendamento":"2026-07-20T12:00:00+00:00",
         "dataInicioProcessamento":null,"dataConclusao":null,"treinoJobId":"{{treino}}",
         "janelaInicio":"2026-07-01","janelaFim":"2026-07-15","tipoCalculo":1,
         "mensagemErro":null,"resultadoJson":null}
        """;
        var (client, captured) = ClientReturning(HttpStatusCode.Accepted, json);

        var r = await client.EnqueueAsync(treino, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 15), 1);

        captured()!.RequestUri!.PathAndQuery.Should().Be($"/api/comparison/run?redeId={RedeDeTeste}");
        r.Success.Should().BeTrue();
        r.Errors.Should().BeNull();
        r.Body!.Id.Should().Be(id);
        r.Body.TipoCalculo.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueAsync_em_400_retorna_os_erros_de_validacao()
    {
        var (client, _) = ClientReturning(
            HttpStatusCode.BadRequest,
            """{"errors":["TipoCalculo deve ser 1 (Emax e Eseg) ou 2 (Dias de Reposição)."]}""");

        var r = await client.EnqueueAsync(Guid.NewGuid(), new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 15), 9);

        r.Success.Should().BeFalse();
        r.Body.Should().BeNull();
        r.Errors.Should().ContainSingle().Which.Should().Contain("TipoCalculo");
    }

    // --- Leitura do ResultadoJson --------------------------------------------

    [Fact]
    public void ParseResultado_de_json_invalido_devolve_null_em_vez_de_resultado_zerado()
    {
        ComparisonApiClient.ParseResultado("{ isto não é json").Should().BeNull();
        ComparisonApiClient.ParseResultado(null).Should().BeNull();
        ComparisonApiClient.ParseResultado("   ").Should().BeNull();
    }

    [Fact]
    public void ParseResultado_le_a_ressalva_e_os_contadores_de_exclusao_separados()
    {
        var r = ComparisonApiClient.ParseResultado(ResultadoJsonForaDoHorizonte)!;

        r.RessalvaTreinoServe.Should().Contain("TREINADO com o preço realizado",
            "a ressalva metodológica viaja com os números e é renderizada junto deles");

        r.ItensDaSugestao.Should().Be(120);
        r.ItensForaCamadaA.Should().Be(11);
        r.ItensForaCamadaAAlemDoHistorico.Should().Be(7);
        r.ItensForaCamadaB.Should().Be(13);
        r.ItensForaCamadaBAlemDoHistorico.Should().Be(5);
        r.ItensForaOrcamentoSkus.Should().Be(24);
        r.TotalExcluido.Should().Be(60);

        // Cada motivo é uma linha própria, com o texto do que significa — a tela nunca
        // os apresenta como um balde só.
        r.Exclusoes.Should().HaveCount(5);
        r.Exclusoes.Select(e => e.Itens).Should().Equal(11, 7, 13, 5, 24);
        r.Exclusoes.Should().OnlyContain(e => e.Significado.Length > 40);
        r.Exclusoes.Select(e => e.Motivo).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ParseResultado_le_a_camada_A_com_a_unidade_rotulada()
    {
        var a = ComparisonApiClient.ParseResultado(ResultadoJsonForaDoHorizonte)!.Previsao!;

        a.ParesAvaliados.Should().Be(40);
        a.ParesDescartados.Should().Be(6);
        a.Erp!.Global!.Wape.Should().Be(0.30);
        a.Ml!.Global!.Wape.Should().Be(0.22);
        a.Vitoria!.TaxaVitoriaMl.Should().BeApproximately(24.0 / 40, 1e-9);

        a.UnidadeTexto.Should().Contain("NÃO é comparável",
            "MAE/RMSE daqui não podem ser lidos ao lado dos do backtest de treinamento");

        // A dimensão em que o ML perde precisa continuar visível — é o que a média
        // global esconde (CLAUDE.md §6).
        var categoriaMl = a.Ml.PorDimensao!["Categoria"];
        var categoriaErp = a.Erp.PorDimensao!["Categoria"];
        categoriaMl["Similar"].Wape.Should().BeGreaterThan(categoriaErp["Similar"].Wape);
    }

    // --- Camada B: o estado que existe hoje ----------------------------------

    [Fact]
    public void Camada_B_fora_do_horizonte_nao_e_utilizavel_e_explica_o_motivo_em_portugues()
    {
        var b = ComparisonApiClient.ParseResultado(ResultadoJsonForaDoHorizonte)!.Decisao!;

        b.Utilidade.Should().Be("ForaDoHorizonteMl");
        b.EhUtilizavel.Should().BeFalse(
            "com cobertura de 15/30 dias do PBS contra horizonte de 7 dias do ML, o esperado hoje é " +
            "nenhum item comparado — e a tela não pode exibir número nenhum desta camada");

        b.ItensComparados.Should().Be(0);
        b.HorizonteMl.Should().Be(7);

        var explicacao = b.ExplicacaoNaoUtilizavel;
        explicacao.Should().NotBeNullOrWhiteSpace(
            "renderizar tabela vazia, zero ou traço aqui leria como 'nenhuma diferença encontrada'");
        explicacao.Should().Contain("não comparou nenhum item");
        explicacao.Should().Contain("horizonte de 7 dia(s)");
        explicacao.Should().Contain("ausência de comparação");
    }

    [Theory]
    [InlineData("ForaDoHorizonteMl")]
    [InlineData("PopulacaoVazia")]
    [InlineData("DescartadoPorRuptura")]
    [InlineData("ReconciliacaoDivergente")]
    [InlineData("SemItensComparaveis")]
    [InlineData("EstadoQueAindaNaoExiste")]
    [InlineData("")]
    [InlineData(null)]
    public void Todo_estado_nao_utilizavel_da_camada_B_tem_explicacao_propria(string? utilidade)
    {
        var b = CamadaB(utilidade);

        b.EhUtilizavel.Should().BeFalse();
        b.ExplicacaoNaoUtilizavel.Should().NotBeNullOrWhiteSpace(
            "nenhum estado pode cair num caminho sem texto — seria uma tela vazia sem motivo");
        b.ExplicacaoNaoUtilizavel.Length.Should().BeGreaterThan(80);
    }

    [Fact]
    public void Camada_B_utilizavel_nao_produz_texto_de_estado()
    {
        var b = CamadaB("Utilizavel");

        b.EhUtilizavel.Should().BeTrue();
        b.ExplicacaoNaoUtilizavel.Should().BeEmpty();
    }

    // --- Portão de reconciliação ---------------------------------------------

    [Fact]
    public void Taxa_de_concordancia_nula_com_populacao_reconciliada_tem_estado_proprio_e_nao_e_100()
    {
        var rec = ComparisonApiClient.ParseResultado(ResultadoJsonForaDoHorizonte)!.Decisao!.Reconciliacao!;

        rec.Itens.Should().Be(30);
        rec.Reconciliados.Should().Be(30);
        rec.ItensComparados.Should().Be(0);
        rec.TaxaConcordancia.Should().BeNull(
            "reconciliar 100% e não comparar nada não é 100% de sucesso — o servidor suprime a taxa");
        rec.AbaixoDoPatamar.Should().BeFalse("não há taxa a comparar com o patamar");

        rec.ExplicacaoTaxaNula.Should().Contain("nenhum deles chegou a ser comparado");
        rec.ExplicacaoTaxaNula.Should().Contain("100%");
    }

    [Fact]
    public void Taxa_de_concordancia_nula_com_populacao_vazia_tem_outro_texto()
    {
        var rec = new ReconciliacaoResumoView(0, 0, 0, 0, 0, 0m, 0m, null, null);

        rec.ExplicacaoTaxaNula.Should().Contain("não recebeu item nenhum");
        rec.ExplicacaoTaxaNula.Should().Contain("Isto não é 100%");
    }

    [Fact]
    public void Concordancia_abaixo_do_patamar_e_sinalizada_global_e_por_curva()
    {
        var curvaRuim = new ReconciliacaoCurvaView("C", 20, 10, 10, 0, 3m, 9m);
        var curvaBoa = new ReconciliacaoCurvaView("A", 20, 20, 0, 0, 0m, 0m);
        var rec = new ReconciliacaoResumoView(40, 30, 10, 0, 30, 3m, 9m, 0.75, null);

        rec.AbaixoDoPatamar.Should().BeTrue("0,75 está abaixo do patamar de apresentabilidade");
        curvaRuim.TaxaConcordancia.Should().Be(0.5);
        curvaRuim.AbaixoDoPatamar.Should().BeTrue();
        curvaBoa.AbaixoDoPatamar.Should().BeFalse();
    }

    /// <summary>
    /// A página afirma que nenhum número de decisão é apresentável enquanto a concordância
    /// não subir. Utilizável e abaixo do patamar é justamente o caso em que ela exibiria uma
    /// tabela de vitórias logo abaixo do próprio aviso que a desautoriza.
    /// </summary>
    [Fact]
    public void Camada_B_utilizavel_com_concordancia_abaixo_do_patamar_nao_tem_numeros_apresentaveis()
    {
        var abaixo = CamadaB("Utilizavel") with
        {
            ItensComparados = 40,
            Reconciliacao = new ReconciliacaoResumoView(100, 62, 38, 0, 40, 2m, 8m, 0.62, null),
        };

        abaixo.EhUtilizavel.Should().BeTrue("o resultado existe e é bem formado");
        abaixo.NumerosApresentaveis.Should().BeFalse(
            "com 0,62 de concordância a diferença medida seria o nosso erro de modelagem da regra do ERP");
    }

    [Fact]
    public void Camada_B_utilizavel_acima_do_patamar_tem_numeros_apresentaveis()
    {
        var acima = CamadaB("Utilizavel") with
        {
            ItensComparados = 40,
            Reconciliacao = new ReconciliacaoResumoView(100, 98, 2, 0, 40, 0m, 1m, 0.98, null),
        };

        acima.NumerosApresentaveis.Should().BeTrue();
    }

    /// <summary>
    /// Taxa nula não retém os números: ela já tem estado próprio na tela, e tratá-la como
    /// "abaixo do patamar" esconderia o resultado por um motivo que não é o dela.
    /// </summary>
    [Fact]
    public void Camada_B_utilizavel_com_taxa_nula_nao_e_retida_pelo_patamar()
    {
        var semTaxa = CamadaB("Utilizavel") with
        {
            Reconciliacao = new ReconciliacaoResumoView(0, 0, 0, 0, 0, 0m, 0m, null, null),
        };

        semTaxa.NumerosApresentaveis.Should().BeTrue();
    }

    [Fact]
    public void Camada_B_nao_utilizavel_nunca_tem_numeros_apresentaveis()
    {
        CamadaB("ForaDoHorizonteMl").NumerosApresentaveis.Should().BeFalse();
    }

    /// <summary>
    /// Reconciliação ausente barra igual: a página avisa logo acima que sem o portão de
    /// validade nada abaixo é interpretável, e exibir a tabela desmentiria o próprio aviso.
    /// </summary>
    [Fact]
    public void Camada_B_sem_reconciliacao_nao_tem_numeros_apresentaveis()
    {
        CamadaB("Utilizavel").NumerosApresentaveis.Should().BeFalse(
            "sem portão de validade não dá para afirmar que modelamos a aritmética do ERP");
    }

    /// <summary>
    /// Sem a lista de itens recusados não há de onde ler o horizonte — a explicação diz que
    /// não sabe, em vez de afirmar um número que ninguém apurou.
    /// </summary>
    [Fact]
    public void Estado_fora_do_horizonte_sem_itens_recusados_nao_inventa_o_numero_de_dias()
    {
        var b = CamadaB("ForaDoHorizonteMl");

        b.HorizonteMl.Should().BeNull();
        b.ExplicacaoNaoUtilizavel.Should().NotContain("de 7 dia(s)",
            "7 era um default embutido na tela; o horizonte tem de vir do resultado");
        b.ExplicacaoNaoUtilizavel.Should().Contain("este resultado não informou");
    }

    // --- Camada C -------------------------------------------------------------

    [Fact]
    public void Camada_C_le_as_figuras_e_marca_base_ponderada_ausente()
    {
        var c = ComparisonApiClient.ParseResultado(ResultadoJsonForaDoHorizonte)!.Intervencao!;

        c.ItensNaPopulacao.Should().Be(120);
        c.NaoPonderado!.FracaoOverride.Should().BeApproximately(30.0 / 120, 1e-9);
        c.Ponderado.Should().BeNull("população sem preço de compra não tem base em R$ a ponderar");
        CamadaCResultado.RessalvaDescritiva.Should().Contain("não avaliação de acurácia");
    }

    // --- Fixtures -------------------------------------------------------------

    private static CamadaBResultado CamadaB(string? utilidade) => new(
        ItensNaPopulacao: 30,
        ItensComparados: 0,
        ItensDescartadosPorRuptura: 0,
        ItensSemPrecoCompra: 0,
        Utilidade: utilidade,
        ItensComFallbackEstoqueSeguranca: 0,
        Reconciliacao: null,
        DetalheReconciliacao: null,
        ForaDoHorizonteMl: null,
        Erp: null,
        Ml: null,
        Vitoria: null,
        VitoriaPorDimensao: null,
        Detalhe: null);

    /// <summary>
    /// Resultado no estado que a POC de fato produz hoje: camada A com pares pontuados e
    /// camada B <c>ForaDoHorizonteMl</c> — reconciliação completa, zero comparações.
    /// </summary>
    private const string ResultadoJsonForaDoHorizonte = """
    {
      "geradoEm": "2026-07-20T12:00:00+00:00",
      "treinoJobId": "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
      "modeloTreinadoAte": "2026-06-30",
      "treinoAte": "2026-06-30",
      "tipoCalculo": 2,
      "janelaInicio": "2026-07-01",
      "janelaFim": "2026-07-15",
      "sugestoes": 3,
      "itensDaSugestao": 120,
      "itensCamadaA": 40,
      "itensForaCamadaA": 11,
      "itensForaCamadaAAlemDoHistorico": 7,
      "itensCamadaB": 30,
      "itensForaCamadaB": 13,
      "itensForaCamadaBAlemDoHistorico": 5,
      "itensForaOrcamentoSkus": 24,
      "ressalvaTreinoServe": "O modelo foi TREINADO com o preço realizado de cada dia e SERVIDO, nesta comparação, com o preço congelado na data da sugestão.",
      "previsao": {
        "paresAvaliados": 40,
        "paresDescartados": 6,
        "unidade": "ErroPorParNaJanela",
        "erp": {
          "nome": "erp",
          "global": { "n": 40, "mae": 2.5, "rmse": 3.5, "wape": 0.30, "mape": 0.42 },
          "porDimensao": {
            "Categoria": {
              "OTC": { "n": 25, "mae": 2.0, "rmse": 3.0, "wape": 0.25, "mape": null },
              "Similar": { "n": 15, "mae": 3.2, "rmse": 4.1, "wape": 0.38, "mape": null }
            }
          }
        },
        "ml": {
          "nome": "lightgbm",
          "global": { "n": 40, "mae": 1.9, "rmse": 2.8, "wape": 0.22, "mape": 0.31 },
          "porDimensao": {
            "Categoria": {
              "OTC": { "n": 25, "mae": 1.4, "rmse": 2.1, "wape": 0.16, "mape": null },
              "Similar": { "n": 15, "mae": 3.9, "rmse": 4.8, "wape": 0.44, "mape": null }
            }
          }
        },
        "vitoria": { "n": 40, "vitoriasMl": 24, "vitoriasErp": 15, "empates": 1 },
        "vitoriaPorDimensao": {
          "Categoria": {
            "OTC": { "n": 25, "vitoriasMl": 20, "vitoriasErp": 5, "empates": 0 },
            "Similar": { "n": 15, "vitoriasMl": 4, "vitoriasErp": 10, "empates": 1 }
          }
        },
        "detalhe": []
      },
      "decisao": {
        "itensNaPopulacao": 30,
        "itensComparados": 0,
        "itensDescartadosPorRuptura": 0,
        "itensSemPrecoCompra": 0,
        "utilidade": "ForaDoHorizonteMl",
        "itensComFallbackEstoqueSeguranca": 0,
        "reconciliacao": {
          "itens": 30,
          "reconciliados": 30,
          "divergentes": 0,
          "bracoMlIndeterminado": 0,
          "itensComparados": 0,
          "divergenciaAbsMedia": 0,
          "divergenciaAbsMaxima": 0,
          "taxaConcordancia": null,
          "porCurva": {
            "A": { "curva": "A", "itens": 30, "reconciliados": 30, "divergentes": 0, "bracoMlIndeterminado": 0, "divergenciaAbsMedia": 0, "divergenciaAbsMaxima": 0 }
          }
        },
        "detalheReconciliacao": [],
        "foraDoHorizonteMl": [
          { "sugestaoId": 1001, "lojaId": 10, "sku": "SKU-1", "diasEstoque": 30, "horizonteMaximoMl": 7, "motivo": "A compra do item cobre 30 dias, mas o braço ML só prevê 7 dia(s) à frente do corte." }
        ],
        "erp": {
          "nome": "erp",
          "unidadesCompradas": 0, "valorComprado": 0, "excessoUnidades": 0, "excessoValor": 0, "faltaUnidades": 0,
          "vendaPerdida": { "unidades": 0, "valor": 0 },
          "posicaoVsVenda": { "n": 0, "mae": 0, "rmse": 0, "wape": 0, "mape": null }
        },
        "ml": {
          "nome": "ml",
          "unidadesCompradas": 0, "valorComprado": 0, "excessoUnidades": 0, "excessoValor": 0, "faltaUnidades": 0,
          "vendaPerdida": { "unidades": 0, "valor": 0 },
          "posicaoVsVenda": { "n": 0, "mae": 0, "rmse": 0, "wape": 0, "mape": null }
        },
        "vitoria": { "n": 0, "vitoriasMl": 0, "vitoriasErp": 0, "empates": 0 },
        "vitoriaPorDimensao": {},
        "detalhe": []
      },
      "intervencao": {
        "itensNaPopulacao": 120,
        "itensSemPreco": 120,
        "naoPonderado": {
          "base": 120, "comDenominador": 118, "comOverride": 30, "vetos": 4, "adicoes": 2,
          "ajustesParaCima": 15, "ajustesParaBaixo": 9,
          "desvioRelativoMedioAbsoluto": 0.21, "desvioRelativoMedioAssinado": 0.04
        },
        "ponderado": null,
        "porCurva": {
          "A": {
            "curva": "A", "itens": 60, "itensSemPreco": 60,
            "naoPonderado": {
              "base": 60, "comDenominador": 60, "comOverride": 20, "vetos": 2, "adicoes": 0,
              "ajustesParaCima": 12, "ajustesParaBaixo": 6,
              "desvioRelativoMedioAbsoluto": 0.25, "desvioRelativoMedioAssinado": 0.08
            },
            "ponderado": null
          }
        }
      }
    }
    """;
}
