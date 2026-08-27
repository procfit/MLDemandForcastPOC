namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Uma célula do relatório IQVIA em forma longa: quanto um EAN vendeu num brick,
/// numa bandeira, num mês. Só células com alguma medida diferente de zero são
/// gravadas (~72% do arquivo real é zero); a cobertura declarada em
/// <see cref="MercadoCarga.ResumoJson"/> é o que separa "zero" de "não coberto".
///
/// <para>
/// Recarga substitui por (RedeId, Mes, Brick): reenviar junho/2026 troca junho/2026
/// daqueles bricks e não toca no resto da série — a série mensal só existe
/// empilhando N arquivos, porque cada um traz o mês e o espelho do ano anterior.
/// </para>
/// </summary>
public sealed class MercadoObservacao
{
    public int RedeId { get; set; }

    /// <summary>Primeiro dia do mês de referência (o "202506" do cabeçalho vira 2025-06-01).</summary>
    public DateOnly Mes { get; set; }

    /// <summary>Micro-região da IQVIA, como veio do cabeçalho (ex.: "528-RJ VOLTA REDONDA RETIRO").</summary>
    public required string Brick { get; set; }

    /// <summary>
    /// Texto da bandeira no cabeçalho. "CONCORRENTES" é o agregado reservado de
    /// todos os PDVs que não são da rede; qualquer outro valor é bandeira própria.
    /// </summary>
    public required string Bandeira { get; set; }

    /// <summary>Só dígitos, como vieram do arquivo. Linha sem EAN é descartada no parse.</summary>
    public required string Ean { get; set; }

    public decimal Unidades { get; set; }

    /// <summary>
    /// "Real CPP" da IQVIA: valor ao consumidor sob a metodologia que NORMALIZA os
    /// preços de venda entre os players. NÃO é preço de balcão observado nem preço
    /// de aquisição da farmácia — não compare com SugestoesCompraItens.PrecoCompra,
    /// e trate ValorCpp/Unidades como preço-índice, não como preço praticado.
    /// </summary>
    public decimal ValorCpp { get; set; }
}
