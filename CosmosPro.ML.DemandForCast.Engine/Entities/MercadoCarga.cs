namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Job de importação de um relatório de mercado da IQVIA (XLSX) para as tabelas
/// de mercado do banco engine. Mesma fila-pattern de <see cref="CargaStage"/>,
/// fila separada porque o ciclo de vida é outro: o dado de mercado é da rede e
/// sobrevive aos imports do Stage — é reaproveitado por várias comparações.
/// </summary>
public sealed class MercadoCarga
{
    public Guid Id { get; set; }

    /// <summary>
    /// Rede dona do dado. Injetado do usuário autenticado — nunca vem do arquivo,
    /// pelo mesmo motivo do RedeId dos CSVs do Stage.
    /// </summary>
    public int RedeId { get; set; }

    public MercadoCargaStatus Status { get; set; }

    public DateTimeOffset DataAgendamento { get; set; }
    public DateTimeOffset? DataInicioProcessamento { get; set; }
    public DateTimeOffset? DataConclusao { get; set; }

    public required string NomeArquivoOriginal { get; set; }

    /// <summary>Chave no MinIO (bucket "mercado") onde o XLSX foi salvo.</summary>
    public required string BlobKey { get; set; }

    public string? MensagemErro { get; set; }

    /// <summary>Observações gravadas (linhas 100% zeradas não contam). Null antes da conclusão.</summary>
    public long? LinhasImportadas { get; set; }

    public string? UsuarioId { get; set; }

    /// <summary>
    /// O que o arquivo declarou, em JSON (<c>MercadoCargaResumo</c>): meses e bricks
    /// cobertos, EANs descartados, linhas zeradas puladas. É a única fonte da distinção
    /// entre "zero" e "não coberto": dentro de um (mês, brick) coberto, ausência de
    /// linha em MercadoObservacoes significa venda zero; fora, significa que o dado
    /// nunca foi enviado.
    /// </summary>
    public string? ResumoJson { get; set; }
}

public enum MercadoCargaStatus
{
    Pendente = 0,
    Processando = 1,
    Concluida = 2,
    Falha = 3,
}
