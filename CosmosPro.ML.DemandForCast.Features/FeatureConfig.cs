namespace CosmosPro.ML.DemandForCast.Features;

/// <summary>
/// Parâmetros do feature engineering. Defaults refletem as decisões de F5:
/// granularidade diária, horizonte/lead time de 7 dias.
/// </summary>
public sealed record FeatureConfig
{
    /// <summary>
    /// Lead time da decisão de compra, em dias. Nenhuma feature de histórico pode
    /// usar dados mais recentes que D - LeadTimeDias (anti-leakage). A compra do
    /// dia D é decidida com LeadTimeDias de antecedência.
    /// </summary>
    public int LeadTimeDias { get; init; } = 7;

    /// <summary>Lags (em dias). Devem ser >= LeadTimeDias.</summary>
    public int[] Lags { get; init; } = [7, 14, 21, 28];

    /// <summary>Tamanho das janelas rolling, em dias.</summary>
    public int RollingCurto { get; init; } = 7;
    public int RollingLongo { get; init; } = 28;

    /// <summary>
    /// Congela o preço a partir desta data (inclusive). Para todo dia-alvo D &gt;= corte,
    /// <c>PrecoUnitario</c> e <c>PrecoRelativoMedia</c> passam a usar o último preço
    /// conhecido ESTRITAMENTE antes do corte, e não o preço realizado em D.
    ///
    /// <para>
    /// Existe porque o preço unitário que chega a este builder é o preço
    /// REALIZADO da venda do dia (<c>Worker/Training/StageObservationLoader</c> lê
    /// <c>PrecoUnitario</c> de <c>Vendas</c>), não um preço de tabela planejado.
    /// Num dia com remarcação não planejada, o desconto realizado entraria na linha de
    /// features daquele mesmo dia e o modelo leria desconto → volume no dia que está
    /// sendo pontuado (CLAUDE.md §6). Para treino isso é indiferente — o passado é
    /// passado —, mas para avaliar previsão contra um baseline que não tinha essa
    /// informação (F13, camada A) o corte é obrigatório.
    /// </para>
    ///
    /// <para>
    /// <c>null</c> (padrão) = comportamento histórico, preço realizado do próprio D.
    /// É opt-in: o caminho de treino não muda.
    /// </para>
    /// </summary>
    public DateOnly? PrecoCongeladoAPartirDe { get; init; }

    /// <summary>
    /// Histórico mínimo (em dias) exigido antes do primeiro dia-alvo válido.
    /// É o índice mais antigo que qualquer feature acessa:
    ///  - maior lag exige D - maxLag >= 0
    ///  - rolling longo termina em D - LeadTime e volta RollingLongo dias, exigindo
    ///    D - LeadTime - (RollingLongo - 1) >= 0
    /// Tomamos o maior dos dois.
    /// </summary>
    public int HistoricoMinimoDias => Math.Max(Lags.Max(), LeadTimeDias + RollingLongo - 1);

    public void Validate()
    {
        if (LeadTimeDias < 1)
            throw new ArgumentException("LeadTimeDias deve ser >= 1.");
        if (Lags.Length == 0 || Lags.Any(l => l < LeadTimeDias))
            throw new ArgumentException($"Todos os lags devem ser >= LeadTimeDias ({LeadTimeDias}) para evitar leakage.");
    }
}
