namespace CosmosPro.ML.DemandForCast.Engine.Sessoes;

/// <summary>
/// A régua do sinal de cor da coluna "Análise rápida", definida pelo patrocinador em
/// 05/09/2026: <b>vermelho</b> a partir de 60 dias de cobertura, <b>amarelo</b> de 30 a 59,
/// <b>verde</b> abaixo de 30.
///
/// <para>
/// <b>Mora no Engine, e não na Web, porque tem dois consumidores em linguagens diferentes.</b>
/// A tela classifica em C# — <see cref="Classificar"/> — e o filtro do servidor classifica em
/// SQL, dentro de uma árvore de expressão que não consegue chamar método. Se os cortes
/// existissem nos dois lugares como literais, um dia alguém mexeria num e não no outro: a
/// tabela pintaria a linha de amarelo e o filtro "só encalhados" a traria junto, sem erro
/// nenhum em nenhum dos dois lados. As constantes daqui são a única fonte.
/// </para>
///
/// <para>
/// A cobertura que alimenta a régua é o <b>estoque do fim do período</b> dividido pela venda
/// média de 120 dias — resposta 1A do patrocinador. Com o estoque do dia da sugestão a mesma
/// régua acusaria outra coisa: "comprou tendo muito" em vez de "a compra deixou capital
/// parado". Ao trocar o numerador, a régua precisa ser rediscutida.
/// </para>
/// </summary>
public static class AnaliseRapida
{
    /// <summary>Dias de cobertura a partir dos quais o item é encalhe.</summary>
    public const decimal VermelhoAPartirDe = 60m;

    /// <summary>Dias de cobertura a partir dos quais o item pede atenção.</summary>
    public const decimal AmareloAPartirDe = 30m;

    public const string Vermelho = nameof(Vermelho);
    public const string Amarelo = nameof(Amarelo);
    public const string Verde = nameof(Verde);

    /// <summary>
    /// Estoque parado com <b>zero venda</b> em 120 dias. Estado próprio por decisão do
    /// patrocinador (resposta 2B): não há cobertura a calcular — a divisão não existe —, mas
    /// está longe de estar bem. Sem estado próprio ele desapareceria justamente por ser ruim
    /// demais para a fórmula. E não é um vermelho mais fraco: o vermelho diz "sobrou demais
    /// para o giro", este diz "não há giro nenhum".
    /// </summary>
    public const string SemGiro = nameof(SemGiro);

    /// <summary>
    /// Quantos dias o estoque duraria no ritmo dos últimos 120 dias, ou <c>null</c> quando não
    /// há conta a fazer.
    ///
    /// <para>
    /// Estoque zero é cobertura <b>zero</b>, e não ausência: prateleira vazia é uma medição.
    /// Venda média zero com estoque na prateleira devolve nulo, porque a divisão não existe —
    /// quem distingue esse caso de "sem dado" é <see cref="Classificar"/>.
    /// </para>
    /// </summary>
    public static decimal? Cobertura(decimal? estoqueNoFim, decimal? vendaMediaDiaria) =>
        estoqueNoFim is not { } estoque || vendaMediaDiaria is not { } media ? null
        : estoque == 0m ? 0m
        : media == 0m ? null
        : estoque / media;

    /// <summary>
    /// O estado da análise rápida, ou <c>null</c> para <b>não avaliado</b> — falta estoque
    /// medido ou histórico para a média. Nulo nunca se confunde com <see cref="Verde"/>: um diz
    /// "está bem", o outro diz "ninguém olhou".
    /// </summary>
    public static string? Classificar(decimal? estoqueNoFim, decimal? vendaMediaDiaria) =>
        estoqueNoFim is not { } estoque || vendaMediaDiaria is not { } media ? null
        : estoque == 0m ? Verde
        : media == 0m ? SemGiro
        : Cobertura(estoque, media) is { } dias
            ? dias >= VermelhoAPartirDe ? Vermelho : dias >= AmareloAPartirDe ? Amarelo : Verde
            : null;
}
