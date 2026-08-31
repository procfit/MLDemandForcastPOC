namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Retrato do cadastro de produtos da rede, reduzido ao que a comparação com a IQVIA
/// precisa: o código de barras. Uma linha por EAN.
///
/// <para>
/// <b>Vive no banco `engine`, e não no Stage, por ciclo de vida.</b> A tela de
/// oportunidades não pertence a sessão nenhuma, e cada import apaga o Stage da rede
/// (<c>DELETE ... WHERE RedeId</c>) — no Stage este catálogo zeraria no envio seguinte, e
/// a tela passaria a oferecer o cadastro inteiro como oportunidade de sortimento. Uma
/// tabela de Stage que ninguém lê é exatamente o defeito que a F16 corrigiu ao remover
/// <c>MercadoIqvia</c>.
/// </para>
///
/// <para>
/// <b>Substituição por <see cref="RedeId"/> inteiro a cada envio</b>, e não por chave
/// parcial: é retrato, não série histórica. Produto que a rede descadastrou tem de sair,
/// senão a tela deixa de oferecer como oportunidade algo que ela já teve. Diferente de
/// <see cref="MercadoObservacao"/>, cuja recarga é por (mês, brick) justamente para
/// preservar a série que vários arquivos empilham.
/// </para>
///
/// <para>
/// <b>Não substitui <c>Stage.Produtos</c>.</b> Aquele é escopado aos SKUs da sugestão e
/// carrega a hierarquia comercial; este é o mestre inteiro e só tem o código. As duas
/// coisas respondem perguntas diferentes: "como este item da compra se comportou" e "este
/// item que o mercado vende existe no meu cadastro".
/// </para>
///
/// <para>
/// <b>Só produtos com código utilizável entram.</b> Medido na Natusfarma em 2026-08-31:
/// dos 79.873 registros, 47.658 estão inativos e quase nenhum tem EAN — dos 29.068 com
/// código utilizável, 29.053 são ativos. Filtrar na origem tira dois terços do arquivo sem
/// perder informação: registro sem código não responde a pergunta desta tabela.
/// </para>
/// </summary>
public sealed class RedeCatalogoEan
{
    public int RedeId { get; set; }

    /// <summary>
    /// Só dígitos, sem zeros à esquerda — normalizado na gravação pela mesma regra do sinal
    /// de mercado. O PBS grava 14 caracteres com zero à esquerda (<c>07896094928060</c>) e a
    /// IQVIA grava 13 (<c>7891721201806</c>); comparação exata casa <b>zero</b>, e a falha é
    /// silenciosa — dicionário vazio, nenhuma exceção, e a tela oferecendo o cadastro
    /// inteiro como oportunidade.
    /// </summary>
    public required string Ean { get; set; }

    /// <summary>Código do produto no ERP, para a tela poder citar o cadastro da rede.</summary>
    public required string Sku { get; set; }

    /// <summary>
    /// Nome no cadastro da rede. Serve a estimar o falso positivo por casamento de nome —
    /// produto que a rede tem sob cadastro sem código de barras — se alguma rede medir
    /// cobertura de EAN baixa nas seções que a IQVIA cobre. Na Natusfarma essa cobertura é
    /// de 98,6% a 99,4% e a estimativa não foi necessária.
    /// </summary>
    public string? Nome { get; set; }
}
