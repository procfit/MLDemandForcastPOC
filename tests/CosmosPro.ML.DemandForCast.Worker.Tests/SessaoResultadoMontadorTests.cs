using CosmosPro.ML.DemandForCast.Forecasting.Comparison;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// O que a materialização grava para o comprador ler um mês depois, quando o Stage que
/// originou os números já foi apagado por outro import.
///
/// <para>
/// <b>O defeito que estes testes existem para impedir é uma linha bem formada que afirma o
/// que ninguém calculou.</b> A cobertura corrente do PBS é de 15 a 30 dias e o pipeline
/// prevê 7, então hoje o normal é o braço de ML <b>não</b> ter decisão — e um zero gravado
/// nessa coluna diz ao comprador "o ML mandaria não comprar nada", que é o oposto de "não
/// foi possível calcular". Nada além destes valores denunciaria a troca: a tela renderiza,
/// a tabela ordena e o número parece uma medição.
/// </para>
/// </summary>
public sealed class SessaoResultadoMontadorTests
{
    private static readonly Guid SessaoId = Guid.Parse("0198a0f0-0000-7000-8000-00000000ff01");
    private static readonly Guid ComparacaoPbsId = Guid.Parse("0198a0f0-0000-7000-8000-00000000ff02");
    private static readonly DateTimeOffset Agora = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime SugestaoDataHora = new(2026, 7, 1, 9, 30, 0);

    private const long SugestaoId = 7401;
    private const int LojaId = 501;
    private const string Sku = "MAT-A";
    private const string SkuB = "MAT-B";

    /// <summary>
    /// O caso real de hoje: a camada B recusou o item por horizonte, então não existe compra
    /// do ML para este par. Nulo e zero são afirmações opostas nesta coluna, e é a coluna que
    /// a manchete usa para dizer se o ML teria comprado melhor.
    /// </summary>
    [Fact]
    public void Item_sem_decisao_do_ml_grava_nulo_nas_colunas_do_braco_de_ml()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m)],
            decisao: Decisao(UtilidadeComparacao.ForaDoHorizonteMl));

        var linha = materializacao.Itens.Single();

        linha.CompraSugeridaMl.Should().BeNull(
            "zero aqui diria ao comprador que o ML mandaria não comprar nada, e ninguém calculou isso");
        linha.SobraMlUnidades.Should().BeNull("sem compra do ML não existe posição contrafactual a comparar");
        linha.SobraMlValor.Should().BeNull();

        linha.CompraSugeridaPbs.Should().Be(30m, "o braço do ERP é conhecido para toda a população");
    }

    /// <summary>
    /// O par do teste acima, e o que impede alguém de "simplificar" o nulo para zero: aqui a
    /// camada B <b>decidiu</b> não comprar nada, e a coluna tem de sair zero. Com os dois
    /// testes verdes, nulo e zero não podem ser colapsados num valor só.
    /// </summary>
    [Fact]
    public void Decisao_do_ml_igual_a_zero_grava_zero_e_nao_nulo()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m)],
            decisao: Decisao(UtilidadeComparacao.Utilizavel, [Par(compraMl: 0m)]));

        var linha = materializacao.Itens.Single();

        linha.CompraSugeridaMl.Should().Be(0m,
            "o ML foi consultado e disse para não comprar — decisão medida, não ausência");
        linha.SobraMlUnidades.Should().NotBeNull("com decisão do ML a sobra dele é calculável");
    }

    /// <summary>
    /// A sobra do braço do ERP é a conta do <see cref="SobraCalculator"/>, não uma cópia dela:
    /// pedidos pendentes entram na posição (mesma regra da camada B) e o valor sai a duas
    /// casas.
    /// </summary>
    [Fact]
    public void Sobra_do_braco_pbs_bate_com_o_SobraCalculator()
    {
        var esperada = SobraCalculator.Calcular(
            comprado: 30m, estoqueInicial: 10m, pedidosPendentes: 5m, vendido: 40m, precoCompra: 3.5m);

        var materializacao = Montar(
            populacao: [Linha(vendido: 40m)],
            decisao: Decisao(UtilidadeComparacao.ForaDoHorizonteMl));

        var linha = materializacao.Itens.Single();

        linha.SobraPbsUnidades.Should().Be(esperada.Unidades);
        linha.SobraPbsValor.Should().Be(esperada.Valor);
        linha.SobraPbsUnidades.Should().Be(5m, "30 comprados + 10 em estoque + 5 pendentes − 40 vendidos");
        linha.SobraPbsValor.Should().Be(17.5m);
    }

    /// <summary>
    /// Camada B vazia não pode virar tela em branco: o agregado sai legível, com o braço do
    /// ERP inteiro e o motivo em português de comprador no lugar do confronto.
    /// </summary>
    [Fact]
    public void Sem_nenhuma_decisao_do_ml_o_agregado_diz_por_que_em_vez_de_ficar_vazio()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m)],
            decisao: Decisao(UtilidadeComparacao.ForaDoHorizonteMl));

        var resultado = materializacao.Resultado;

        resultado.Confronto.Should().BeNull("nenhum item teve os dois braços");
        resultado.ItensComDecisaoMl.Should().Be(0);
        resultado.UtilidadeDecisaoMl.Should().Be(UtilidadeComparacao.ForaDoHorizonteMl);

        resultado.MotivoMlIndisponivel.Should().NotBeNullOrWhiteSpace();
        resultado.MotivoMlIndisponivel.Should().Contain("prever",
            "quem lê é comprador de farmácia: a frase tem de dizer o que faltou, não o nome do enum");

        resultado.Pbs.CompraUnidades.Should().Be(30m, "o desfecho do ERP vale por si, sem contraparte");
        resultado.Pbs.SobraUnidades.Should().Be(5m);
        resultado.ItensAvaliados.Should().Be(1);
    }

    /// <summary>
    /// O confronto soma os dois braços sobre <b>os mesmos</b> itens. Com um item decidido e
    /// outro não, somar o ERP sobre os dois e o ML sobre um faria o ML parecer melhor por ter
    /// sido medido em menos linhas.
    /// </summary>
    [Fact]
    public void Confronto_soma_os_dois_bracos_sobre_exatamente_os_itens_comparaveis()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m), Linha(sku: SkuB, vendido: 0m)],
            decisao: Decisao(UtilidadeComparacao.Utilizavel, [Par(compraMl: 20m)]));

        var resultado = materializacao.Resultado;

        resultado.ItensAvaliados.Should().Be(2);
        resultado.Confronto.Should().NotBeNull();
        resultado.Confronto!.Itens.Should().Be(1);
        resultado.Confronto.Pbs.CompraUnidades.Should().Be(30m,
            "o braço do ERP no confronto só pode contar o item que o ML também decidiu");
        resultado.Confronto.Ml.CompraUnidades.Should().Be(20m);

        resultado.Pbs.CompraUnidades.Should().Be(60m, "fora do confronto, o ERP soma a população inteira");
        resultado.MotivoMlIndisponivel.Should().BeNull("houve confronto — não há ausência a explicar");
    }

    /// <summary>
    /// As duas camadas são independentes: a A pontua uma taxa dentro dos 7 dias que o
    /// pipeline alcança e costuma existir exatamente onde a decisão da B não existe. Este é o
    /// estado normal de hoje, e a materialização precisa preservá-lo em vez de zerar os dois
    /// lados juntos.
    /// </summary>
    [Fact]
    public void Previsao_da_camada_a_sobrevive_mesmo_sem_decisao_da_camada_b()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m)],
            previsao: Previsao([Pares(demandaDiaMl: 7.25, demandaDiaReal: 8.0)]),
            decisao: Decisao(UtilidadeComparacao.ForaDoHorizonteMl));

        var linha = materializacao.Itens.Single();

        linha.DemandaDiaMl.Should().Be(7.25m);
        linha.DemandaDiaReal.Should().Be(8m);
        linha.CompraSugeridaMl.Should().BeNull();
        materializacao.Resultado.ItensComPrevisaoMl.Should().Be(1);
    }

    /// <summary>
    /// Item fora da camada A: as duas taxas dela saem nulas, e a do ERP — que vem do Stage e
    /// não da camada — continua gravada. Zero em <c>DemandaDiaReal</c> afirmaria que o item
    /// não vendeu nada por dia, o que é medição e não ausência.
    /// </summary>
    [Fact]
    public void Item_fora_da_camada_a_nao_inventa_demanda_real_nem_previsao()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m)],
            previsao: Previsao([]),
            decisao: Decisao(UtilidadeComparacao.ForaDoHorizonteMl));

        var linha = materializacao.Itens.Single();

        linha.DemandaDiaMl.Should().BeNull();
        linha.DemandaDiaReal.Should().BeNull();
        linha.DemandaDiaPbs.Should().Be(6m, "a previsão do próprio ERP está na linha da sugestão");
        linha.VendidoNaJanela.Should().Be(40m, "a venda da cobertura não depende da camada A");
        materializacao.Resultado.ItensComPrevisaoMl.Should().Be(0);
    }

    /// <summary>
    /// Multi-inquilino: toda linha nasce amarrada à sessão que a materializou, e a população é
    /// exatamente a que veio do Stage daquela rede — nem uma linha além.
    /// </summary>
    [Fact]
    public void Toda_linha_carrega_a_sessao_e_a_populacao_nao_e_alargada()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m), Linha(sku: SkuB, vendido: 1m)],
            // A camada A traz um par que NÃO está na população: pode acontecer se a janela do
            // job e a da materialização divergirem, e nesse caso a linha extra não pode entrar.
            previsao: Previsao([Pares(sku: "MAT-INTRUSO")]),
            decisao: Decisao(UtilidadeComparacao.ForaDoHorizonteMl));

        materializacao.Itens.Should().OnlyContain(i => i.SessaoId == SessaoId);
        materializacao.Itens.Select(i => i.Sku).Should().BeEquivalentTo([Sku, SkuB]);
    }

    /// <summary>
    /// O aviso "N itens sem cadastro" nasce no manifesto do ZIP e morreria no log sem esta
    /// travessia — o diretório do import é apagado no fim dele, e a materialização acontece
    /// três fases depois.
    /// </summary>
    [Fact]
    public void SkusSemCadastro_atravessa_para_o_agregado()
    {
        var comAviso = Montar(populacao: [Linha(vendido: 1m)], skusSemCadastro: 3);
        comAviso.Resultado.SkusSemCadastro.Should().Be(3);

        var semDeclaracao = Montar(populacao: [Linha(vendido: 1m)], skusSemCadastro: null);
        semDeclaracao.Resultado.SkusSemCadastro.Should().BeNull(
            "zero afirmaria que nenhum item ficou de fora, e o envio não declarou isso");
    }

    /// <summary>
    /// Ruptura observada vem com a cobertura do dado ao lado: cobertura sem snapshot nenhum é
    /// "não sabemos", não "não houve falta".
    /// </summary>
    [Fact]
    public void Ruptura_observada_reporta_a_cobertura_do_snapshot_junto()
    {
        var materializacao = Montar(populacao:
        [
            Linha(vendido: 40m, diasSemEstoque: 2, diasComSnapshot: 5),
            Linha(sku: SkuB, vendido: 0m, diasSemEstoque: 0, diasComSnapshot: 0),
        ]);

        var ruptura = materializacao.Resultado.Ruptura;

        ruptura.ItensComDiaSemEstoque.Should().Be(1);
        ruptura.DiasSemEstoque.Should().Be(2);
        ruptura.DiasComSnapshot.Should().Be(5);
        ruptura.DiasNaJanela.Should().Be(10, "dois itens com cobertura de 5 dias cada");
    }

    /// <summary>
    /// Cobertura que passa do fim do histórico importado sai contada: nela a venda da janela
    /// está subcontada e a sobra, inflada — e sem o contador a manchete afirmaria capital
    /// parado que talvez tenha vendido depois.
    /// </summary>
    [Fact]
    public void Janela_alem_do_historico_importado_sai_contada()
    {
        var materializacao = Montar(populacao:
        [
            Linha(vendido: 40m),
            Linha(sku: SkuB, vendido: 3m, alemDoHistorico: true),
        ]);

        materializacao.Resultado.ItensComJanelaAlemDoHistorico.Should().Be(1);
    }

    /// <summary>
    /// Cobertura além do histórico marca a <b>linha</b>, e não só o agregado: é o comprador
    /// que ordena e pagina esta tabela conferindo item a item, e uma linha com a janela
    /// truncada não é comparável com uma linha inteira. A marcação também não pode ser
    /// acrescentada depois — <c>DiasEstoque</c> e a última venda importada morrem no próximo
    /// import.
    /// </summary>
    [Fact]
    public void Janela_alem_do_historico_marca_a_linha_e_nao_so_a_manchete()
    {
        var materializacao = Montar(populacao:
        [
            Linha(vendido: 40m),
            Linha(sku: SkuB, vendido: 3m, alemDoHistorico: true),
        ]);

        materializacao.Itens.Single(i => i.Sku == Sku).JanelaAlemDoHistorico.Should().BeFalse(
            "a cobertura deste item termina dentro do histórico importado");
        materializacao.Itens.Single(i => i.Sku == SkuB).JanelaAlemDoHistorico.Should().BeTrue(
            "sem a marca na linha, quem lê a tabela não distingue a venda subcontada da completa");
    }

    /// <summary>
    /// Item sem <c>PrecoCompra</c> entra nos agregados em R$ com zero, e é isso que puxa a
    /// manchete para baixo. Sem esta contagem a queda é invisível: a tela lidera com um valor
    /// em reais que parece completo — o mesmo defeito das colunas de ML, uma camada acima.
    /// </summary>
    [Fact]
    public void Itens_sem_preco_de_compra_saem_contados_na_manchete()
    {
        var comFalta = Montar(populacao:
        [
            Linha(vendido: 40m),
            Linha(sku: SkuB, vendido: 3m, precoCompra: null),
        ]);

        comFalta.Resultado.ItensSemPrecoCompra.Should().Be(1,
            "a manchete em R$ está subestimada e precisa poder dizer por quê");
        comFalta.Resultado.Pbs.SobraValor.Should().Be(17.5m,
            "o agregado continua somando o item sem preço como zero — é o contador que o qualifica");

        var completa = Montar(populacao: [Linha(vendido: 40m), Linha(sku: SkuB, vendido: 3m)]);

        completa.Resultado.ItensSemPrecoCompra.Should().Be(0,
            "com todos os itens precificados a figura em reais está inteira");
    }

    /// <summary>
    /// Na <b>linha</b> a escolha é a oposta à do agregado: sobra em reais de item sem preço
    /// sai nula. Zero afirmaria "esta compra não deixou capital parado" sobre o item com 5
    /// unidades encalhadas — e é por esta coluna que o comprador ordena a tabela para achar o
    /// pior item, onde os sem preço iriam para o fim da fila como se fossem os melhores.
    /// </summary>
    [Fact]
    public void Sobra_em_reais_do_item_sem_preco_sai_nula_na_linha()
    {
        var materializacao = Montar(
            populacao: [Linha(vendido: 40m, precoCompra: null)],
            decisao: Decisao(UtilidadeComparacao.Utilizavel, [Par(compraMl: 20m)]));

        var linha = materializacao.Itens.Single();

        linha.SobraPbsUnidades.Should().Be(5m, "as unidades sobraram e são conhecidas");
        linha.SobraPbsValor.Should().BeNull("o valor delas é desconhecido, não zero");
        linha.SobraMlValor.Should().BeNull("mesma ausência no braço de ML, que também depende do preço");
        linha.SobraMlUnidades.Should().Be(0m,
            "o braço de ML existe nesta linha: a ausência é só do preço, e ela não pode apagar a decisão");
    }

    /// <summary>
    /// Curva vazia no Stage é ausência de rótulo, não uma curva chamada "": a coluna da linha
    /// sai <b>nula</b>. Quem batiza o grupo é o recorte por curva de
    /// <c>GET /api/comparacoes/{id}/analise</c> ("sem curva"), única versão desse corte — o
    /// resultado materializado não guarda um segundo.
    /// </summary>
    [Fact]
    public void Item_sem_curva_nao_inventa_rotulo()
    {
        var materializacao = Montar(populacao: [Linha(vendido: 1m, curva: "")]);

        materializacao.Itens.Single().Curva.Should().BeNull();
    }

    // --- Arranjo --------------------------------------------------------------

    private static Materializacao Montar(
        IReadOnlyList<ItemDoStage> populacao,
        ComparisonResult? previsao = null,
        DecisionComparisonResult? decisao = null,
        int? skusSemCadastro = 0)
        => SessaoResultadoMontador.Montar(
            sessaoId: SessaoId,
            skusSemCadastro: skusSemCadastro,
            comparacaoPbsId: ComparacaoPbsId,
            sugestaoDataHora: SugestaoDataHora,
            comparacao: Saida(
                previsao ?? Previsao([Pares()]),
                decisao ?? Decisao(UtilidadeComparacao.ForaDoHorizonteMl)),
            populacao: populacao,
            agora: Agora);

    /// <summary>
    /// Linha da sugestão do ERP com os números do <see cref="Sobra_do_braco_pbs_bate_com_o_SobraCalculator"/>:
    /// 30 comprados, 10 em estoque, 5 pendentes, R$ 3,50 de custo, cobertura de 5 dias.
    /// </summary>
    private static ItemDoStage Linha(
        string sku = Sku,
        decimal vendido = 0m,
        string curva = "A",
        int diasSemEstoque = 0,
        int diasComSnapshot = 5,
        bool alemDoHistorico = false,
        decimal? precoCompra = 3.5m)
        => new(
            Item: new SugestaoItemStage(
                SugestaoId: SugestaoId,
                LojaId: LojaId,
                Sku: sku,
                Curva: curva,
                DemandaDia: 6m,
                EstoqueSaldo: 10m,
                EstoqueSeguranca: null,
                EstoqueMaximo: null,
                DiasEstoque: 5,
                PedidosPendentes: 5m,
                CompraSugerida: 30m,
                CompraAutorizada: 30m,
                PrecoCompra: precoCompra,
                FatorEmbalagem: null,
                Falteiro: false),
            NomeProduto: $"Produto {sku}",
            VendidoNaJanela: vendido,
            DiasSemEstoque: diasSemEstoque,
            DiasComSnapshot: diasComSnapshot,
            JanelaAlemDoHistorico: alemDoHistorico);

    private static ParComparado Pares(
        string sku = Sku, double demandaDiaMl = 7.0, double demandaDiaReal = 8.0)
        => new(
            SugestaoId: SugestaoId,
            LojaId: LojaId,
            Sku: sku,
            DiasAvaliados: 5,
            DemandaDiaReal: demandaDiaReal,
            DemandaDiaErp: 6.0,
            DemandaDiaMl: demandaDiaMl,
            ErroAbsErp: 2.0,
            ErroAbsMl: 0.75,
            Resultado: ResultadoPar.VitoriaMl);

    private static DecisaoComparada Par(string sku = Sku, decimal compraMl = 20m)
        => new(
            SugestaoId: SugestaoId,
            LojaId: LojaId,
            Sku: sku,
            Curva: "A",
            DiasAvaliados: 5,
            VendaRealJanela: 40m,
            DemandaDiaErp: 6m,
            DemandaDiaMl: 7m,
            CompraErp: 30m,
            CompraMl: compraMl,
            ExcessoErp: 5m,
            ExcessoMl: 0m,
            FaltaErp: 0m,
            FaltaMl: 5m,
            Resultado: ResultadoPar.VitoriaErp);

    private static ComparisonResult Previsao(IReadOnlyList<ParComparado> detalhe)
    {
        var metricas = new ForecastMetrics(detalhe.Count, 1.0, 1.5, 0.2, 0.25);
        var braco = new ArmResult(
            "erp", metricas, new Dictionary<string, IReadOnlyDictionary<string, ForecastMetrics>>());

        return new ComparisonResult(
            ParesAvaliados: detalhe.Count,
            ParesDescartados: 0,
            Unidade: UnidadeMetrica.ErroPorParNaJanela,
            Erp: braco,
            Ml: braco with { Nome = "ml" },
            Vitoria: new WinRate(detalhe.Count, detalhe.Count, 0, 0),
            VitoriaPorDimensao: new Dictionary<string, IReadOnlyDictionary<string, WinRate>>(),
            Detalhe: detalhe);
    }

    private static DecisionComparisonResult Decisao(
        UtilidadeComparacao utilidade, IReadOnlyList<DecisaoComparada>? detalhe = null)
    {
        detalhe ??= [];
        var metricas = new ForecastMetrics(detalhe.Count, 1.0, 1.5, 0.2, 0.25);
        var braco = new ArmDecisionResult(
            "erp", 0m, 0m, 0m, 0m, 0m, new VendaPerdidaIlustrativa(0m, 0m), metricas);

        return new DecisionComparisonResult(
            ItensNaPopulacao: 1,
            ItensComparados: detalhe.Count,
            ItensDescartadosPorRuptura: 0,
            ItensSemPrecoCompra: 0,
            Utilidade: utilidade,
            ItensComFallbackEstoqueSeguranca: 0,
            Reconciliacao: new ReconciliacaoResumo(
                1, 1, 0, 0, detalhe.Count, 0m, 0m,
                new Dictionary<string, ReconciliacaoPorCurva>()),
            DetalheReconciliacao: [],
            ForaDoHorizonteMl: [],
            Erp: braco,
            Ml: braco with { Nome = "ml" },
            Vitoria: new WinRate(detalhe.Count, 0, detalhe.Count, 0),
            VitoriaPorDimensao: new Dictionary<string, IReadOnlyDictionary<string, WinRate>>(),
            Detalhe: detalhe);
    }

    private static ComparacaoOutput Saida(ComparisonResult previsao, DecisionComparisonResult decisao)
    {
        var figuras = new HumanOverrideFigures(0m, 0m, 0m, 0m, 0m, 0m, 0m, null, null);

        return new ComparacaoOutput(
            GeradoEm: Agora,
            TreinoJobId: Guid.Parse("0198a0f0-0000-7000-8000-00000000ff03"),
            ModeloTreinadoAte: new DateOnly(2026, 6, 30),
            TreinoAte: new DateOnly(2026, 7, 1),
            TipoCalculo: 2,
            JanelaInicio: new DateOnly(2026, 7, 1),
            JanelaFim: new DateOnly(2026, 7, 1),
            Sugestoes: 1,
            ItensDaSugestao: 1,
            ItensCamadaA: previsao.ParesAvaliados,
            ItensForaCamadaA: 0,
            ItensForaCamadaAAlemDoHistorico: 0,
            ItensCamadaB: decisao.ItensComparados,
            ItensForaCamadaB: 0,
            ItensForaCamadaBAlemDoHistorico: 0,
            ItensForaOrcamentoSkus: 0,
            RessalvaTreinoServe: ComparacaoOutput.RessalvaPadraoTreinoServe,
            Previsao: previsao,
            Decisao: decisao,
            Intervencao: new HumanOverrideResult(
                1, 0, figuras, null, new Dictionary<string, HumanOverrideResumoCurva>()));
    }
}
