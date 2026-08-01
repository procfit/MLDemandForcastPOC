namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Job de comparação entre a sugestão de compra do ERP (PBS) e a que o ML.NET
/// produziria, para UMA janela de vendas reais e UM método de cálculo do ERP (F13).
/// Vive em engine.ComparacoesPbs. O Worker faz polling com o mesmo padrão
/// competing-consumers das demais filas — ver <see cref="CargaStage"/>.
/// </summary>
public sealed class ComparacaoPbs
{
    public Guid Id { get; set; }

    /// <summary>Rede cujos dados serão comparados.</summary>
    public int RedeId { get; set; }

    public ComparacaoPbsStatus Status { get; set; }

    public DateTimeOffset DataAgendamento { get; set; }
    public DateTimeOffset? DataInicioProcessamento { get; set; }
    public DateTimeOffset? DataConclusao { get; set; }

    /// <summary>
    /// TreinoJob de onde sai o modelo LightGBM usado na comparação. Referência
    /// solta (sem FK, sem navegação) — mesmo padrão das demais ligações entre
    /// jobs: o histórico da comparação sobrevive se o treino for removido.
    /// </summary>
    public Guid TreinoJobId { get; set; }

    /// <summary>
    /// Início da janela: primeiro dia (inclusive) em que a sugestão do ERP pode ter sido
    /// calculada (<c>SugestoesCompra.DataHora</c>). <b>Não</b> é o primeiro dia de venda
    /// pontuado — ver <see cref="JanelaFim"/>.
    /// </summary>
    public DateOnly JanelaInicio { get; set; }

    /// <summary>
    /// Fim da janela: último dia (inclusive) em que a sugestão do ERP pode ter sido
    /// calculada (<c>SugestoesCompra.DataHora</c>).
    ///
    /// <para>
    /// <b>Não limita os dias de venda pontuados.</b> Cada sugestão é medida sobre a
    /// cobertura que começa na própria data dela — <c>DiasEstoque</c> dias à frente,
    /// tipicamente 15 ou 30 —, então uma janela até 31/07 pontua venda real até meados de
    /// agosto. Quem escolhe "julho" esperando que a pontuação pare em 31/07 vai se
    /// surpreender. Uma sugestão cuja cobertura ultrapassa o fim dos dados importados não
    /// é descartada por estar fora da janela — ela sai contada em
    /// <c>ComparacaoOutput.ItensForaCamadaAAlemDoHistorico</c> /
    /// <c>ItensForaCamadaBAlemDoHistorico</c>, com o motivo explícito em vez de misturada
    /// com série curta.
    /// </para>
    /// </summary>
    public DateOnly JanelaFim { get; set; }

    /// <summary>
    /// Método de cálculo do ERP em escopo nesta execução. Espelha
    /// dbo.SugestoesCompra.TipoCalculo (ver CosmosPro.ML.DemandForCast.Database\Tables\SugestoesCompra.sql):
    /// 1 = "Emax e Eseg" (usa estoque máximo/segurança), 2 = "Dias de Reposição"
    /// (cobertura fixa em dias, não usa eMax/eSeg). São baselines distintos —
    /// misturar os dois numa mesma métrica não significa nada, por isso uma
    /// execução sempre mira em UM método, nunca nos dois.
    /// </summary>
    public byte TipoCalculo { get; set; }

    /// <summary>
    /// JSON com as métricas das camadas de comparação (previsão, decisão,
    /// intervenção humana), globais e por hierarquia. Renderizado pela UI de
    /// resultados.
    /// </summary>
    public string? ResultadoJson { get; set; }

    public string? MensagemErro { get; set; }
}

public enum ComparacaoPbsStatus
{
    Pendente = 0,
    Processando = 1,
    Concluido = 2,
    Falha = 3,
}
