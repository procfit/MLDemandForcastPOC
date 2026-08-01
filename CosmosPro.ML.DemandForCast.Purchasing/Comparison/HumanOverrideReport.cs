namespace CosmosPro.ML.DemandForCast.Purchasing.Comparison;

/// <summary>
/// Uma linha de <c>SugestoesCompraItens</c> reduzida aos campos que a Camada C compara: o
/// que o ERP sugeriu contra o que o comprador autorizou. <c>RedeId</c>/<c>SugestaoId</c>/
/// <c>LojaId</c>/<c>Sku</c> só servem para detectar linha duplicada — não entram em
/// nenhuma conta.
/// </summary>
public sealed record HumanOverrideItem
{
    public required int RedeId { get; init; }

    public required long SugestaoId { get; init; }

    public required int LojaId { get; init; }

    public required string Sku { get; init; }

    /// <summary>
    /// Curva de giro do ERP (A..E). Nula, vazia ou só espaço cai no balde
    /// <see cref="HumanOverrideReport.CurvaSemClassificacao"/> — nunca some do relatório.
    /// </summary>
    public string? Curva { get; init; }

    /// <summary><c>CompraSugerida</c> — o que o ERP mandou comprar.</summary>
    public required decimal CompraSugerida { get; init; }

    /// <summary><c>CompraAutorizada</c> — o que o comprador aprovou.</summary>
    public required decimal CompraAutorizada { get; init; }

    /// <summary>
    /// <c>PrecoCompra</c>. Nulo exclui a linha de todos os agregados ponderados por valor
    /// — ver <see cref="HumanOverrideResult.ItensSemPreco"/> — mas ela continua entrando
    /// nos agregados não ponderados.
    /// </summary>
    public decimal? PrecoCompra { get; init; }
}

/// <summary>
/// Frações e desvio médio numa base comum: contagem de itens no não ponderado, soma de
/// valor em R$ das linhas com preço no ponderado. Os dois lados desta classe têm os
/// mesmos campos porque a única coisa que muda entre eles é o peso de cada linha — 1 no
/// não ponderado, <c>PrecoCompra × valor-de-referência</c> no ponderado (ver
/// <see cref="HumanOverrideReport"/> para a definição de valor-de-referência).
/// </summary>
/// <param name="Base">
/// Denominador de todas as frações abaixo. Zero faz as frações saírem nulas em vez de
/// dividir por zero — o caso real é uma população inteira sem <c>PrecoCompra</c>.
/// </param>
/// <param name="ComDenominador">
/// Quanto de <paramref name="Base"/> tem <c>CompraSugerida &gt; 0</c> e portanto entra nas
/// médias de desvio relativo abaixo. Linha de adição (<c>CompraSugerida = 0</c>) não tem
/// denominador — dividir por zero não é definido, e por isso ela é excluída das médias em
/// vez de virar infinito ou NaN. Uma população só de adições deixa
/// <see cref="DesvioRelativoMedioAbsoluto"/> e <see cref="DesvioRelativoMedioAssinado"/>
/// nulos, nunca NaN.
/// </param>
/// <param name="ComOverride"><c>CompraAutorizada &lt;&gt; CompraSugerida</c>. Inclui vetos e adições.</param>
/// <param name="Vetos"><c>CompraAutorizada = 0</c> com <c>CompraSugerida &gt; 0</c>: o comprador recusou a compra inteira.</param>
/// <param name="Adicoes"><c>CompraSugerida = 0</c> com <c>CompraAutorizada &gt; 0</c>: o comprador comprou algo que o ERP não pediu.</param>
/// <param name="AjustesParaCima">
/// <c>CompraAutorizada &gt; CompraSugerida</c>, ambos positivos — override comum para
/// cima. Exclui veto e adição: eles têm bucket próprio mesmo sendo, aritmeticamente, um
/// "para baixo" ou "para cima" extremo, porque recusar a compra inteira ou comprar do
/// nada é qualitativamente diferente de ajustar uma quantidade.
/// </param>
/// <param name="AjustesParaBaixo">Espelho de <paramref name="AjustesParaCima"/>: <c>CompraAutorizada &lt; CompraSugerida</c>, ambos positivos.</param>
/// <param name="DesvioRelativoMedioAbsoluto">
/// Média de <c>|CompraAutorizada − CompraSugerida| ÷ CompraSugerida</c> sobre
/// <paramref name="ComDenominador"/> linhas. Nula sem nenhuma linha com denominador.
/// Reportada sempre ao lado de <see cref="DesvioRelativoMedioAssinado"/>: overrides que se
/// cancelam (uma linha +50%, outra −50%) fariam o desvio assinado sair perto de zero
/// enquanto o absoluto continua alto — só os dois juntos distinguem "sem intervenção" de
/// "intervenção que se cancela no agregado".
/// </param>
/// <param name="DesvioRelativoMedioAssinado">
/// Média do mesmo desvio sem valor absoluto: positivo = compradores compram
/// sistematicamente mais que o ERP sugere; negativo = sistematicamente menos.
/// </param>
public sealed record HumanOverrideFigures(
    decimal Base,
    decimal ComDenominador,
    decimal ComOverride,
    decimal Vetos,
    decimal Adicoes,
    decimal AjustesParaCima,
    decimal AjustesParaBaixo,
    double? DesvioRelativoMedioAbsoluto,
    double? DesvioRelativoMedioAssinado)
{
    public double? FracaoOverride => Base == 0m ? null : (double)(ComOverride / Base);

    public double? FracaoVetos => Base == 0m ? null : (double)(Vetos / Base);

    public double? FracaoAdicoes => Base == 0m ? null : (double)(Adicoes / Base);

    public double? FracaoAjustesParaCima => Base == 0m ? null : (double)(AjustesParaCima / Base);

    public double? FracaoAjustesParaBaixo => Base == 0m ? null : (double)(AjustesParaBaixo / Base);
}

/// <summary>
/// Recorte de <see cref="HumanOverrideResult"/> numa curva de giro do ERP. Existe porque
/// uma taxa de override global baixa pode esconder uma curva inteira sendo reescrita pelo
/// comprador (CLAUDE.md §6: média global esconde regressão local) — plausivelmente mais
/// comum nas curvas de giro alto que na cauda.
/// </summary>
public sealed record HumanOverrideResumoCurva(
    string Curva,
    int Itens,
    int ItensSemPreco,
    HumanOverrideFigures NaoPonderado,
    HumanOverrideFigures? Ponderado);

/// <summary>
/// Camada C do comparativo F13: intervenção humana. Compara só dois números que o próprio
/// ERP gravou, <c>CompraSugerida</c> e <c>CompraAutorizada</c> — sem braço de ML, sem
/// venda real e, portanto, sem pergunta de ruptura ou de corte de informação. As Camadas A
/// e B (<c>Forecasting/Comparison</c>, <c>Purchasing/Comparison/DecisionComparer</c>)
/// carregam esse aparato porque comparam previsão contra previsão; aqui não há previsão
/// nenhuma dos dois lados, só a distância entre uma sugestão e uma aprovação.
///
/// <para>
/// <b>Isto é estatística descritiva, não avaliação de acurácia.</b> Um override não prova
/// que o ERP errou — pode ser o comprador que errou, ou pode ser informação que nem o ERP
/// nem nenhum modelo enxergariam (um acordo pontual de fornecedor, um concorrente
/// fechando, um surto local). Ver o aviso completo, e a razão de ele existir, em
/// <see cref="HumanOverrideResult"/>.
/// </para>
///
/// <para>
/// <b>Valor-de-referência de uma linha</b> (peso dos agregados ponderados e base da
/// ponderação do desvio relativo): <c>PrecoCompra × CompraSugerida</c> quando
/// <c>CompraSugerida &gt; 0</c>, e <c>PrecoCompra × CompraAutorizada</c> numa linha de
/// adição, onde <c>CompraSugerida</c> é zero e não serviria como valor de nada. Ou seja: o
/// valor em jogo é o da sugestão do ERP quando ele sugeriu algo, e o do que o comprador de
/// fato comprou quando o ERP não sugeriu nada.
/// </para>
/// </summary>
public static class HumanOverrideReport
{
    /// <summary>Balde de <see cref="HumanOverrideItem.Curva"/> nula, vazia ou só espaço.</summary>
    public const string CurvaSemClassificacao = "(sem curva)";

    private const string ParamPopulacao = "populacao";

    public static HumanOverrideResult Compute(IReadOnlyList<HumanOverrideItem> populacao)
    {
        ValidarPopulacao(populacao);

        var (naoPonderado, ponderado, itensSemPreco) = Agregar(populacao);

        var porCurva = populacao
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Curva) ? CurvaSemClassificacao : i.Curva!)
            .ToDictionary(g => g.Key, g =>
            {
                var itensDaCurva = g.ToList();
                var (naoPond, pond, semPreco) = Agregar(itensDaCurva);
                return new HumanOverrideResumoCurva(g.Key, itensDaCurva.Count, semPreco, naoPond, pond);
            });

        return new HumanOverrideResult(populacao.Count, itensSemPreco, naoPonderado, ponderado, porCurva);
    }

    private static (HumanOverrideFigures NaoPonderado, HumanOverrideFigures? Ponderado, int ItensSemPreco) Agregar(
        IReadOnlyList<HumanOverrideItem> itens)
    {
        var total = itens.Count;
        int comDenominador = 0, comOverride = 0, vetos = 0, adicoes = 0, paraCima = 0, paraBaixo = 0;
        double somaAbs = 0, somaAssinada = 0;

        var itensSemPreco = 0;
        decimal valorTotal = 0m, valorComDenominador = 0m, valorComOverride = 0m,
            valorVetos = 0m, valorAdicoes = 0m, valorParaCima = 0m, valorParaBaixo = 0m;
        double valorSomaAbs = 0, valorSomaAssinada = 0;

        foreach (var item in itens)
        {
            var categoria = Classificar(item);
            if (categoria != Categoria.SemOverride) comOverride++;

            switch (categoria)
            {
                case Categoria.Veto: vetos++; break;
                case Categoria.Adicao: adicoes++; break;
                case Categoria.ParaCima: paraCima++; break;
                case Categoria.ParaBaixo: paraBaixo++; break;
            }

            double? desvio = null;
            if (item.CompraSugerida > 0m)
            {
                comDenominador++;
                desvio = (double)((item.CompraAutorizada - item.CompraSugerida) / item.CompraSugerida);
                somaAbs += Math.Abs(desvio.Value);
                somaAssinada += desvio.Value;
            }

            if (item.PrecoCompra is not { } preco)
            {
                itensSemPreco++;
                continue;
            }

            var valorReferencia = item.CompraSugerida > 0m ? item.CompraSugerida : item.CompraAutorizada;
            var valor = preco * valorReferencia;
            valorTotal += valor;

            if (categoria != Categoria.SemOverride) valorComOverride += valor;
            switch (categoria)
            {
                case Categoria.Veto: valorVetos += valor; break;
                case Categoria.Adicao: valorAdicoes += valor; break;
                case Categoria.ParaCima: valorParaCima += valor; break;
                case Categoria.ParaBaixo: valorParaBaixo += valor; break;
            }

            if (desvio is { } d)
            {
                valorComDenominador += valor;
                valorSomaAbs += (double)valor * Math.Abs(d);
                valorSomaAssinada += (double)valor * d;
            }
        }

        var naoPonderado = new HumanOverrideFigures(
            total,
            comDenominador,
            comOverride,
            vetos,
            adicoes,
            paraCima,
            paraBaixo,
            comDenominador == 0 ? null : somaAbs / comDenominador,
            comDenominador == 0 ? null : somaAssinada / comDenominador);

        HumanOverrideFigures? ponderado = valorTotal == 0m
            ? null
            : new HumanOverrideFigures(
                valorTotal,
                valorComDenominador,
                valorComOverride,
                valorVetos,
                valorAdicoes,
                valorParaCima,
                valorParaBaixo,
                valorComDenominador == 0m ? null : valorSomaAbs / (double)valorComDenominador,
                valorComDenominador == 0m ? null : valorSomaAssinada / (double)valorComDenominador);

        return (naoPonderado, ponderado, itensSemPreco);
    }

    /// <summary>
    /// Exaustiva e mutuamente exclusiva sobre os cinco casos possíveis de dois decimais
    /// não negativos. Veto e adição são checados antes dos demais para que uma recusa
    /// total ou uma compra do nada nunca também conte como ajuste comum para
    /// baixo/para cima.
    /// </summary>
    private static Categoria Classificar(HumanOverrideItem item)
    {
        if (item.CompraAutorizada == item.CompraSugerida) return Categoria.SemOverride;
        if (item.CompraSugerida > 0m && item.CompraAutorizada == 0m) return Categoria.Veto;
        if (item.CompraSugerida == 0m && item.CompraAutorizada > 0m) return Categoria.Adicao;
        return item.CompraAutorizada > item.CompraSugerida ? Categoria.ParaCima : Categoria.ParaBaixo;
    }

    private static void ValidarPopulacao(IReadOnlyList<HumanOverrideItem> populacao)
    {
        var chaves = new HashSet<(int RedeId, long SugestaoId, int LojaId, string Sku)>();

        foreach (var item in populacao)
        {
            if (item.CompraSugerida < 0m || item.CompraAutorizada < 0m)
                throw new ArgumentException(
                    $"Quantidade negativa no item (rede {item.RedeId}, sugestão {item.SugestaoId}, " +
                    $"loja {item.LojaId}, sku {item.Sku}): CompraSugerida {item.CompraSugerida}, " +
                    $"CompraAutorizada {item.CompraAutorizada}.",
                    ParamPopulacao);

            if (!chaves.Add((item.RedeId, item.SugestaoId, item.LojaId, item.Sku)))
                throw new ArgumentException(
                    $"A população repete o item (rede {item.RedeId}, sugestão {item.SugestaoId}, " +
                    $"loja {item.LojaId}, sku {item.Sku}). Linha duplicada pesa duas vezes nas " +
                    "frações e nas médias — sintoma de join que multiplicou linhas.",
                    ParamPopulacao);
        }
    }

    private enum Categoria { SemOverride, Veto, Adicao, ParaCima, ParaBaixo }
}

/// <summary>
/// Resultado da Camada C: a distância entre <c>CompraSugerida</c> e
/// <c>CompraAutorizada</c> sobre a população que o comprador de fato avaliou. A população
/// é a que o chamador passar — esta classe nunca a alarga nem a filtra, mesma regra de
/// população das Camadas A e B.
///
/// <para>
/// <b>Isto não é evidência sobre a qualidade da previsão do ERP, nem do ML.</b> Um
/// override mede que o comprador discordou do ERP, não quem estava certo: a discordância
/// pode vir de um erro do comprador, ou de informação que nem o ERP nem nenhum modelo
/// enxergariam no momento — um acordo pontual de fornecedor, um concorrente fechando, um
/// surto local de demanda. Este projeto já promoveu uma comparação a mais do que ela media
/// e teve de aposentá-la (ver <c>DecisionComparer</c>, motivação da reconciliação); esta
/// classe é deliberadamente descritiva, e uma UI que a consumir não deve apresentar estes
/// números como veredito sobre a acurácia de ninguém — só sobre o tamanho da intervenção
/// humana no processo hoje.
/// </para>
/// </summary>
public sealed record HumanOverrideResult(
    int ItensNaPopulacao,
    int ItensSemPreco,
    HumanOverrideFigures NaoPonderado,
    HumanOverrideFigures? Ponderado,
    IReadOnlyDictionary<string, HumanOverrideResumoCurva> PorCurva);
