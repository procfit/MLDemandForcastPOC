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

    /// <summary>
    /// Categoria do produto, copiada de <c>Produtos.Categoria</c> na materialização — pelo
    /// mesmo motivo de <see cref="NomeProduto"/>: o Stage da rede é apagado no import
    /// seguinte, então resolver a categoria consultando o Stage na hora de filtrar devolveria
    /// a categoria do envio seguinte, ou nada.
    ///
    /// <para>
    /// Nulo significa <b>"o cadastro do PBS não tem categoria para este SKU"</b> — nunca "sem
    /// filtro" e nunca "todas". Sessões materializadas antes desta coluna existir também têm
    /// nulo, e a tela precisa dizer isso em vez de exibir uma lista de categorias vazia como
    /// se a sessão não tivesse itens.
    /// </para>
    ///
    /// <para>
    /// <b>Não existe coluna de departamento</b>, pedida junto com esta: o contrato de
    /// extração do PBS não traz o campo, nem <c>Stage.Produtos</c> o tem. Inventá-lo a partir
    /// da categoria seria dado fabricado numa tela de decisão de compra.
    /// </para>
    /// </summary>
    public string? Categoria { get; set; }

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

    /// <summary>
    /// Sobra do braço do ERP em R$. <b>Nula quando o item não tem <c>PrecoCompra</c>
    /// cadastrado no Stage</b>, e não zero: as unidades sobraram, só não se sabe quanto
    /// capital elas representam. Zero afirmaria "esta compra não deixou capital parado" —
    /// exatamente o oposto — e é a coluna pela qual o comprador ordena a tabela para achar o
    /// pior item, onde os sem preço migrariam para o fim da lista como se fossem os melhores.
    /// A manchete continua somando esses itens como zero, e por isso declara quantos são em
    /// <c>SessaoResultado.ItensSemPrecoCompra</c>.
    /// </summary>
    public decimal? SobraPbsValor { get; set; }

    /// <summary>
    /// Sobra do braço de ML em R$. Nula quando <see cref="SobraMlUnidades"/> é nula
    /// (não houve decisão do ML) <b>ou</b> quando o item não tem <c>PrecoCompra</c>, pela
    /// mesma razão de <see cref="SobraPbsValor"/>. Quem precisa saber se o braço de ML existe
    /// para a linha olha <see cref="CompraSugeridaMl"/>, que não depende de preço.
    /// </summary>
    public decimal? SobraMlValor { get; set; }

    /// <summary>
    /// A cobertura desta linha (<c>DiasEstoque</c> dias a partir do dia da sugestão) avança
    /// além do último dia de venda importado, então <see cref="VendidoNaJanela"/> está
    /// subcontada e as sobras, infladas — a linha <b>não</b> é comparável com as demais.
    ///
    /// <para>
    /// Gravada por linha, e não só contada no agregado, porque é o único momento em que se
    /// sabe: o <c>DiasEstoque</c> e a última data de venda vivem no Stage, que o próximo
    /// import apaga (<c>DELETE ... WHERE RedeId</c>). Sem esta coluna, marcar a linha depois
    /// exigiria migration <b>e</b> reimportação do mesmo ZIP. Quem renderiza a tabela precisa
    /// sinalizar a linha, porque o comprador confere item a item contra a memória dele.
    /// </para>
    /// </summary>
    public bool JanelaAlemDoHistorico { get; set; }

    // --- Sinal de mercado da IQVIA (F16 parte C, grupo B) ----------------------------
    //
    // Nulo nas sete significa "não foi possível calcular", nunca zero — o mesmo contrato
    // das colunas do braço de ML, e pelo mesmo motivo. Cinco causas legítimas: loja sem
    // Cnpj no Stage, CNPJ fora do painel da IQVIA, SKU sem Ean, EAN que a IQVIA não
    // reportou, e nenhum mês coberto antes do mês da sugestão.

    /// <summary>
    /// Mês da IQVIA que este item comparou (primeiro dia do mês). É sempre <b>estritamente
    /// anterior</b> ao mês da sugestão: o mês da sugestão contém as consequências dela, e
    /// comparar contra ele tornaria circular a afirmação de que o alerta teria avisado o
    /// comprador.
    ///
    /// <para>
    /// Gravado por linha porque a tela precisa dizer contra o que comparou. A cobertura da
    /// rede muda conforme novos relatórios entram, e uma sessão antiga tem de continuar
    /// declarando o mês que ela realmente usou.
    /// </para>
    /// </summary>
    public DateOnly? MercadoMes { get; set; }

    /// <summary>Brick da IQVIA da loja, resolvido pelo CNPJ dela no painel.</summary>
    public string? MercadoBrick { get; set; }

    /// <summary>
    /// Unidades que a IQVIA atribuiu às bandeiras próprias da rede, neste EAN, neste brick e
    /// mês. <b>Zero é medição</b>, não ausência — e zero aqui com
    /// <see cref="MercadoUnidadesConcorrentes"/> positivo é o alerta mais forte que existe: o
    /// item está no cadastro, está na sugestão, o bairro vende, e a rede vendeu nada.
    /// </summary>
    public decimal? MercadoUnidadesRede { get; set; }

    /// <summary>Unidades do agregado de concorrentes, no mesmo recorte.</summary>
    public decimal? MercadoUnidadesConcorrentes { get; set; }

    /// <summary>
    /// Fatia da rede neste item dividida pela fatia agregada da rede no mesmo brick e mês.
    /// 1,0 = o item vai tão bem quanto a rede vai naquele bairro; abaixo de 0,5 dispara
    /// alerta (regra B2).
    ///
    /// <para>
    /// <b>Não é fatia de mercado</b> — é desempenho relativo à própria rede, justamente para
    /// o tamanho dela no bairro não contaminar a leitura. A régua por número de lojas
    /// exigiria o contador de PDVs concorrentes, que o relatório da IQVIA publica apenas
    /// numa área de tabela dinâmica que o parser não lê.
    /// </para>
    /// </summary>
    public decimal? MercadoIndiceDesempenho { get; set; }

    /// <summary>
    /// Dias em que a loja ficou sem estoque deste SKU <b>no mês comparado</b> — não na
    /// janela de cobertura da sugestão. É a evidência da regra B3.
    ///
    /// <para>
    /// Nulo quando aquele mês não está no histórico de estoque importado, o que é diferente
    /// de zero: zero afirma que havia estoque todos os dias, e é o que separa
    /// <c>MercadoAlertas.SemCausa</c> de <c>MercadoAlertas.NaoApurado</c>.
    /// </para>
    /// </summary>
    public int? MercadoDiasSemEstoque { get; set; }

    /// <summary>
    /// Classificação do alerta: um dos valores de <c>MercadoAlertas</c>. Nulo significa
    /// <b>não avaliado</b> (sem dado de mercado para o item), e não "está tudo bem" — para
    /// isso existe <c>MercadoAlertas.SemAlerta</c>. Quem renderiza precisa distinguir os
    /// dois: o comprador não pode ler ausência de medição como aprovação.
    /// </summary>
    public string? MercadoAlerta { get; set; }
}
