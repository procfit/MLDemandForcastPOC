namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Um PDV do painel da IQVIA dentro de um brick, identificado por CNPJ — vem da
/// aba "quantidade PDV" do relatório. É a ponte entre o brick e as lojas da rede
/// (Stage.Lojas.Cnpj): sem ela o sinal de mercado não se prende à série de loja
/// nenhuma.
///
/// <para>
/// O agregado de concorrência vem com CNPJ zerado ("00000000000000") e conta como
/// linha normal — ele diz quantos PDVs concorrentes o painel enxerga no brick, e
/// nunca vai casar com loja da rede. Recarga substitui por (RedeId, Brick).
/// </para>
/// </summary>
public sealed class MercadoBrickPdv
{
    public int RedeId { get; set; }
    public required string Brick { get; set; }

    /// <summary>Só dígitos. "00000000000000" é o agregado anônimo de concorrentes.</summary>
    public required string Cnpj { get; set; }

    public required string Bandeira { get; set; }
}
