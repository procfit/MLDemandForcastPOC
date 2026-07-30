namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Uma comparação entre a sugestão de compra do ERP e a que o ML faria, ancorada a
/// UMA sugestão do PBS.
/// <para>
/// Nasce sem <see cref="SugestaoId"/>: a sugestão é escolhida no Extractor, que é o
/// único com acesso ao PBS, e o ZIP declara qual foi. É assim que o ovo-e-galinha se
/// resolve — a web não pode pedir a sugestão antes de ter os dados.
/// </para>
/// </summary>
public sealed class ComparacaoSessao
{
    public Guid Id { get; set; }
    public int RedeId { get; set; }
    public string? Nome { get; set; }

    public SessaoStatus Status { get; set; }
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }

    public long? SugestaoId { get; set; }
    /// <summary>Retrato da sugestão, para o painel ser legível sem consultar o Stage.</summary>
    public string? SugestaoDescricao { get; set; }
    public DateTime? SugestaoDataHora { get; set; }
    public byte? SugestaoTipoCalculo { get; set; }

    public Guid? CargaStageId { get; set; }
    public Guid? TreinoJobId { get; set; }
    public Guid? ComparacaoPbsId { get; set; }

    /// <summary>Agregados da manchete. O detalhe por item vive em ComparacaoSessaoItens.</summary>
    public string? ResultadoJson { get; set; }

    public string? MotivoInviabilidade { get; set; }
    public string? MensagemErro { get; set; }

    private static readonly Dictionary<SessaoStatus, SessaoStatus[]> Permitidas = new()
    {
        [SessaoStatus.AguardandoDados] = [SessaoStatus.ProcessandoDados, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.ProcessandoDados] = [SessaoStatus.Treinando, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Treinando] = [SessaoStatus.Comparando, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Comparando] = [SessaoStatus.Concluida, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Concluida] = [],
        [SessaoStatus.Inviavel] = [SessaoStatus.AguardandoDados],  // reenviar outro ZIP
        [SessaoStatus.Falha] = [SessaoStatus.AguardandoDados],
    };

    public static bool PodeTransicionar(SessaoStatus de, SessaoStatus para) =>
        Permitidas.TryGetValue(de, out var destinos) && destinos.Contains(para);
}

public enum SessaoStatus
{
    AguardandoDados = 0,
    ProcessandoDados = 1,
    Treinando = 2,
    Comparando = 3,
    Concluida = 4,
    Inviavel = 5,
    Falha = 6,
}
