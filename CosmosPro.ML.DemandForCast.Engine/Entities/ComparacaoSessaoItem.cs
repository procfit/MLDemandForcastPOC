namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Detalhe por item da comparação. Tabela, e não JSON, porque o import faz
/// DELETE ... WHERE RedeId — o Stage da rede é apagado a cada ZIP novo, então o
/// resultado tem de ser materializado para uma sessão antiga continuar legível. E
/// porque esta é a tabela que o comprador ordena e pagina para conferir contra a
/// memória dele, o que exige paginação server-side.
///
/// <para>
/// <b>As colunas do braço de ML são anuláveis, e isso é o contrato da tabela.</b> Nulo
/// significa "não foi possível calcular", nunca "o ML disse zero" — duas afirmações
/// opostas para quem lê a tela. A cobertura corrente do ERP é de 15 a 30 dias e o
/// pipeline prevê 7 (<c>DecisionOptions.HorizonteMaximoMl</c>), então hoje o desfecho
/// esperado é justamente a ausência: gravar zero faria a tela dizer ao comprador que o
/// ML mandaria não comprar nada, e ele decidiria sobre um número que ninguém calculou.
/// Quem renderiza precisa distinguir os dois casos; quem escreve não pode "simplificar"
/// o nulo para zero.
/// </para>
/// </summary>
public sealed class ComparacaoSessaoItem
{
    public Guid SessaoId { get; set; }
    public int LojaId { get; set; }
    public required string Sku { get; set; }
    public string? NomeProduto { get; set; }
    public string? Curva { get; set; }

    public decimal CompraSugeridaPbs { get; set; }

    /// <summary>
    /// Quanto o braço de ML teria mandado comprar. Nulo quando a camada B não decidiu por
    /// este item — cobertura além do horizonte do ML, aritmética do ERP não reproduzida
    /// (<c>StatusReconciliacao.Divergente</c>), janela descartada por ruptura, ou item
    /// que nem chegou à população da camada. Ver a nota da classe.
    /// </summary>
    public decimal? CompraSugeridaMl { get; set; }

    public decimal VendidoNaJanela { get; set; }

    public decimal DemandaDiaPbs { get; set; }

    /// <summary>
    /// Previsão de demanda diária do ML, vinda da camada A. Nula quando o item não entrou
    /// naquela camada: série incompleta nos dias pontuados, SKU fora do orçamento de
    /// SKUs do treino, ou janela avançando além do histórico importado.
    ///
    /// <para>
    /// <b>Independente de <see cref="CompraSugeridaMl"/>:</b> a camada A pontua uma taxa
    /// dentro dos 7 dias que o pipeline alcança, então ela costuma existir exatamente nos
    /// casos em que a decisão do ML não existe. Preenchida com a decisão nula é o estado
    /// normal de hoje, não uma inconsistência.
    /// </para>
    /// </summary>
    public decimal? DemandaDiaMl { get; set; }

    /// <summary>
    /// Demanda diária real da janela que a camada A pontuou, apurada com a política de
    /// ruptura dela. Nula pelo mesmo motivo de <see cref="DemandaDiaMl"/> — as duas saem
    /// do mesmo par avaliado —, e não por falta de venda: zero aqui afirmaria que o item
    /// não vendeu nada por dia, o que é uma medição e não uma ausência. A venda observada
    /// na cobertura inteira continua em <see cref="VendidoNaJanela"/>, que não depende da
    /// camada A.
    /// </summary>
    public decimal? DemandaDiaReal { get; set; }

    public decimal SobraPbsUnidades { get; set; }

    /// <summary>
    /// Sobra do braço de ML em unidades. Nula sempre que <see cref="CompraSugeridaMl"/> é
    /// nula: sem a compra do ML não existe posição de estoque contrafactual para comparar
    /// com a venda real.
    /// </summary>
    public decimal? SobraMlUnidades { get; set; }

    public decimal SobraPbsValor { get; set; }

    /// <summary>
    /// Sobra do braço de ML em R$. Nula junto com <see cref="SobraMlUnidades"/> — e note
    /// que zero aqui é valor legítimo e diferente de nulo: item sem <c>PrecoCompra</c>
    /// cadastrado tem sobra em unidades e zero em reais (<c>SobraCalculator</c>).
    /// </summary>
    public decimal? SobraMlValor { get; set; }
}
