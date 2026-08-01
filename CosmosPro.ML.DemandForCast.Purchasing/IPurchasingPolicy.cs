namespace CosmosPro.ML.DemandForCast.Purchasing;

/// <summary>
/// Política de compra. Dado o contexto de um SKU×loja num dia D, computa o par
/// <see cref="PolicyParameters.ReorderPoint"/> (s) e <see cref="PolicyParameters.OrderUpToLevel"/> (S)
/// no estilo clássico (s, S):
/// <list type="bullet">
///   <item>se posição de estoque (físico + em trânsito) ≤ s → pede até S.</item>
///   <item>quantidade pedida = max(0, S − posição de estoque).</item>
/// </list>
///
/// <para>
/// Única implementação no repo: <see cref="Policies.ForecastRopPolicy"/> (forecast
/// LightGBM acumulado no LT + safety por desvio do erro). A regra clássica eMax/eSeg
/// deixou de ser reimplementada aqui — o baseline do TCC agora vem do próprio ERP
/// (PBS), que já grava sua própria eMax/eSeg e sua própria previsão de demanda (F13).
/// </para>
/// </summary>
public interface IPurchasingPolicy
{
    string Name { get; }

    PolicyParameters Compute(PolicyContext context);
}

/// <summary>Par (s, S) de uma política de reabastecimento num dia D.</summary>
public sealed record PolicyParameters(decimal ReorderPoint, decimal OrderUpToLevel);
