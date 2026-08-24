using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CosmosPro.ML.DemandForCast.Web.Services;

// Namespace dos demais clients (ImportsApiClient, TrainingApiClient…) mesmo o arquivo
// morando em Services/: o caminho veio do brief da tarefa, o namespace segue os irmãos.
namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// Fila de comparações contra o ERP (F13) — <c>/api/comparison</c>. Não confundir com
/// <see cref="ComparacoesApiClient"/>, que é a sessão guiada de F14.
///
/// <para>
/// O <c>redeId</c> vem sempre do <see cref="IRedeContext"/>, derivado do usuário
/// autenticado — nunca de rota, query ou formulário da página (ver ImportsApiClient).
/// </para>
/// </summary>
public class ComparisonApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Teto curto para as leituras por trás do poll de 3s da página — mesmo motivo de
    /// <see cref="ComparacoesApiClient"/>: sem ele um tick pendurado bloqueia o guard
    /// de refresh da tela até o Timeout do HttpClient.
    /// </summary>
    private static readonly TimeSpan LeituraTimeout = TimeSpan.FromSeconds(30);

    public async Task<EnqueueComparacaoResult> EnqueueAsync(
        Guid treinoJobId,
        DateOnly janelaInicio,
        DateOnly janelaFim,
        byte tipoCalculo,
        CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var resp = await httpClient.PostAsJsonAsync(
            $"/api/comparison/run?redeId={redeId}",
            new
            {
                TreinoJobId = treinoJobId,
                JanelaInicio = janelaInicio,
                JanelaFim = janelaFim,
                TipoCalculo = tipoCalculo,
            },
            JsonOpts,
            ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            var body = await resp.Content.ReadFromJsonAsync<ComparacaoPbsRunView>(JsonOpts, ct);
            return new EnqueueComparacaoResult(true, body, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(JsonOpts, ct);
            return new EnqueueComparacaoResult(false, null, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var texto = await resp.Content.ReadAsStringAsync(ct);
        return new EnqueueComparacaoResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {texto}"]);
    }

    public async Task<IReadOnlyList<ComparacaoPbsRunView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);
        var r = await httpClient.GetFromJsonAsync<List<ComparacaoPbsRunView>>(
            $"/api/comparison?take={take}&redeId={redeId}", JsonOpts, cts.Token);
        return r ?? [];
    }

    /// <summary>Detalhe de uma execução — é o único caminho que traz o <c>ResultadoJson</c>.</summary>
    public async Task<ComparacaoPbsRunView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);
        var resp = await httpClient.GetAsync($"/api/comparison/{id}?redeId={redeId}", cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ComparacaoPbsRunView>(JsonOpts, cts.Token);
    }

    /// <summary>
    /// Desserializa o <c>ResultadoJson</c> gravado pelo Worker. Devolve <c>null</c> quando
    /// ausente ou ilegível — a página trata isso como "sem resultado", nunca como zero.
    /// </summary>
    public static ComparacaoResultado? ParseResultado(string? resultadoJson)
    {
        if (string.IsNullOrWhiteSpace(resultadoJson)) return null;
        // Catch largo de propósito (mesmo de TrainingApiClient.ParseResultado): isto roda
        // dentro do render da página, e qualquer exceção que escape aqui vira 500 na tela
        // inteira em vez de "sem resultado" numa aba.
        try { return JsonSerializer.Deserialize<ComparacaoResultado>(resultadoJson, JsonOpts); }
        catch { return null; }
    }
}

public sealed record EnqueueComparacaoResult(
    bool Success,
    ComparacaoPbsRunView? Body,
    IReadOnlyList<string>? Errors);

/// <summary>Espelha <c>ComparacaoPbsView</c> da apiservice.</summary>
public sealed record ComparacaoPbsRunView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    Guid TreinoJobId,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    byte TipoCalculo,
    string? MensagemErro,
    string? ResultadoJson);

/// <summary>
/// Rótulos dos dois métodos de cálculo do ERP. São <b>baselines distintos</b>: a tela
/// separa os resultados em abas por método e nunca soma nem promedia entre eles.
/// </summary>
public static class TipoCalculoErp
{
    public const byte EmaxEseg = 1;
    public const byte DiasReposicao = 2;

    public static readonly IReadOnlyList<byte> Todos = [EmaxEseg, DiasReposicao];

    public static string Nome(byte tipo) => tipo switch
    {
        EmaxEseg => "Emax e Eseg",
        DiasReposicao => "Dias de Reposição",
        _ => $"Método {tipo}",
    };

    public static string Descricao(byte tipo) => tipo switch
    {
        EmaxEseg =>
            "O ERP repõe até um nível (estoque máximo), com um componente de estoque de segurança. " +
            "A quantidade sugerida depende do eMax/eSeg que ele gravou.",
        DiasReposicao =>
            "O ERP dimensiona a compra por uma cobertura fixa em dias, sem usar eMax/eSeg.",
        _ => "Método de cálculo não reconhecido por esta tela.",
    };
}

// --- Espelho de ComparacaoOutput (Worker/Comparison/ComparacaoProcessor.cs) ----------
//
// Os campos calculados do lado servidor (TaxaConcordancia, Motivo) são desserializados,
// não recalculados: a regra de supressão da taxa mora no Purchasing e duplicá-la aqui
// faria as duas divergirem em silêncio. Os triviais (frações, taxas de vitória) são
// recalculados para que um JSON de teste não precise repeti-los.

public sealed record ComparacaoResultado(
    DateTimeOffset GeradoEm,
    Guid TreinoJobId,
    DateOnly ModeloTreinadoAte,
    DateOnly? TreinoAte,
    byte TipoCalculo,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    int Sugestoes,
    int ItensDaSugestao,
    int ItensCamadaA,
    int ItensForaCamadaA,
    int ItensForaCamadaAAlemDoHistorico,
    int ItensCamadaB,
    int ItensForaCamadaB,
    int ItensForaCamadaBAlemDoHistorico,
    int ItensForaOrcamentoSkus,
    string? RessalvaTreinoServe,
    CamadaAResultado? Previsao,
    CamadaBResultado? Decisao,
    CamadaCResultado? Intervencao)
{
    /// <summary>
    /// Motivos pelos quais itens do ERP deixaram a população, cada um com o que significa.
    /// São campos separados na origem justamente para não serem lidos como um balde só —
    /// a tela os mostra em linhas próprias, com o texto visível na página.
    /// </summary>
    public IReadOnlyList<ExclusaoPopulacao> Exclusoes =>
    [
        new("Sem série no histórico (camada A)", ItensForaCamadaA,
            "O item não tem linha de feature em todos os dias pontuados da camada A: série curta demais, " +
            "ou loja/SKU sem venda no período. Não é falta de horizonte nem de orçamento."),
        new("Janela além do histórico importado (camada A)", ItensForaCamadaAAlemDoHistorico,
            "A janela pontuada da camada A avança para além do último dia de venda que foi importado. " +
            "Não é problema do item: é o fim dos dados. Cresce sozinho quando a janela de sugestões se " +
            "aproxima do fim do período importado."),
        new("Sem série ou sem eMax (camada B)", ItensForaCamadaB,
            "O item não tem a janela de cobertura completa no histórico, ou é do método \"Emax e Eseg\" " +
            "sem estoque máximo gravado. Itens recusados por horizonte do ML NÃO estão aqui — esses " +
            "aparecem no estado da camada B, com o motivo."),
        new("Janela além do histórico importado (camada B)", ItensForaCamadaBAlemDoHistorico,
            "A cobertura inteira que a compra precisa para ser pontuada avança para além do último dia " +
            "de venda importado. A sugestão está dentro da janela pedida; a venda que a mediria, não."),
        new("Fora do orçamento de SKUs", ItensForaOrcamentoSkus,
            "O SKU não coube no top-N de SKUs recalculado com o corte da sugestão, então não recebeu " +
            "classe ABC nem feature nenhuma. Pode ter série completa e mesmo assim não caber — o motivo " +
            "é orçamento, não falta de dado."),
    ];

    public int TotalExcluido =>
        ItensForaCamadaA + ItensForaCamadaAAlemDoHistorico + ItensForaCamadaB +
        ItensForaCamadaBAlemDoHistorico + ItensForaOrcamentoSkus;
}

/// <param name="Itens">Quantos itens saíram por este motivo.</param>
public sealed record ExclusaoPopulacao(string Motivo, int Itens, string Significado);

// --- Camada A: previsão contra previsão ---------------------------------------------

public sealed record CamadaAResultado(
    int ParesAvaliados,
    int ParesDescartados,
    string? Unidade,
    ArmResultado? Erp,
    ArmResultado? Ml,
    PlacarVitorias? Vitoria,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, PlacarVitorias>>? VitoriaPorDimensao,
    IReadOnlyList<ParComparadoView>? Detalhe)
{
    /// <summary>
    /// Rótulo da unidade em que MAE/RMSE foram apurados. Precisa aparecer junto dos
    /// números: o backtest de F7 traz as MESMAS métricas em outra unidade (erro por dia),
    /// e promediar a janela encolhe a variância — o MAE daqui sai menor para o mesmo
    /// modelo, sem que ele tenha melhorado.
    /// </summary>
    public string UnidadeTexto => Unidade switch
    {
        "ErroPorParNaJanela" =>
            "Um ponto de erro por par (sugestão, loja, SKU), com previsão e verdade promediadas sobre a " +
            "janela avaliada. NÃO é comparável com o MAE/RMSE do backtest de treinamento, que usa um " +
            "ponto de erro por dia: promediar a janela encolhe a variância e reduz o MAE do mesmo modelo.",
        "ErroPorDia" =>
            "Um ponto de erro por (dia, loja, SKU) — a mesma unidade do backtest walk-forward.",
        _ => "Unidade de erro não informada pelo resultado; não compare estes números com os do backtest.",
    };
}

public sealed record ArmResultado(
    string? Nome,
    MetricsDto? Global,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricsDto>>? PorDimensao);

public sealed record PlacarVitorias(int N, int VitoriasMl, int VitoriasErp, int Empates)
{
    public double TaxaVitoriaMl => N == 0 ? 0 : (double)VitoriasMl / N;

    public double TaxaVitoriaErp => N == 0 ? 0 : (double)VitoriasErp / N;
}

public sealed record ParComparadoView(
    long SugestaoId,
    int LojaId,
    string? Sku,
    int DiasAvaliados,
    double DemandaDiaReal,
    double DemandaDiaErp,
    double DemandaDiaMl,
    double ErroAbsErp,
    double ErroAbsMl,
    string? Resultado);

// --- Camada B: decisão contra decisão -----------------------------------------------

public sealed record CamadaBResultado(
    int ItensNaPopulacao,
    int ItensComparados,
    int ItensDescartadosPorRuptura,
    int ItensSemPrecoCompra,
    string? Utilidade,
    int ItensComFallbackEstoqueSeguranca,
    ReconciliacaoResumoView? Reconciliacao,
    IReadOnlyList<ItemReconciliadoView>? DetalheReconciliacao,
    IReadOnlyList<ItemForaDoHorizonteView>? ForaDoHorizonteMl,
    ArmDecisaoView? Erp,
    ArmDecisaoView? Ml,
    PlacarVitorias? Vitoria,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, PlacarVitorias>>? VitoriaPorDimensao,
    IReadOnlyList<DecisaoComparadaView>? Detalhe,
    string? MotivoForaDoHorizonteMl = null)
{
    /// <summary>
    /// Único portão que a UI deve checar antes de exibir qualquer número desta camada.
    /// Zero itens comparados produz um resultado bem formado com os dois braços zerados e
    /// placar (0,0,0,0) — que lido como tabela é indistinguível de "empate".
    /// </summary>
    public bool EhUtilizavel => Utilidade == "Utilizavel";

    /// <summary>
    /// Se os números de decisão podem ser <b>exibidos</b>. Mais estrito que
    /// <see cref="EhUtilizavel"/>: com a concordância de reconciliação abaixo do patamar, o
    /// resultado existe e é bem formado, mas mede o nosso desconhecimento da regra do ERP —
    /// e a página afirma isso em texto. Exibir a tabela mesmo assim contradiria o próprio
    /// aviso que ela imprime logo acima.
    /// <para>
    /// Reconciliação ausente também barra: sem o portão de validade não há como afirmar que
    /// modelamos a aritmética do ERP, e "não sei" não pode render mais que "sei que está
    /// ruim". O Worker sempre a preenche, então isto cobre payload truncado ou de outra versão.
    /// </para>
    /// </summary>
    public bool NumerosApresentaveis => EhUtilizavel && Reconciliacao is { AbaixoDoPatamar: false };

    /// <summary>Horizonte declarado do braço ML, lido dos próprios itens recusados.</summary>
    public int? HorizonteMl => ForaDoHorizonteMl is { Count: > 0 } lista ? lista[0].HorizonteMaximoMl : null;

    /// <summary>
    /// Por que esta camada não comparou nada, em português corrente. Sempre não vazio para
    /// estado não utilizável: a página exibe este texto no lugar da tabela, e jamais um
    /// zero, um traço ou uma grade vazia — que leriam como "nenhuma diferença encontrada".
    /// </summary>
    public string ExplicacaoNaoUtilizavel => Utilidade switch
    {
        "Utilizavel" => "",

        "ForaDoHorizonteMl" =>
            "Esta camada não comparou nenhum item. Todos os itens que reconciliaram exigem previsão para " +
            "mais dias do que o modelo alcança: a cobertura das compras do ERP excede o horizonte " +
            (HorizonteMl is { } dias
                ? $"de {dias} dia(s) do pipeline atual"
                : "do pipeline atual, cujo tamanho este resultado não informou — a lista de itens " +
                  "recusados veio vazia, e inventar um número aqui seria afirmar o que não sabemos") +
            " (cobertura corrente no PBS é de 15 ou 30 dias). " +
            "Isto NÃO é empate entre ERP e ML, nem diferença zero — é ausência de comparação. Enquanto não " +
            "houver previsão multi-horizonte, nenhum número de decisão pode ser lido daqui. É o estado " +
            "esperado hoje, e a camada A (previsão contra previsão) continua válida.",

        "PopulacaoVazia" =>
            "Esta camada não recebeu item nenhum: nenhuma linha da sugestão do ERP chegou ao comparador de " +
            "decisão. Os motivos estão nas exclusões de população acima. Não há o que comparar — não é " +
            "empate nem diferença zero.",

        "DescartadoPorRuptura" =>
            "Todos os itens que reconciliaram e cabiam no horizonte foram descartados pela política de " +
            "ruptura: a janela de venda deles ficou sem nenhum dia válido (estoque zerado). Venda em dia de " +
            "ruptura não mede demanda, então nenhum par foi pontuado — e nenhum par pontuado não é empate.",

        "ReconciliacaoDivergente" =>
            "Nenhum item reconciliou: a aritmética que reproduzimos não chegou ao CompraSugerida que o ERP " +
            "gravou em nenhuma linha. O portão de validade fechou por completo. Qualquer diferença medida " +
            "abaixo dele seria o nosso desconhecimento da regra do ERP, não a qualidade da previsão.",

        "SemItensComparaveis" =>
            "Nenhum item foi comparado, por uma combinação de motivos — parte fora do horizonte do ML, parte " +
            "descartada por ruptura, parte sem reconciliar —, sem que nenhum deles sozinho responda por toda " +
            "a população. Zero itens comparados não é empate.",

        null or "" =>
            "O resultado desta camada não informou o estado de utilidade, então não há como afirmar que os " +
            "números abaixo comparam alguma coisa. Nada desta camada deve ser lido.",

        _ =>
            $"Esta camada reportou o estado \"{Utilidade}\", que esta tela não reconhece. Por precaução, " +
            "nenhum número de decisão é exibido: um estado desconhecido pode significar zero comparações, e " +
            "zero comparações não é empate.",
    };
}

public sealed record ReconciliacaoResumoView(
    int Itens,
    int Reconciliados,
    int Divergentes,
    int BracoMlIndeterminado,
    int ItensComparados,
    decimal DivergenciaAbsMedia,
    decimal DivergenciaAbsMaxima,
    double? TaxaConcordancia,
    IReadOnlyDictionary<string, ReconciliacaoCurvaView>? PorCurva)
{
    /// <summary>
    /// Por que a taxa está nula. <b>Nulo não é 100%</b>: são dois casos distintos e a
    /// página renderiza este texto em vez de um número ou de um traço.
    /// </summary>
    public string ExplicacaoTaxaNula => Itens == 0
        ? "Nada foi reconciliado: a camada de decisão não recebeu item nenhum, então não existe taxa de " +
          "concordância a apresentar. Isto não é 100% — é a ausência do portão."
        : $"A taxa é omitida de propósito. A fórmula reproduziu o ERP nos {Itens} itens da população, mas " +
          "nenhum deles chegou a ser comparado (todos caíram por horizonte ou por ruptura). Exibir 100% " +
          "aqui leria como comparação bem-sucedida onde não houve comparação nenhuma.";

    /// <summary>
    /// Patamar abaixo do qual nenhum número da camada B é apresentável — uma taxa baixa
    /// significa "não modelamos o ERP", nunca "o ML ganhou".
    /// </summary>
    /// <summary>
    /// Espelha <c>Purchasing.Comparison.Reconciliacao.PatamarAceitavel</c>, que é quem decide:
    /// o projeto Web não referencia Purchasing e redeclara as views. Mudar um sem o outro faz a
    /// tela pintar de vermelho um resultado que o domínio considerou apresentável, ou o oposto.
    /// </summary>
    public const double PatamarAceitavel = 0.95;

    public bool AbaixoDoPatamar => TaxaConcordancia is { } t && t < PatamarAceitavel;
}

public sealed record ReconciliacaoCurvaView(
    string? Curva,
    int Itens,
    int Reconciliados,
    int Divergentes,
    int BracoMlIndeterminado,
    decimal DivergenciaAbsMedia,
    decimal DivergenciaAbsMaxima)
{
    public double TaxaConcordancia => Itens == 0 ? 0 : (double)Reconciliados / Itens;

    /// <summary>
    /// Curva inteira divergindo enquanto a taxa global parece alta é o sintoma de
    /// sobrevivência seletiva que a média global esconde (CLAUDE.md §6).
    /// </summary>
    public bool AbaixoDoPatamar => Itens > 0 && TaxaConcordancia < ReconciliacaoResumoView.PatamarAceitavel;
}

public sealed record ItemReconciliadoView(
    long SugestaoId,
    int LojaId,
    string? Sku,
    string? Curva,
    string? Status,
    decimal CompraSugeridaErp,
    decimal CompraRecalculada,
    decimal Divergencia,
    decimal DiferencaAssinada,
    decimal? FatorEmbalagem);

/// <summary>
/// Item que o braço ML não alcança. Sem o motivo por item de propósito: ele é o mesmo para
/// a lista inteira e chega uma vez só, em
/// <see cref="CamadaBResultado.MotivoForaDoHorizonteMl"/>.
/// </summary>
public sealed record ItemForaDoHorizonteView(
    long SugestaoId,
    int LojaId,
    string? Sku,
    short DiasEstoque,
    int HorizonteMaximoMl);

public sealed record ArmDecisaoView(
    string? Nome,
    decimal UnidadesCompradas,
    decimal ValorComprado,
    decimal ExcessoUnidades,
    decimal ExcessoValor,
    decimal FaltaUnidades,
    VendaPerdidaView? VendaPerdida,
    MetricsDto? PosicaoVsVenda);

public sealed record VendaPerdidaView(decimal Unidades, decimal Valor);

public sealed record DecisaoComparadaView(
    long SugestaoId,
    int LojaId,
    string? Sku,
    string? Curva,
    int DiasAvaliados,
    decimal VendaRealJanela,
    decimal DemandaDiaErp,
    decimal DemandaDiaMl,
    decimal CompraErp,
    decimal CompraMl,
    decimal ExcessoErp,
    decimal ExcessoMl,
    decimal FaltaErp,
    decimal FaltaMl,
    string? Resultado);

// --- Camada C: intervenção humana ---------------------------------------------------

public sealed record CamadaCResultado(
    int ItensNaPopulacao,
    int ItensSemPreco,
    OverrideFigurasView? NaoPonderado,
    OverrideFigurasView? Ponderado,
    IReadOnlyDictionary<string, OverrideCurvaView>? PorCurva)
{
    /// <summary>
    /// Aviso obrigatório ao lado dos números: um override mede que o comprador discordou
    /// do ERP, não quem estava certo.
    /// </summary>
    public const string RessalvaDescritiva =
        "Isto é estatística descritiva, não avaliação de acurácia. Um override não prova que o ERP errou — " +
        "pode ser o comprador que errou, ou informação que nem o ERP nem nenhum modelo enxergariam (um acordo " +
        "pontual de fornecedor, um concorrente fechando, um surto local). Estes números medem o tamanho da " +
        "intervenção humana no processo hoje, e nada sobre a qualidade da previsão de ninguém.";
}

public sealed record OverrideFigurasView(
    decimal Base,
    decimal ComDenominador,
    decimal ComOverride,
    decimal Vetos,
    decimal Adicoes,
    decimal AjustesParaCima,
    decimal AjustesParaBaixo,
    double? DesvioRelativoMedioAbsoluto,
    double? DesvioRelativoMedioAssinado)
{
    public double? FracaoOverride => Base == 0m ? null : (double)(ComOverride / Base);

    public double? FracaoVetos => Base == 0m ? null : (double)(Vetos / Base);

    public double? FracaoAdicoes => Base == 0m ? null : (double)(Adicoes / Base);

    public double? FracaoAjustesParaCima => Base == 0m ? null : (double)(AjustesParaCima / Base);

    public double? FracaoAjustesParaBaixo => Base == 0m ? null : (double)(AjustesParaBaixo / Base);
}

public sealed record OverrideCurvaView(
    string? Curva,
    int Itens,
    int ItensSemPreco,
    OverrideFigurasView? NaoPonderado,
    OverrideFigurasView? Ponderado);
