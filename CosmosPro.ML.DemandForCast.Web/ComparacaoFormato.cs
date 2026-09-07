namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// Formatação de par de métricas em que a tela também declara um vencedor.
///
/// <para>
/// <b>O problema que isto resolve, relatado pelo patrocinador em 07/09/2026:</b> a tela mostrava
/// MAE <c>0,08</c> nos dois lados e escrevia "menor erro: PBS". Não era erro de conta — o
/// veredito compara os valores crus, e <c>0,0812</c> é de fato menor que <c>0,0849</c> —, era a
/// <b>exibição</b> arredondando para duas casas e escondendo justamente a diferença em que o
/// veredito se apoia. Para quem lê, o software afirmava com autoridade de número algo que os
/// números na tela contradiziam.
/// </para>
///
/// <para>
/// A correção é derivar a precisão da comparação, e não fixá-la: <see cref="CasasParaDistinguir"/>
/// devolve o menor número de casas em que os dois valores <b>deixam de parecer iguais</b>. Assim
/// a tela não tem como declarar vencedor entre dois números idênticos — se ela nomeia um lado, a
/// diferença está visível na mesma linha. Aumentar o padrão para três ou quatro casas não
/// resolveria: só empurraria a colisão para o próximo par de valores próximos.
/// </para>
///
/// <para>
/// O teto de <see cref="MaximoDeCasas"/> existe porque a precisão serve à leitura, não à
/// aritmética: passado esse ponto a diferença é menor do que qualquer decisão de compra
/// consegue usar. O veredito continua saindo da igualdade exata dos valores crus — mudar o
/// critério aqui faria a coluna "menor erro" discordar de <c>SessaoFatia.MlPerde</c>, e as duas
/// leituras da mesma tela passariam a se contradizer.
/// </para>
/// </summary>
public static class ComparacaoFormato
{
    public const int MinimoDeCasas = 2;
    public const int MaximoDeCasas = 6;

    /// <summary>
    /// Menor casa decimal em que <paramref name="a"/> e <paramref name="b"/> não se parecem,
    /// entre <see cref="MinimoDeCasas"/> e <see cref="MaximoDeCasas"/>.
    ///
    /// <para>
    /// Devolve o mínimo quando não há par a distinguir: algum lado nulo (nada foi apurado) ou
    /// valores exatamente iguais (aí a tela declara empate, e casas extras só poluiriam).
    /// </para>
    /// </summary>
    public static int CasasParaDistinguir(
        double? a, double? b, Func<double, int, string> formatar)
    {
        if (a is not { } x || b is not { } y || x == y) return MinimoDeCasas;

        for (var casas = MinimoDeCasas; casas < MaximoDeCasas; casas++)
        {
            if (formatar(x, casas) != formatar(y, casas)) return casas;
        }

        return MaximoDeCasas;
    }

    /// <summary>Unidades por dia — o formato do MAE.</summary>
    public static string Unidades(double valor, int casas) =>
        valor.ToString($"N{casas}");

    /// <summary>
    /// Percentual — o formato do WAPE. Uma casa a menos que o pedido em unidades porque
    /// <c>P</c> já multiplica por 100: <c>P2</c> mostra duas casas depois do ponto num número
    /// que é cem vezes maior, então equivale a quatro casas na fração.
    /// </summary>
    public static string Percentual(double valor, int casas) =>
        valor.ToString($"P{Math.Max(1, casas - 1)}");
}
