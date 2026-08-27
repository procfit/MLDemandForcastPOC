namespace CosmosPro.ML.DemandForCast.ApiService.Imports;

/// <summary>
/// Define quais CSVs são esperados no ZIP de import e quais colunas cada um
/// precisa conter (case-insensitive, qualquer ordem). Mantido em sync com o
/// schema do banco Stage (ver Docs/schema.md).
/// </summary>
internal static class ImportSchemas
{
    public static readonly IReadOnlyDictionary<string, string[]> ExpectedFiles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["lojas.csv"] = ["LojaId", "Nome", "UF", "Cidade"],
        ["produtos.csv"] = ["Sku", "Nome"],
        ["vendas.csv"] = ["Data", "LojaId", "Sku", "Quantidade", "PrecoUnitario", "ValorTotal"],
        ["estoques_diarios.csv"] = ["Data", "LojaId", "Sku", "QuantidadeEmEstoque"],
        ["compras.csv"] = ["DataPedido", "LojaId", "Sku", "Quantidade"],
        ["promocoes.csv"] = ["DataInicio", "DataFim", "Sku"],
        // mercado_iqvia.csv saiu do contrato na F16: o dado de mercado é importado à
        // parte (XLSX da IQVIA, banco engine). ZIPs antigos que ainda o trazem passam
        // sem erro — entrada desconhecida no ZIP nunca foi validada nem carregada.
    };

    /// <summary>
    /// Arquivos OPCIONAIS: se presentes no ZIP, têm o header validado; se ausentes,
    /// o import segue normalmente. Mantém compatibilidade com ZIPs antigos (pré-F8.x)
    /// que não traziam sinais exógenos.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> OptionalFiles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["sinais_externos.csv"] = ["Data", "Geografia", "Tipo", "Valor"],
        // Sugestões do ERP: só quem extrai do PBS traz. O dataset sintético e os
        // ZIPs anteriores à F12 não têm, e o import precisa seguir funcionando.
        ["sugestoes_compra.csv"] = ["SugestaoId", "DataHora", "TipoCalculo"],
        ["sugestoes_compra_itens.csv"] = ["SugestaoId", "LojaId", "Sku", "DemandaDia", "CompraSugerida", "CompraAutorizada"],
    };
}
