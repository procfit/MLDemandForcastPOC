using CosmosPro.ML.DemandForCast.Engine.Entities;

namespace CosmosPro.ML.DemandForCast.Worker.Sessoes;

/// <summary>
/// Retrato de uma sessão em fase intermediária, tal como o claim do
/// <see cref="SessaoWorker"/> a lê. Só os campos que a decisão de avanço e a criação do job
/// seguinte consomem — é a lista que o <c>OUTPUT</c> do claim tem de devolver por inteiro,
/// porque o claim é a única leitura da linha.
/// </summary>
internal sealed record SessaoEmAndamento(
    Guid Id,
    int RedeId,
    SessaoStatus Status,
    Guid? CargaStageId,
    Guid? TreinoJobId,
    Guid? ComparacaoPbsId,
    DateTime? SugestaoDataHora,
    byte? SugestaoTipoCalculo);

/// <summary>
/// O que o import deixou no Stage a respeito da sugestão a que a sessão está ancorada, lido
/// com o <b>mesmo recorte</b> (rede, método, dia) que a comparação vai usar.
/// </summary>
/// <param name="Cabecalhos">
/// Sugestões de compra que o recorte seleciona. Tem de ser exatamente uma: a sessão é
/// ancorada a UMA sugestão e a comparação agrega sem separar por sugestão.
/// </param>
/// <param name="SkusDistintos">SKUs distintos avaliados — dimensiona o orçamento do treino.</param>
internal readonly record struct SugestaoNoStage(int Cabecalhos, int SkusDistintos);

/// <summary>
/// Traduz a declaração da sugestão gravada na sessão nos parâmetros dos dois jobs que a
/// sessão cria — ou no motivo pelo qual ela não pode seguir.
///
/// <para>
/// Puro e sem I/O pelo mesmo motivo do <see cref="SessaoAvancador"/>: os dois valores
/// derivados aqui não dão erro quando estão errados. Um corte deslocado em um dia produz um
/// treino que roda liso e uma comparação recusada três fases depois; uma janela larga demais
/// produz uma comparação que roda e mede a sugestão errada.
/// </para>
/// </summary>
internal static class SessaoJobs
{
    /// <summary>
    /// Piso do orçamento de SKUs do treino — o default histórico de <c>/api/training/run</c>.
    ///
    /// <para>
    /// Não é redundante com uma sugestão pequena: o orçamento seleciona os SKUs de
    /// <b>maior volume da rede</b> (<c>StageObservationLoader</c>), não os SKUs da sugestão.
    /// Pedir exatamente 3 porque a sugestão tem 3 itens escolheria os 3 mais vendidos do
    /// catálogo, que podem não ser os da sugestão — e a comparação sairia vazia por orçamento.
    /// O piso mantém a vizinhança em volta deles.
    /// </para>
    /// </summary>
    public const int PisoDeSkusDoTreino = 80;

    /// <summary>
    /// Teto do orçamento de SKUs do treino.
    ///
    /// <para>
    /// <b>Limite técnico duro primeiro:</b> <c>StageObservationLoader</c> monta
    /// <c>Sku IN (@s0, …, @sN)</c> com um parâmetro por SKU, e o SQL Server aceita no máximo
    /// 2100 parâmetros por comando. Passar de ~2000 não degrada, <b>quebra</b> — e quebraria no
    /// treino, longe de quem escolheu o número. Mil deixa o comando na metade do limite,
    /// com espaço para o corte de data e para uma coluna nova no futuro.
    /// </para>
    ///
    /// <para>
    /// <b>Trade-off, dito por inteiro:</b> o tempo de treino cresce com SKUs × lojas × dias, e
    /// o backtest walk-forward reajusta três engines a cada um dos 4 folds. Mil é 12× o piso e
    /// não foi medido em dado real (Retiro) — o que impede o pior caso de virar espera infinita
    /// é <see cref="ComparacaoSessao.LimiteDeFaseSemProgresso"/>, não este número. Numa sugestão
    /// com mais de mil SKUs distintos a comparação passa a medir a <b>fatia de maior volume</b>
    /// da sugestão, e o resto sai contado em <c>ComparacaoOutput.ItensForaOrcamentoSkus</c>:
    /// número honesto e visível, não silêncio. Subir o teto exige medir o treino com dado real
    /// primeiro.
    /// </para>
    /// </summary>
    public const int TetoDeSkusDoTreino = 1000;

    /// <summary>
    /// Job de treino da sessão, com o corte anti-vazamento derivado da sugestão.
    ///
    /// <para>
    /// <b>O corte é o próprio dia da sugestão.</b> As duas pontas obrigam a isso:
    /// <c>StageObservationLoader</c> aplica <c>TreinoAte</c> de forma <b>exclusiva</b>
    /// (<c>Data &lt; @treinoAte</c>), então cortar no dia da sugestão faz o ajuste parar no dia
    /// anterior; e <c>ComparacaoProcessor</c> exige que a data efetivamente alcançada seja
    /// <b>estritamente anterior</b> à sugestão. Cortar no dia seguinte satisfaria a leitura
    /// ingênua de "treinar com o que existia até a compra" e faria a última fase recusar o job,
    /// porque o dia da sugestão teria entrado no ajuste.
    /// </para>
    ///
    /// <para>
    /// A hora do cálculo é descartada de propósito. Cortar no instante da sugestão deixaria
    /// entrar as vendas da manhã do próprio dia, que o comprador de fato não tinha na tela — e o
    /// Stage é diário, não horário, então não existe recorte fino a fazer.
    /// </para>
    /// </summary>
    /// <param name="stage">
    /// Retrato da sugestão no Stage. <c>SkusDistintos</c> dimensiona o orçamento do treino: um
    /// número fixo pequeno deixa quase toda a sugestão real fora da população da comparação
    /// (<c>ComparacaoOutput.ItensForaOrcamentoSkus</c>) por um motivo que não tem nada a ver com
    /// o método sob teste — ver <see cref="PisoDeSkusDoTreino"/> e
    /// <see cref="TetoDeSkusDoTreino"/>. <c>Cabecalhos</c> é a invariante de "uma sugestão por
    /// sessão", checada aqui e não na comparação; ver <see cref="MaisDeUmaSugestao"/>.
    /// </param>
    public static (TreinoJob? Job, string? MotivoInviabilidade) Treino(
        SessaoEmAndamento sessao, SugestaoNoStage stage, DateTimeOffset agora)
    {
        if (SemDeclaracao(sessao)) return (null, SugestaoNaoDeclarada);
        if (stage.Cabecalhos > 1) return (null, MaisDeUmaSugestao(stage.Cabecalhos));

        return (new TreinoJob
        {
            Id = Guid.CreateVersion7(),
            RedeId = sessao.RedeId,
            Status = TreinoStatus.Pendente,
            DataAgendamento = agora,
            MaxSkus = Math.Clamp(stage.SkusDistintos, PisoDeSkusDoTreino, TetoDeSkusDoTreino),
            TreinoAte = DateOnly.FromDateTime(sessao.SugestaoDataHora!.Value),
        }, null);
    }

    /// <summary>
    /// Job da comparação contra o ERP, mirando a única sugestão a que a sessão está ancorada.
    ///
    /// <para>
    /// <b>A janela é o dia da sugestão nas duas pontas.</b> <c>StageSugestaoLoader</c> converte
    /// o par em <c>DataHora &gt;= JanelaInicio 00:00</c> e <c>DataHora &lt; JanelaFim + 1 dia
    /// 00:00</c>, então o próprio dia captura a sugestão a qualquer hora que ela tenha sido
    /// calculada, e nada de outro dia entra. Alargar seria pior que inútil: o import traz uma
    /// sugestão só, e uma janela maior passaria a depender de o ZIP continuar assim.
    /// </para>
    ///
    /// <para>
    /// O <paramref name="treinoJobId"/> vem por parâmetro em vez de sair do retrato: quem chama
    /// já teve de ler o treino para saber que ele concluiu, então aqui ele nunca é nulo — e
    /// aceitar nulo obrigaria a inventar mensagem de comprador para um estado impossível.
    /// </para>
    /// </summary>
    public static (ComparacaoPbs? Job, string? MotivoInviabilidade) Comparacao(
        SessaoEmAndamento sessao, Guid treinoJobId, DateTimeOffset agora)
    {
        if (SemDeclaracao(sessao)) return (null, SugestaoNaoDeclarada);

        var diaDaSugestao = DateOnly.FromDateTime(sessao.SugestaoDataHora!.Value);

        return (new ComparacaoPbs
        {
            Id = Guid.CreateVersion7(),
            RedeId = sessao.RedeId,
            Status = ComparacaoPbsStatus.Pendente,
            DataAgendamento = agora,
            TreinoJobId = treinoJobId,
            JanelaInicio = diaDaSugestao,
            JanelaFim = diaDaSugestao,
            TipoCalculo = sessao.SugestaoTipoCalculo!.Value,
        }, null);
    }

    /// <summary>
    /// Motivo de encerramento quando o job da fase corrente foi reclamado por um processo que
    /// nunca voltou — ou <c>null</c> enquanto a fase ainda tem direito de estar rodando.
    ///
    /// <para>
    /// As três filas reclamam com <c>WHERE Status = 'Pendente'</c> e gravam <c>Processando</c>,
    /// sem lease e sem heartbeat: um worker que morre no meio do treino deixa o job em
    /// <c>Processando</c> para sempre, e a sessão em <c>Treinando</c> sendo repescada a cada 5
    /// segundos sem nunca chegar a estado terminal. O comprador vê um spinner eterno, sem motivo
    /// e sem próxima ação. A idade da reclamação é o único sinal disponível — ver
    /// <see cref="ComparacaoSessao.LimiteDeFaseSemProgresso"/> para a escolha do limite.
    /// </para>
    ///
    /// <para>
    /// <paramref name="inicioDoProcessamento"/> nulo (job ainda <c>Pendente</c>) <b>nunca</b> é
    /// abandono: fila parada significa que nenhum worker está de pé, e nesse cenário este código
    /// também não está rodando para julgar. Quando um worker sobe, o job pendente é reclamado
    /// normalmente — e é esse o desfecho correto, não matar a sessão.
    /// </para>
    /// </summary>
    public static string? FaseAbandonada(string fase, DateTimeOffset? inicioDoProcessamento, DateTimeOffset agora)
    {
        if (inicioDoProcessamento is not { } inicio) return null;
        if (agora - inicio <= ComparacaoSessao.LimiteDeFaseSemProgresso) return null;

        return $"A etapa de {fase} começou e ficou mais de " +
               $"{ComparacaoSessao.LimiteDeFaseSemProgresso.TotalHours:0} horas sem dar sinal de progresso, o que " +
               "normalmente significa que o processamento foi interrompido antes de terminar. Envie os dados " +
               "novamente para recomeçar; se acontecer de novo, procure o suporte.";
    }

    /// <summary>
    /// Data e método viajam na mesma declaração do extrator e são checados juntos, na primeira
    /// fronteira. Deixar o método para depois faria a sessão treinar por minutos antes de
    /// descobrir que não há contra o que disputar.
    /// </summary>
    private static bool SemDeclaracao(SessaoEmAndamento sessao)
        => sessao.SugestaoDataHora is null || sessao.SugestaoTipoCalculo is null;

    /// <summary>
    /// Inviabilidade, não falha, pelo critério que o <c>CargaProcessor</c> firmou: nada quebrou,
    /// faltou pré-condição no que foi enviado, e o remédio é gerar o envio de novo pelo extrator.
    /// Chamar isto de falha mandaria o comprador "tentar de novo" um arquivo que nunca vai servir.
    /// </summary>
    private const string SugestaoNaoDeclarada =
        "Não sabemos qual sugestão de compra do seu ERP esta comparação deveria avaliar, então não há o que " +
        "comparar. Baixe o extrator, escolha novamente a sugestão que você quer comparar e envie o arquivo que " +
        "ele gerar, sem alterar o conteúdo.";

    /// <summary>
    /// Uma sessão está ancorada a UMA sugestão, e a comparação agrega os itens sem separar por
    /// sugestão: duas selecionadas pelo mesmo recorte sairiam somadas num resultado só, que não
    /// descreve nenhuma das duas — e nada na tela denunciaria a mistura.
    ///
    /// <para>
    /// A checagem mora aqui, na primeira fronteira da sessão, e <b>não</b> no
    /// <c>ComparacaoProcessor</c>: aquele processador também atende a comparação avulsa da F13,
    /// cuja janela é escolhida pelo comprador (o default da tela são 30 dias) e cujo resultado
    /// declara <c>Sugestoes: N</c> de propósito. Recusar N &gt; 1 lá dentro tiraria uma
    /// capacidade que a F13 oferece; recusar aqui protege exatamente quem depende da invariante.
    /// Fica antes do treino também por economia: descobrir isso uma fase depois custaria minutos
    /// de LightGBM.
    /// </para>
    /// </summary>
    private static string MaisDeUmaSugestao(int cabecalhos) =>
        $"Os dados enviados trazem {cabecalhos} sugestões de compra do mesmo dia e do mesmo método de cálculo, e " +
        "cada comparação avalia uma sugestão por vez — juntar as duas daria um resultado que não descreve nenhuma " +
        "delas. Gere os dados novamente pelo extrator, escolhendo uma única sugestão, e envie o arquivo que ele " +
        "produzir sem alterar o conteúdo.";
}
