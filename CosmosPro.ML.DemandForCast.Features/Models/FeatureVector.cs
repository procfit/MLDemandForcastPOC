namespace CosmosPro.ML.DemandForCast.Features.Models;

/// <summary>
/// Linha de features pronta para treino/inferência, referente a um dia-alvo D
/// de um SKU em uma loja. As features de histórico (lags/rolling) respeitam o lead
/// time: usam apenas informação disponível até D - <c>LeadTimeDias</c> (ver
/// <see cref="FeatureConfig"/>). Calendário e promoção do próprio D são conhecidos
/// antecipadamente (a compra é planejada sabendo o dia da semana e a promoção
/// agendada).
///
/// <para>
/// <b>Preço é a exceção honesta:</b> <see cref="PrecoUnitario"/> e
/// <see cref="PrecoRelativoMedia"/> vêm do preço médio REALIZADO da venda de D, não
/// de um preço de tabela — remarcação não planejada entra neles. Para treino isso é
/// aceitável; para avaliar contra um baseline que não tinha essa informação, gere as
/// features com <see cref="FeatureConfig.PrecoCongeladoAPartirDe"/>.
/// </para>
/// </summary>
public sealed record FeatureVector
{
    // --- Chaves / contexto (não são features de treino diretas) ---------------
    public required DateOnly Data { get; init; }
    public required int LojaId { get; init; }
    public required string Sku { get; init; }

    // --- Target ---------------------------------------------------------------
    /// <summary>Unidades vendidas no dia-alvo D (o que o modelo prevê).</summary>
    public required decimal Target { get; init; }

    /// <summary>
    /// false quando D está em ruptura — a linha deve ser EXCLUÍDA do conjunto de
    /// treino (target enviesado), mas pode existir para inspeção/inferência.
    /// </summary>
    public required bool IsValidTarget { get; init; }

    // --- Lags (unidades vendidas em D-k, k >= LeadTimeDias) -------------------
    public decimal Lag7 { get; init; }
    public decimal Lag14 { get; init; }
    public decimal Lag21 { get; init; }
    public decimal Lag28 { get; init; }

    // --- Rolling (janela terminando em D - LeadTimeDias) ----------------------
    public decimal RollMean7 { get; init; }
    public decimal RollMean28 { get; init; }
    public decimal RollStd28 { get; init; }
    public decimal RollMax28 { get; init; }

    // --- Calendário do dia-alvo D (conhecido a priori) ------------------------
    public int DiaDaSemana { get; init; }   // 0=domingo .. 6=sábado
    public int DiaDoMes { get; init; }
    public int Mes { get; init; }
    public bool FimDeSemana { get; init; }
    public bool Feriado { get; init; }

    // --- Promoção (planejada, conhecida) do dia-alvo D ------------------------
    public bool EmPromocao { get; init; }
    public int DiasDesdeUltimaPromo { get; init; }

    // --- Preço do dia-alvo D (realizado, salvo se congelado) ------------------

    /// <summary>
    /// Preço praticado em D. Realizado por padrão; com
    /// <c>FeatureConfig.PrecoCongeladoAPartirDe</c>, o último preço conhecido antes
    /// do corte.
    /// </summary>
    public decimal PrecoUnitario { get; init; }

    /// <summary>
    /// <see cref="PrecoUnitario"/> relativo à média de preço da janela rolling
    /// (1.0 = igual). Herda o congelamento de <see cref="PrecoUnitario"/>.
    /// </summary>
    public decimal PrecoRelativoMedia { get; init; }

    // --- Hierarquia (categóricas) ---------------------------------------------
    public string Categoria { get; init; } = "";
    public string PrincipioAtivo { get; init; } = "";
    public string ClasseAbc { get; init; } = "";
    public string UF { get; init; } = "";
    public string Regiao { get; init; } = "";
    public string PerfilLoja { get; init; } = "";
}
