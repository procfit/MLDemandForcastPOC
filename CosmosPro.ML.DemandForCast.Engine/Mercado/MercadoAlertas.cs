namespace CosmosPro.ML.DemandForCast.Engine.Mercado;

/// <summary>
/// Os valores que <c>ComparacaoSessaoItem.MercadoAlerta</c> aceita (regras B2, B3 e B6 do
/// documento de controle da IQVIA). Vive no Engine, e não no Worker, porque quem escreve
/// (Worker, na materialização) e quem filtra (ApiService, na tela de itens) precisam do
/// mesmo texto — divergir aqui produziria um filtro que nunca casa, sem erro nenhum e sem
/// nada na tela denunciando.
///
/// <para>
/// <b>Nulo não está nesta lista, de propósito.</b> Nulo significa <b>não avaliado</b> —
/// falta dado de mercado para o item —, enquanto <see cref="SemAlerta"/> significa
/// <b>avaliado e dentro do esperado</b>. São afirmações diferentes, e é a coluna que tem de
/// distingui-las: quem lê a tela não pode precisar cruzar com outra coluna para saber se
/// houve medição.
/// </para>
/// </summary>
public static class MercadoAlertas
{
    /// <summary>Índice de desempenho igual ou acima do limiar. Avaliado, e está bem.</summary>
    public const string SemAlerta = nameof(SemAlerta);

    /// <summary>Abaixo do limiar, e a loja ficou sem estoque no mês comparado (regra B3).</summary>
    public const string Ruptura = nameof(Ruptura);

    /// <summary>
    /// Abaixo do limiar, e havia estoque todos os dias do mês comparado — nenhuma hipótese
    /// de disponibilidade explica (regra B6). Vai para análise do comprador.
    /// </summary>
    public const string SemCausa = nameof(SemCausa);

    /// <summary>
    /// Abaixo do limiar, mas o mês comparado não está no histórico de estoque importado.
    /// <b>Não é <see cref="SemCausa"/>:</b> aquele afirma que não houve ruptura, e aqui
    /// ninguém verificou. Colapsar os dois faria o software afirmar sobre estoque que não
    /// olhou.
    /// </summary>
    public const string NaoApurado = nameof(NaoApurado);

    /// <summary>
    /// Tamanho declarado da coluna no banco. Valor novo que passe daqui estoura o
    /// <c>SqlBulkCopy</c> da materialização — três fases depois de quem o introduziu —,
    /// então há um teste afirmando que todos cabem.
    /// </summary>
    public const int TamanhoMaximo = 20;
}
