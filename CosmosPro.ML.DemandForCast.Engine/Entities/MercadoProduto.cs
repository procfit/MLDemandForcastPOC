namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Dimensão de produto do relatório IQVIA, uma linha por EAN. Fica fora de
/// <see cref="MercadoObservacao"/> porque os atributos se repetiriam em cada
/// célula da forma longa (dezenas de milhares de linhas por arquivo).
///
/// <para>
/// Upsert por (RedeId, Ean), último arquivo vence. É catálogo da IQVIA, não da
/// rede: um EAN pode existir aqui sem existir em Stage.Produtos e vice-versa —
/// o join com o cadastro da rede é por valor, na leitura (F16 parte C).
/// </para>
/// </summary>
public sealed class MercadoProduto
{
    public int RedeId { get; set; }
    public required string Ean { get; set; }

    public required string DescricaoLonga { get; set; }
    public string? Laboratorio { get; set; }

    /// <summary>Texto livre da IQVIA; combinações vêm separadas por '|'. Não é chave.</summary>
    public string? Molecula { get; set; }

    /// <summary>"PRESCRICAO", "MIP" etc.</summary>
    public string? AreaFarmacia { get; set; }

    public string? Nec1 { get; set; }
    public string? Forma3 { get; set; }

    /// <summary>Classe terapêutica (ATC-like da IQVIA, ex.: "R05A0 - A/GRIPAIS EXC A/INFEC").</summary>
    public string? Classe4 { get; set; }
}
