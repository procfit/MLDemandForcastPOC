namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Detalhe por item da comparação. Tabela, e não JSON, porque o import faz
/// DELETE ... WHERE RedeId — o Stage da rede é apagado a cada ZIP novo, então o
/// resultado tem de ser materializado para uma sessão antiga continuar legível. E
/// porque esta é a tabela que o comprador ordena e pagina para conferir contra a
/// memória dele, o que exige paginação server-side.
/// </summary>
public sealed class ComparacaoSessaoItem
{
    public Guid SessaoId { get; set; }
    public int LojaId { get; set; }
    public required string Sku { get; set; }
    public string? NomeProduto { get; set; }
    public string? Curva { get; set; }

    public decimal CompraSugeridaPbs { get; set; }
    public decimal CompraSugeridaMl { get; set; }
    public decimal VendidoNaJanela { get; set; }

    public decimal DemandaDiaPbs { get; set; }
    public decimal DemandaDiaMl { get; set; }
    public decimal DemandaDiaReal { get; set; }

    public decimal SobraPbsUnidades { get; set; }
    public decimal SobraMlUnidades { get; set; }
    public decimal SobraPbsValor { get; set; }
    public decimal SobraMlValor { get; set; }
}
