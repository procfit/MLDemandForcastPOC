namespace CosmosPro.ML.DemandForCast.Purchasing.Comparison;

/// <summary>
/// O que sobrou: quanto do que foi comprado nao vendeu, na janela em que ja se conhece a
/// venda real. E a manchete da tela de resultado do comprador leigo — a primeira frase, em
/// reais.
///
/// <para>
/// Difere de <c>DecisionComparer</c> por medir o desfecho da compra <b>de fato</b> feita, e
/// nao um contrafactual: nao ha braco ML aqui, nem reconciliacao, nem horizonte de previsao —
/// so a quantidade comprada, a posicao de estoque no momento da decisao e a venda que
/// realmente ocorreu. Por isso esta conta nao depende do horizonte curto do ML que impede o
/// <c>DecisionComparer</c> de rodar hoje.
/// </para>
/// </summary>
public static class SobraCalculator
{
    /// <summary>
    /// <paramref name="unidades"/> = <c>max(0, comprado + estoqueInicial + pedidosPendentes − vendido)</c>.
    ///
    /// <para>
    /// <b>Pedidos pendentes entram na posicao — mesma regra da posicao pontuada em
    /// <c>DecisionComparer</c></b> (<c>EstoqueSaldo + PedidosPendentes + compra</c>, ver a
    /// documentacao de <c>ArmDecisionResult.ExcessoUnidades</c> em <c>DecisionModels.cs</c>).
    /// Mercadoria em transito no momento da decisao chega e atende a janela, independente de
    /// ela ter entrado ou nao na aritmetica que decidiu a compra. Omiti-la aqui faria esta
    /// manchete e o excesso da Camada B relatarem dois numeros diferentes para o mesmo item,
    /// sem que nenhum dos dois estivesse errado por si so — a fonte da discrepancia seria so
    /// esta funcao ter ignorado o que a outra conta.
    /// </para>
    ///
    /// <para>
    /// Vender mais do que havia disponivel nunca produz sobra negativa: e ruptura, medida em
    /// outro lugar (o <c>FaltaUnidades</c> da Camada B), nao o inverso desta manchete.
    /// </para>
    /// </summary>
    /// <param name="precoCompra">
    /// <c>SugestoesCompraItens.PrecoCompra</c>. Nulo (coluna sem preco cadastrado) faz
    /// <see cref="Sobra.Valor"/> sair zero em vez de lancar ou propagar nulo — mesma regra de
    /// <c>DecisionItem.PrecoCompra</c> nos agregados em R$ da Camada B. Preco zero produz o
    /// mesmo zero pela propria multiplicacao, sem tratamento especial.
    /// </param>
    /// <returns>
    /// <see cref="Sobra.Valor"/> arredondado a duas casas (centavos) por
    /// <see cref="MidpointRounding.AwayFromZero"/>: e a manchete que o usuario leigo le em
    /// reais, e um valor com mais casas que centavos e mais precisao do que a moeda admite. O
    /// modo "para cima no empate" foi escolhido sobre o arredondamento bancario (par mais
    /// proximo) porque previsibilidade para quem le a tela pesa mais do que o vies estatistico
    /// de longo prazo que o bancario evitaria.
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
}

/// <summary>Sobra de um item: unidades que nao venderam e o valor delas em R$.</summary>
public sealed record Sobra(decimal Unidades, decimal Valor);
