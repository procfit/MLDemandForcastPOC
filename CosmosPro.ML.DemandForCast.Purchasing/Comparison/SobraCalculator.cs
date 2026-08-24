namespace CosmosPro.ML.DemandForCast.Purchasing.Comparison;

/// <summary>
/// O que sobrou: quanto do que foi comprado não vendeu, na janela em que já se conhece a
/// venda real. É a manchete da tela de resultado do comprador leigo — a primeira frase, em
/// reais.
///
/// <para>
/// Difere de <c>DecisionComparer</c> por medir o desfecho da compra <b>de fato</b> feita, e
/// não um contrafactual: não há braço ML aqui, nem reconciliação, nem horizonte de previsão —
/// só a quantidade comprada, a posição de estoque no momento da decisão e a venda que
/// realmente ocorreu. Por isso esta conta não depende do horizonte curto do ML que impede o
/// <c>DecisionComparer</c> de rodar hoje.
/// </para>
/// </summary>
public static class SobraCalculator
{
    /// <summary>
    /// Unidades = <c>max(0, comprado + estoqueInicial + pedidosPendentes − vendido)</c>.
    ///
    /// <para>
    /// <b>Pedidos pendentes entram na posição — mesma regra da posição pontuada em
    /// <c>DecisionComparer</c></b> (<c>EstoqueSaldo + PedidosPendentes + compra</c>, ver a
    /// documentação de <c>ArmDecisionResult.ExcessoUnidades</c> em <c>DecisionModels.cs</c>).
    /// Mercadoria em trânsito no momento da decisão chega e atende à janela, independente de
    /// ela ter entrado ou não na aritmética que decidiu a compra. Omiti-la aqui faria esta
    /// manchete e o excesso da Camada B relatarem dois números diferentes para o mesmo item,
    /// sem que nenhum dos dois estivesse errado por si só — a fonte da discrepância seria só
    /// esta função ter ignorado o que a outra conta.
    /// </para>
    ///
    /// <para>
    /// Vender mais do que havia disponível nunca produz sobra negativa: é ruptura, medida em
    /// outro lugar (o <c>FaltaUnidades</c> da Camada B), não o inverso desta manchete.
    /// </para>
    /// </summary>
    /// <param name="precoCompra">
    /// <c>SugestoesCompraItens.PrecoCompra</c>. Nulo (coluna sem preço cadastrado) faz
    /// <see cref="Sobra.Valor"/> sair zero em vez de lançar ou propagar nulo — mesma regra de
    /// <c>DecisionItem.PrecoCompra</c> nos agregados em R$ da Camada B. Preço zero produz o
    /// mesmo zero pela própria multiplicação, sem tratamento especial.
    /// </param>
    /// <returns>
    /// <see cref="Sobra.Valor"/> arredondado a duas casas (centavos) por
    /// <see cref="MidpointRounding.AwayFromZero"/>: é a manchete que o usuário leigo lê em
    /// reais, e um valor com mais casas que centavos afirma mais precisão do que a moeda
    /// admite. O modo "para cima no empate" foi escolhido sobre o arredondamento bancário (par
    /// mais próximo) porque previsibilidade para quem lê a tela pesa mais do que o viés
    /// estatístico de longo prazo que o bancário evitaria.
    /// </returns>
    public static Sobra Calcular(
        decimal comprado, decimal estoqueInicial, decimal pedidosPendentes, decimal vendido, decimal? precoCompra)
    {
        var posicao = comprado + estoqueInicial + pedidosPendentes;
        var unidades = Math.Max(0m, posicao - vendido);
        var valor = precoCompra is null
            ? 0m
            : Math.Round(unidades * precoCompra.Value, 2, MidpointRounding.AwayFromZero);

        return new Sobra(unidades, valor);
    }

    /// <summary>
    /// A parcela da sobra que a COMPRA causou, separada do estoque que já estava lá.
    ///
    /// <para>
    /// <c>max(0, comprado − max(0, vendido − estoqueInicial − pedidosPendentes))</c>: das
    /// unidades compradas, quantas não chegaram a ser necessárias porque o que já havia na
    /// prateleira (mais o que estava a caminho) deu conta da venda. A venda consome primeiro
    /// a posição que existia — só o excedente dela é atribuível à compra.
    /// </para>
    ///
    /// <para>
    /// <b>Por que as duas medidas convivem.</b> <see cref="Calcular"/> responde "o que sobrou
    /// na prateleira", que é o estado do capital parado e é o que o comprador vê ao abrir a
    /// loja. Esta responde "o que ESTA compra deixou parado", que é a única das duas
    /// atribuível à decisão sob teste. Numa sugestão em que o ERP comprou 207 unidades e a
    /// prateleira terminou com 4.194 de excesso, a primeira mede sobretudo inventário
    /// herdado: usá-la para julgar a decisão de compra é cobrar da compra o que ela não fez.
    /// Nenhuma das duas substitui a outra, e é por isso que a tela mostra ambas.
    /// </para>
    /// </summary>
    public static Sobra CalcularDaCompra(
        decimal comprado, decimal estoqueInicial, decimal pedidosPendentes, decimal vendido, decimal? precoCompra)
    {
        var posicaoPrevia = estoqueInicial + pedidosPendentes;
        var consumoDaCompra = Math.Max(0m, vendido - posicaoPrevia);
        var unidades = Math.Max(0m, comprado - consumoDaCompra);
        var valor = precoCompra is null
            ? 0m
            : Math.Round(unidades * precoCompra.Value, 2, MidpointRounding.AwayFromZero);

        return new Sobra(unidades, valor);
    }
}

/// <summary>Sobra de um item: unidades que não venderam e o valor delas em R$.</summary>
public sealed record Sobra(decimal Unidades, decimal Valor);
