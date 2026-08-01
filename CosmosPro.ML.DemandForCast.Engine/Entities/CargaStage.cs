namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Job de importação de dados do usuário (ZIP de CSVs) para o banco Stage.
/// Vive em engine.CargasStage. Worker faz polling desta tabela.
/// </summary>
public sealed class CargaStage
{
    public Guid Id { get; set; }

    /// <summary>
    /// Rede dona dos dados desta carga. O Worker injeta este valor em toda linha
    /// carregada no Stage — RedeId nunca trafega nos CSVs, para o cliente não poder
    /// reivindicar a rede de outro.
    /// </summary>
    public int RedeId { get; set; }

    public CargaStageStatus Status { get; set; }

    /// <summary>Quando o usuário fez o upload via API.</summary>
    public DateTimeOffset DataAgendamento { get; set; }

    /// <summary>Quando o Worker pegou o job (Status -> Processando).</summary>
    public DateTimeOffset? DataInicioProcessamento { get; set; }

    /// <summary>Quando terminou (sucesso ou falha).</summary>
    public DateTimeOffset? DataConclusao { get; set; }

    /// <summary>Nome do arquivo que o usuário enviou (ex.: "import-2026-05-20.zip").</summary>
    public required string NomeArquivoOriginal { get; set; }

    /// <summary>Chave no MinIO onde o ZIP foi salvo. Bucket fica implícito na config do consumer.</summary>
    public required string BlobKey { get; set; }

    /// <summary>Mensagem de erro quando Status = Falha. Null caso contrário.</summary>
    public string? MensagemErro { get; set; }

    /// <summary>Total de linhas carregadas (somando todas as tabelas). Null antes da conclusão.</summary>
    public long? LinhasImportadas { get; set; }

    /// <summary>Placeholder para futuro modelo de autenticação. Por enquanto pode ser "anonymous".</summary>
    public string? UsuarioId { get; set; }
}

public enum CargaStageStatus
{
    Pendente = 0,
    Processando = 1,
    Concluida = 2,
    Falha = 3,
}
