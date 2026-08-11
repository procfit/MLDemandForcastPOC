using CosmosPro.ML.DemandForCast.Engine.Entities;

namespace CosmosPro.ML.DemandForCast.Worker.Sessoes;

/// <summary>
/// Situação do job da fase corrente, do ponto de vista de quem só decide se a sessão anda.
/// Público — e não interno como o resto do Worker — porque entra na assinatura do
/// <c>[Theory]</c> que cobre a decisão, e o xUnit exige classe de teste pública.
/// </summary>
public enum JobResultado
{
    EmAndamento = 0,
    Concluido = 1,
    Falhou = 2,
}

/// <summary>
/// Para onde a sessão vai quando o job da fase corrente chega a um desfecho.
///
/// <para>
/// Pura de propósito — sem banco, sem I/O, sem relógio. A ordem das fases (importar,
/// treinar, comparar) é a regra de negócio da sessão, e mantê-la aqui é o que permite
/// verificá-la sem subir SQL Server, MinIO e três workers. O <see cref="SessaoWorker"/>
/// cuida do resto: ler o job da fase, criar o da seguinte e gravar.
/// </para>
///
/// <para>
/// Só as três fases intermediárias se movem. Um job antigo que muda de estado não pode
/// arrastar uma sessão terminal de volta ao fluxo, e sair de <c>Inviavel</c>/<c>Falha</c> é
/// decisão do usuário (reenviar outro ZIP), não deste avanço — ver
/// <see cref="ComparacaoSessao.PodeTransicionar"/>, que é quem tem a palavra final.
/// </para>
/// </summary>
internal static class SessaoAvancador
{
    public static SessaoStatus ProximoEstado(SessaoStatus atual, JobResultado resultado) => (atual, resultado) switch
    {
        (SessaoStatus.ProcessandoDados, JobResultado.Concluido) => SessaoStatus.Treinando,
        (SessaoStatus.Treinando, JobResultado.Concluido) => SessaoStatus.Comparando,
        // Comparar não conclui: falta o comprador avaliar. A transição para Concluida é a
        // única da máquina que não sai daqui — quem a faz é o endpoint de envio do
        // questionário, porque o que falta é um humano, não um job.
        (SessaoStatus.Comparando, JobResultado.Concluido) => SessaoStatus.AguardandoQuestionario,

        (SessaoStatus.ProcessandoDados or SessaoStatus.Treinando or SessaoStatus.Comparando,
            JobResultado.Falhou) => SessaoStatus.Falha,

        _ => atual,
    };
}
