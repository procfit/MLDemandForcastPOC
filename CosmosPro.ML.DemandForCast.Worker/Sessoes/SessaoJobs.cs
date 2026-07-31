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
    /// Orçamento de SKUs do treino da sessão. Mesmo default do enfileiramento manual
    /// (<c>/api/training/run</c>): a sessão não pergunta nada ao comprador, e inventar aqui um
    /// número diferente do que o resto do POC exercita mudaria o tempo de treino sem medida.
    /// Consequência conhecida: numa sugestão real do PBS, com muito mais itens do que isto, os
    /// que ficarem fora do top-N saem contados em <c>ComparacaoOutput.ItensForaOrcamentoSkus</c>.
    /// </summary>
    private const int MaxSkusDoTreino = 80;

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
    public static (TreinoJob? Job, string? MotivoInviabilidade) Treino(
        SessaoEmAndamento sessao, DateTimeOffset agora)
    {
        if (SemDeclaracao(sessao)) return (null, SugestaoNaoDeclarada);

        return (new TreinoJob
        {
            Id = Guid.CreateVersion7(),
            RedeId = sessao.RedeId,
            Status = TreinoStatus.Pendente,
            DataAgendamento = agora,
            MaxSkus = MaxSkusDoTreino,
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
}
