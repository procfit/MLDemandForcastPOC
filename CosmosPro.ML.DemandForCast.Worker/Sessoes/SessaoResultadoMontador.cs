using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Comparison;

namespace CosmosPro.ML.DemandForCast.Worker.Sessoes;

/// <summary>
/// Uma linha da sugestão do ERP acompanhada do que o Stage sabe sobre o desfecho dela.
/// </summary>
/// <param name="Item">A linha de <c>SugestoesCompraItens</c> — a população, que nunca é alargada.</param>
/// <param name="NomeProduto">
/// <c>Produtos.Nome</c>. Copiado para a materialização porque o Stage da rede é apagado no
/// próximo import: sem o nome gravado, a tabela do comprador vira uma lista de códigos.
/// </param>
/// <param name="VendidoNaJanela">
/// Unidades vendidas nos <c>DiasEstoque</c> dias de cobertura, a partir do dia da sugestão.
/// É a mesma janela que a camada B pontua.
/// </param>
/// <param name="DiasSemEstoque">
/// Dias da cobertura com snapshot de <c>EstoquesDiarios</c> zerado ou negativo.
/// </param>
/// <param name="DiasComSnapshot">
/// Dias da cobertura que têm snapshot de estoque. Menor que a cobertura significa que a
/// ruptura observada está subcontada por falta de dado, não por não ter havido falta —
/// distinção que a manchete precisa poder fazer.
/// </param>
/// <param name="JanelaAlemDoHistorico">
/// A cobertura deste item avança para além do último dia de venda importado. A venda da
/// janela sai necessariamente subcontada, e com ela a sobra sai inflada.
/// </param>
internal sealed record ItemDoStage(
    SugestaoItemStage Item,
    string? NomeProduto,
    decimal VendidoNaJanela,
    int DiasSemEstoque,
    int DiasComSnapshot,
    bool JanelaAlemDoHistorico);

/// <summary>Linhas a gravar e os agregados que as acompanham.</summary>
internal sealed record Materializacao(
    IReadOnlyList<ComparacaoSessaoItem> Itens,
    SessaoResultado Resultado);

/// <summary>
/// Desfecho de um braço sobre um conjunto de itens: o que ele mandou comprar, em unidades, e
/// o que sobrou na prateleira em unidades e em R$.
///
/// <para>
/// Não há compra em R$. A manchete confronta os dois braços pela <b>sobra</b> — é ela que
/// responde quanto capital ficou parado —, e o valor da compra não aparece em lugar nenhum da
/// tela. Persistir um número que ninguém lê o deixaria envelhecendo sem que qualquer teste ou
/// tela denunciasse um erro nele.
/// </para>
/// </summary>
internal sealed record BracoDaSessao(
    decimal CompraUnidades,
    decimal SobraUnidades,
    decimal SobraValor);

/// <summary>
/// Confronto braço a braço, restrito aos itens em que <b>os dois</b> braços existem.
///
/// <para>
/// Existe para impedir a comparação torta: o braço do ERP é conhecido para toda a
/// população, e o do ML só para o subconjunto que a camada B conseguiu decidir. Somar o
/// ERP sobre 30 mil itens e o ML sobre 40 e mostrar os dois lado a lado faria o ML parecer
/// dezenas de vezes melhor por ter sido medido em menos itens. Aqui os dois somam sobre
/// exatamente os mesmos <paramref name="Itens"/>.
/// </para>
/// </summary>
internal sealed record ConfrontoDaSessao(int Itens, BracoDaSessao Pbs, BracoDaSessao Ml);

/// <summary>
/// Ruptura <b>observada</b> na cobertura da sugestão, apurada de <c>EstoquesDiarios</c>.
///
/// <para>
/// Só existe para o que de fato aconteceu. Não há par para o braço de ML: quantos dias o
/// estoque teria ficado zerado sob a compra do ML depende de simular a posição dia a dia, e
/// nem a camada B produz isso — ela devolve falta em unidades, não em dias. Inventar o
/// número do lado do ML é justamente o que esta materialização não faz.
/// </para>
/// </summary>
/// <param name="DiasNaJanela">
/// Soma dos dias de cobertura de todos os itens — o denominador de
/// <paramref name="DiasComSnapshot"/>.
/// </param>
internal sealed record RupturaObservada(
    int ItensComDiaSemEstoque,
    int DiasSemEstoque,
    int DiasComSnapshot,
    int DiasNaJanela);

/// <summary>
/// Agregados da manchete da sessão, gravados em <c>ComparacaoSessao.ResultadoJson</c>.
///
/// <para>
/// <b>Cheque <see cref="Confronto"/> antes de qualquer frase de veredito.</b> Nulo significa
/// que nenhum item teve decisão de ML calculada, e o motivo está em
/// <see cref="MotivoMlIndisponivel"/> em português de comprador. Com a cobertura de 15 a 30
/// dias corrente no PBS contra o horizonte de 7 dias do pipeline, nulo é o desfecho
/// esperado hoje — e a tela precisa explicar isso em vez de mostrar células vazias.
/// </para>
/// </summary>
/// <param name="Pbs">
/// O braço do ERP sobre <b>toda</b> a população avaliada. É o desfecho real da compra que
/// foi feita, e vale por si mesmo mesmo sem contraparte de ML.
/// </param>
/// <param name="ItensComPrevisaoMl">
/// Itens com previsão de demanda do ML (camada A). Independente de
/// <see cref="ItensComDecisaoMl"/>: a camada A pontua uma taxa dentro do horizonte e
/// costuma existir justamente onde a decisão não existe.
/// </param>
/// <param name="ItensComJanelaAlemDoHistorico">
/// Itens cuja cobertura avança além do último dia de venda importado. Neles a venda da
/// janela está subcontada e a sobra, inflada. Zero é o caso normal de um envio gerado pelo
/// extrator, que monta o período em torno da sugestão. Cada linha afetada também vem
/// marcada em <see cref="ComparacaoSessaoItem.JanelaAlemDoHistorico"/>.
/// </param>
/// <param name="ItensSemPrecoCompra">
/// Itens sem <c>PrecoCompra</c> cadastrado no Stage. <b>Eles entram em
/// <see cref="Pbs"/> e em <see cref="Confronto"/> com unidades e zero em R$</b>, então toda
/// figura em reais desta manchete está subestimada nessa proporção. Sem este número a
/// subestimação seria invisível: a tela mostraria "R$ 12.400 sobraram" onde o total real é
/// bem maior, com a cara de figura completa. Quem renderiza <b>tem</b> de qualificar os
/// valores em R$ quando isto não é zero — dizendo quantos itens estão de fora e que o
/// remédio é cadastrar o preço de compra deles no ERP.
/// </param>
/// <param name="SkusSemCadastro">
/// SKUs da sugestão que o extrator não achou no cadastro de produtos do PBS, vindos do
/// manifesto do ZIP. Nulo quando o envio não declarou — ver
/// <see cref="ComparacaoSessao.SkusSemCadastro"/>.
/// </param>
/// <param name="RessalvaTreinoServe">
/// Ressalva metodológica copiada do resultado da comparação. Viaja com os números de
/// propósito: quem lê o número precisa ler a ressalva.
/// </param>
/// <remarks>
/// <b>Não há recorte por curva aqui.</b> Média global esconde regressão local (CLAUDE.md §6) e
/// a curva é o eixo que o comprador reconhece, mas o recorte que a tela renderiza é o de
/// <c>GET /api/comparacoes/{id}/analise</c>, agregado no servidor a partir das linhas de
/// <c>ComparacaoSessaoItens</c> que esta materialização grava. Ele cobre os <b>dois</b> braços
/// com erro por curva; um recorte gravado aqui traria só a sobra do ERP e seria uma segunda
/// versão do mesmo corte, envelhecendo em paralelo — exatamente o que o cuidado desta camada
/// existe para evitar.
/// </remarks>
internal sealed record SessaoResultado(
    DateTimeOffset GeradoEm,
    Guid ComparacaoPbsId,
    DateTime? SugestaoDataHora,
    byte TipoCalculo,
    int ItensAvaliados,
    decimal VendidoNaJanelaUnidades,
    BracoDaSessao Pbs,
    ConfrontoDaSessao? Confronto,
    string? MotivoMlIndisponivel,
    int ItensComDecisaoMl,
    int ItensComPrevisaoMl,
    UtilidadeComparacao UtilidadeDecisaoMl,
    RupturaObservada Ruptura,
    int ItensComJanelaAlemDoHistorico,
    int ItensSemPrecoCompra,
    int? SkusSemCadastro,
    string RessalvaTreinoServe);

/// <summary>
/// Transforma a população do Stage e o resultado das camadas da comparação nas linhas de
/// <c>ComparacaoSessaoItens</c> e nos agregados da manchete.
///
/// <para>
/// Puro e sem I/O, pelo mesmo motivo de <see cref="SessaoJobs"/> e
/// <see cref="SessaoAvancador"/>: nada aqui dá erro quando está errado. Um zero no lugar de
/// um nulo grava uma linha bem formada que afirma ao comprador "o ML mandaria não comprar
/// nada"; uma sobra somada sobre populações diferentes nos dois braços produz uma manchete
/// plausível e falsa. Os dois passam por qualquer teste de fumaça e só um teste de valor
/// pega.
/// </para>
/// </summary>
internal static class SessaoResultadoMontador
{
    public static Materializacao Montar(
        Guid sessaoId,
        int? skusSemCadastro,
        Guid comparacaoPbsId,
        DateTime? sugestaoDataHora,
        ComparacaoOutput comparacao,
        IReadOnlyList<ItemDoStage> populacao,
        DateTimeOffset agora)
    {
        // ToDictionary e não indexador com "último vence": chave repetida significaria mais
        // de uma sugestão na população, invariante que SessaoJobs recusa antes do treino.
        // Se ela cair, o desfecho tem de ser ruidoso — a alternativa é a linha de uma
        // sugestão sobrescrevendo a da outra sem nada na tela denunciando a mistura.
        var previsaoPorItem = comparacao.Previsao.Detalhe.ToDictionary(p => (p.LojaId, p.Sku));
        var decisaoPorItem = comparacao.Decisao.Detalhe.ToDictionary(d => (d.LojaId, d.Sku));

        var itens = new List<ComparacaoSessaoItem>(populacao.Count);

        var pbs = new Acumulador();
        var pbsComparavel = new Acumulador();
        var mlComparavel = new Acumulador();
        decimal vendidoTotal = 0m;
        int comDecisaoMl = 0, comPrevisaoMl = 0, alemDoHistorico = 0, semPrecoCompra = 0;
        int itensComDiaSemEstoque = 0, diasSemEstoque = 0, diasComSnapshot = 0, diasNaJanela = 0;

        foreach (var linha in populacao)
        {
            var item = linha.Item;
            previsaoPorItem.TryGetValue((item.LojaId, item.Sku), out var camadaA);
            decisaoPorItem.TryGetValue((item.LojaId, item.Sku), out var camadaB);

            var sobraPbs = SobraCalculator.Calcular(
                comprado: item.CompraSugerida,
                estoqueInicial: item.EstoqueSaldo,
                pedidosPendentes: item.PedidosPendentes,
                vendido: linha.VendidoNaJanela,
                precoCompra: item.PrecoCompra);

            // Os dois valores do braço de ML andam num par nulável só porque eles nascem e
            // morrem juntos: separá-los em duas variáveis obrigaria a repetir a mesma
            // condição em cada uso, e foi exatamente essa repetição que divergiu antes.
            var ml = camadaB is null
                ? default((decimal Compra, Sobra Sobra)?)
                : (camadaB.CompraMl, SobraCalculator.Calcular(
                    comprado: camadaB.CompraMl,
                    estoqueInicial: item.EstoqueSaldo,
                    pedidosPendentes: item.PedidosPendentes,
                    vendido: linha.VendidoNaJanela,
                    precoCompra: item.PrecoCompra));

            itens.Add(new ComparacaoSessaoItem
            {
                SessaoId = sessaoId,
                LojaId = item.LojaId,
                Sku = item.Sku,
                NomeProduto = linha.NomeProduto,
                Curva = string.IsNullOrWhiteSpace(item.Curva) ? null : item.Curva,
                CompraSugeridaPbs = item.CompraSugerida,
                CompraSugeridaMl = ml?.Compra,
                VendidoNaJanela = linha.VendidoNaJanela,
                DemandaDiaPbs = item.DemandaDia,
                DemandaDiaMl = camadaA is null ? null : Taxa(camadaA.DemandaDiaMl),
                DemandaDiaReal = camadaA is null ? null : Taxa(camadaA.DemandaDiaReal),
                SobraPbsUnidades = sobraPbs.Unidades,
                SobraMlUnidades = ml?.Sobra.Unidades,
                SobraPbsValor = item.PrecoCompra is null ? null : sobraPbs.Valor,
                SobraMlValor = item.PrecoCompra is null ? null : ml?.Sobra.Valor,
                JanelaAlemDoHistorico = linha.JanelaAlemDoHistorico,
            });

            vendidoTotal += linha.VendidoNaJanela;
            pbs.Somar(item.CompraSugerida, sobraPbs);

            if (camadaA is not null) comPrevisaoMl++;
            if (linha.JanelaAlemDoHistorico) alemDoHistorico++;
            if (item.PrecoCompra is null) semPrecoCompra++;

            if (ml is { } bracoMl)
            {
                comDecisaoMl++;
                pbsComparavel.Somar(item.CompraSugerida, sobraPbs);
                mlComparavel.Somar(bracoMl.Compra, bracoMl.Sobra);
            }

            diasNaJanela += Math.Max(0, (int)item.DiasEstoque);
            diasComSnapshot += linha.DiasComSnapshot;
            diasSemEstoque += linha.DiasSemEstoque;
            if (linha.DiasSemEstoque > 0) itensComDiaSemEstoque++;
        }

        var confronto = comDecisaoMl == 0
            ? null
            : new ConfrontoDaSessao(comDecisaoMl, pbsComparavel.Fechar(), mlComparavel.Fechar());

        return new Materializacao(
            itens,
            new SessaoResultado(
                GeradoEm: agora,
                ComparacaoPbsId: comparacaoPbsId,
                SugestaoDataHora: sugestaoDataHora,
                TipoCalculo: comparacao.TipoCalculo,
                ItensAvaliados: populacao.Count,
                VendidoNaJanelaUnidades: vendidoTotal,
                Pbs: pbs.Fechar(),
                Confronto: confronto,
                MotivoMlIndisponivel: confronto is null
                    ? MotivoMlIndisponivel(comparacao.Decisao)
                    : null,
                ItensComDecisaoMl: comDecisaoMl,
                ItensComPrevisaoMl: comPrevisaoMl,
                UtilidadeDecisaoMl: comparacao.Decisao.Utilidade,
                Ruptura: new RupturaObservada(
                    itensComDiaSemEstoque, diasSemEstoque, diasComSnapshot, diasNaJanela),
                ItensComJanelaAlemDoHistorico: alemDoHistorico,
                ItensSemPrecoCompra: semPrecoCompra,
                SkusSemCadastro: skusSemCadastro,
                RessalvaTreinoServe: comparacao.RessalvaTreinoServe));
    }

    /// <summary>
    /// Por que não há coluna de ML para mostrar, em português de comprador — ele não conhece
    /// horizonte de previsão nem reconciliação, e "sem dados" o faria concluir que a
    /// ferramenta está quebrada.
    ///
    /// <para>
    /// A camada A é citada sempre que ela pode ter sobrado: quando a decisão cai por
    /// horizonte, a comparação de previsão contra previsão continua válida, e é isso que
    /// salva a sessão de parecer inútil.
    /// </para>
    /// </summary>
    private static string MotivoMlIndisponivel(DecisionComparisonResult decisao) => decisao.Utilidade switch
    {
        UtilidadeComparacao.ForaDoHorizonteMl =>
            "A compra desta sugestão cobre mais dias do que o método de ML consegue prever hoje, então não é " +
            "possível dizer quanto ele mandaria comprar sem inventar a demanda dos dias que faltam. A comparação " +
            "de previsão de demanda, dia a dia, continua valendo e está na área técnica.",

        UtilidadeComparacao.ReconciliacaoDivergente =>
            "Não conseguimos reproduzir a conta que o seu ERP usou para chegar às quantidades desta sugestão. " +
            "Sem reproduzi-la, trocar só a previsão mediria o que nós não sabemos sobre a regra dele, não a " +
            "qualidade da previsão — então preferimos não mostrar um número em que você não deveria confiar.",

        UtilidadeComparacao.DescartadoPorRuptura =>
            "Em todos os itens comparáveis o estoque ficou zerado em algum dia do período avaliado. Venda em dia " +
            "de falta não mede demanda — o item não vendeu porque não estava lá —, e por isso esses itens ficam " +
            "fora do confronto em vez de entrar com um número enviesado para baixo.",

        UtilidadeComparacao.PopulacaoVazia =>
            "Nenhum item desta sugestão chegou à etapa que calcula a compra do método de ML, então não há coluna " +
            "de ML para comparar. Os números do seu ERP abaixo continuam sendo o que de fato aconteceu.",

        UtilidadeComparacao.SemItensComparaveis =>
            "Os itens desta sugestão ficaram fora do confronto por motivos diferentes entre si — parte por " +
            "cobertura longa demais para o método de ML prever, parte por falta de estoque no período —, e nenhum " +
            "sobrou para comparar os dois métodos item a item.",

        // Utilizavel com zero item confrontável: a camada comparou pares que não têm linha
        // correspondente na população materializada. Não deveria acontecer, e a mensagem
        // admite isso em vez de inventar uma causa plausível.
        _ =>
            "Não foi possível calcular a compra do método de ML para nenhum item desta sugestão. Os números do " +
            "seu ERP abaixo continuam válidos; procure o suporte se precisar do comparativo desta compra.",
    };

    /// <summary>
    /// Taxa de unidades/dia da camada A em <c>decimal(12,4)</c>, a precisão da coluna.
    /// Arredondar aqui, e não no banco, deixa o valor gravado igual ao valor somado.
    /// </summary>
    private static decimal Taxa(double valor) =>
        Math.Round((decimal)valor, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Somatório de um braço. A sobra em R$ vem do <see cref="SobraCalculator"/>, onde item sem
    /// <c>PrecoCompra</c> contribui <b>zero</b> — de propósito: uma soma nula por causa de um
    /// item sem preço apagaria a manchete inteira, e o preço que se paga por isso é a
    /// subestimação declarada em <see cref="SessaoResultado.ItensSemPrecoCompra"/>. Na
    /// <b>linha</b> a escolha é a oposta, nulo, porque lá não há nada a preservar: ver
    /// <see cref="ComparacaoSessaoItem.SobraPbsValor"/>.
    /// </summary>
    private sealed class Acumulador
    {
        private decimal _compraUnidades;
        private decimal _sobraUnidades;
        private decimal _sobraValor;

        public void Somar(decimal comprado, Sobra sobra)
        {
            _compraUnidades += comprado;
            _sobraUnidades += sobra.Unidades;
            _sobraValor += sobra.Valor;
        }

        public BracoDaSessao Fechar() => new(_compraUnidades, _sobraUnidades, _sobraValor);
    }
}
