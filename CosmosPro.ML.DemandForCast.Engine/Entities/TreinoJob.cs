namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Job de treino do engine de previsão. Vive em engine.TreinoJobs. O Worker faz
/// polling (mesmo padrão competing-consumers das cargas) — ver
/// <see cref="CargaStage"/>. O resultado (comparação dos engines + chave do modelo
/// salvo no MinIO) é gravado ao concluir.
/// </summary>
public sealed class TreinoJob
{
    public Guid Id { get; set; }

    /// <summary>Rede cujos dados serão treinados. Modelo é sempre por rede, nunca cruzando.</summary>
    public int RedeId { get; set; }

    public TreinoStatus Status { get; set; }

    public DateTimeOffset DataAgendamento { get; set; }
    public DateTimeOffset? DataInicioProcessamento { get; set; }
    public DateTimeOffset? DataConclusao { get; set; }

    /// <summary>
    /// Limite de SKUs (top por volume de vendas) usados no treino. Mantém o tempo
    /// de treino do POC sob controle — o backtest retreina o LightGBM a cada fold.
    /// </summary>
    public int MaxSkus { get; set; }

    /// <summary>
    /// Corte de informação do treino: nenhuma observação com data igual ou posterior
    /// a esta pode entrar no ajuste do modelo. <c>null</c> = sem corte, treina sobre
    /// todo o histórico importado da rede.
    ///
    /// <para>
    /// É um controle anti-vazamento, não um parâmetro de desempenho — encurtar a
    /// janela de treino não deixa o modelo melhor, deixa-o honesto. O import da
    /// comparação traz de propósito os dias <b>posteriores</b> à sugestão do ERP,
    /// porque são o gabarito contra o qual os dois braços são pontuados. Treinar sem
    /// corte faz o modelo aprender sobre esses mesmos dias, e a comparação deixa de
    /// medir previsão para medir memória.
    /// </para>
    ///
    /// <para>
    /// Quem monta a população da comparação declara
    /// <c>ComparisonItem.ModeloTreinadoAte</c>, que o comparador exige ser
    /// estritamente anterior à data da sugestão. Este campo é o que torna essa
    /// declaração verificável em vez de adivinhada.
    /// </para>
    /// </summary>
    public DateOnly? TreinoAte { get; set; }

    /// <summary>Chave no MinIO (bucket de modelos) do .zip do modelo LightGBM treinado.</summary>
    public string? ModeloBlobKey { get; set; }

    /// <summary>
    /// JSON com a comparação walk-forward dos engines (métricas globais + por
    /// hierarquia). Renderizado pela UI de resultados.
    /// </summary>
    public string? ResultadoJson { get; set; }

    /// <summary>Quantidade de linhas de feature usadas no treino (para exibição).</summary>
    public long? FeaturesGeradas { get; set; }

    public string? MensagemErro { get; set; }
}

public enum TreinoStatus
{
    Pendente = 0,
    Processando = 1,
    Concluido = 2,
    Falha = 3,
}
