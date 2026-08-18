namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Janela de dados que o ZIP precisa cobrir para uma sugestão datada em T.
/// <para>
/// Antes de T: histórico para o modelo aprender. O pipeline de features exige no
/// mínimo 34 dias por SKU×loja, mas 34 dias não pegam sazonalidade — usamos 12 meses.
/// Depois de T: até T + dias de cobertura, que é o período que a compra deveria
/// suprir e portanto o único que revela quem acertou.
/// </para>
/// <para>
/// <b>A cobertura vem do <c>DIAS_ESTOQUE</c> dos itens da sugestão</b> (o maior deles), não
/// do <c>DIAS_CURVA_*</c> do cabeçalho. Ver o comentário de
/// <c>catalogo_sugestoes_contagens.sql</c>: o campo do cabeçalho é do método 2 e vem zerado
/// em 83% das sugestões de eMax/eSeg.
/// </para>
/// </summary>
internal sealed record ExtractionWindow(
    DateOnly Inicio, DateOnly Fim, bool Viavel, string? MotivoInviabilidade, string? Ressalva = null)
{
    private const int MesesHistorico = 12;

    /// <summary>
    /// A janela em uma linha. Fonte única para o rótulo da tela
    /// (<see cref="DesfechoDaAnalise"/>) e para a confirmação da extração, que precisam
    /// dizer a mesma coisa — quem confirma está conferindo o que o rótulo prometeu.
    /// </summary>
    public string Descricao => $"janela de dados {Inicio:dd/MM/yyyy} a {Fim:dd/MM/yyyy}";

    /// <summary>
    /// Até onde o pipeline de previsão consegue prever, em dias. Espelha
    /// <c>DecisionOptions.HorizonteMaximoMl</c> do projeto Purchasing, que não é referenciado
    /// aqui de propósito: o extrator é WinForms e trazer o Purchasing arrastaria o ML.NET
    /// para dentro dele. Se o horizonte mudar lá, este número muda aqui.
    /// <para>
    /// O número existe porque prever o dia <c>T+H-1</c> sem enxergar o futuro exige que as
    /// features daquele dia parem em <c>T-1</c>, o que só acontece com lead time ≥ H — e as
    /// features hoje são construídas com 7 dias.
    /// </para>
    /// <para>
    /// <b>Não é mais um veto, e sim o gatilho da ressalva.</b> Ele recusava a extração, sob o
    /// argumento de que "todo item ficaria fora do horizonte e a comparação sairia vazia".
    /// O argumento estava errado: a camada A pontua <c>min(cobertura, lead time)</c> dias
    /// (<c>ComparacaoProcessor</c>) e produz número inteiro; só a camada B cai, e ela já tem
    /// contador e texto de comprador próprios do outro lado
    /// (<c>SessaoResultadoMontador.MotivoMlIndisponivel</c>). A frase que a recusa economizava
    /// custava toda sugestão de uma rede cujo ciclo de reposição é de 15 a 30 dias — que é o
    /// ciclo corrente do PBS, e portanto a rede inteira, não um caso de borda.
    /// </para>
    /// </summary>
    internal const int HorizonteMaximoMlDias = 7;

    public static ExtractionWindow Derive(DateOnly dataSugestao, int diasCobertura, DateOnly hoje)
    {
        var fim = dataSugestao.AddDays(diasCobertura);
        var inicio = dataSugestao.AddMonths(-MesesHistorico);

        // Cobertura zero ou negativa nao produz um unico dia de gabarito: a janela terminaria
        // no proprio dia da sugestao e nao haveria venda posterior contra a qual pontuar
        // ninguem. Isto aconteceu em producao — sugestao 21682, 879 MB extraidos, nada
        // comparavel — porque a cobertura era lida do campo errado e vinha zerada.
        if (diasCobertura <= 0)
        {
            return new ExtractionWindow(inicio, fim, false,
                $"Esta sugestão não declara dias de cobertura (DIAS_ESTOQUE dos itens é " +
                $"{diasCobertura}). Sem cobertura não existe período posterior à compra, e é " +
                "nesse período que as vendas mostram quem acertou. Escolha outra sugestão.");
        }

        // A cobertura tem de ter terminado: sem as vendas do periodo nao ha como
        // dizer quem acertou.
        if (fim > hoje)
        {
            var limite = hoje.AddDays(-diasCobertura);
            return new ExtractionWindow(inicio, fim, false,
                $"Esta sugestão é de {dataSugestao:dd/MM/yyyy} e cobre {diasCobertura} dias. " +
                $"As vendas que provariam quem acertou ainda não aconteceram. " +
                $"Escolha uma sugestão de até {limite:dd/MM/yyyy}.");
        }

        // Ressalva, não recusa: a extração vale a pena porque a camada A sobrevive inteira.
        // Ver o comentário de HorizonteMaximoMlDias.
        if (diasCobertura > HorizonteMaximoMlDias)
        {
            return new ExtractionWindow(inicio, fim, true, null,
                $"Esta sugestão cobre {diasCobertura} dias e o método de ML prevê " +
                $"{HorizonteMaximoMlDias}. A comparação de demanda por dia continua valendo, mas a " +
                "coluna de quanto o ML mandaria comprar vai sair em branco — é o limite atual do " +
                "modelo, não um problema do envio.");
        }

        return new ExtractionWindow(inicio, fim, true, null);
    }
}
