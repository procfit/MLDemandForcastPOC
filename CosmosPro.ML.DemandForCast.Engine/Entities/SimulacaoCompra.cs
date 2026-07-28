namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Job de simulação de compras (F8). Vive em engine.SimulacoesCompra. O Worker faz
/// polling com o mesmo padrão competing-consumers das cargas/treinos. Cada
/// simulação compara políticas de reabastecimento (clássica eMax/eSeg vs ROP
/// derivado do forecast LightGBM) em uma janela histórica do Stage, com KPIs
/// globais e por hierarquia.
/// </summary>
public sealed class SimulacaoCompra
{
    public Guid Id { get; set; }

    /// <summary>Rede simulada. Redundante com o TreinoJob, mas evita join no polling.</summary>
    public int RedeId { get; set; }

    public SimulacaoStatus Status { get; set; }

    public DateTimeOffset DataAgendamento { get; set; }
    public DateTimeOffset? DataInicioProcessamento { get; set; }
    public DateTimeOffset? DataConclusao { get; set; }

    /// <summary>
    /// TreinoJob de onde sairá o modelo LightGBM usado pela política forecast-based.
    /// Apontar para um treino concluído (Status=Concluido + ModeloBlobKey).
    /// </summary>
    public Guid TreinoJobId { get; set; }

    /// <summary>Janela de replay (em dias) usada para a simulação — default 60.</summary>
    public int JanelaDias { get; set; }

    /// <summary>Lead time (dias entre pedido e recebimento) usado nas políticas.</summary>
    public int LeadTimeDias { get; set; }

    /// <summary>Ciclo de revisão (dias).</summary>
    public int CicloDias { get; set; }

    /// <summary>Fator de serviço z (default 1.65 ≈ 95%).</summary>
    public double FatorServico { get; set; }

    /// <summary>Quantos SKUs×lojas foram simulados (informativo na UI).</summary>
    public long? SeriesSimuladas { get; set; }

    /// <summary>JSON com <c>SimulationResult</c> (KPIs globais + por hierarquia, por política).</summary>
    public string? ResultadoJson { get; set; }

    public string? MensagemErro { get; set; }
}

public enum SimulacaoStatus
{
    Pendente = 0,
    Processando = 1,
    Concluido = 2,
    Falha = 3,
}
