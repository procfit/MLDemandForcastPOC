using System.Linq.Expressions;
using CosmosPro.ML.DemandForCast.Engine.Entities;

namespace CosmosPro.ML.DemandForCast.ApiService.Comparacoes;

/// <summary>
/// Whitelist das colunas pelas quais o detalhe de uma sessão pode ser ordenado, na mesma
/// linha de defesa do <see cref="Stage.StageBrowser"/>: nome de coluna vindo do request
/// nunca chega ao banco: ou bate com um item desta lista, ou a ordenação cai no padrão.
///
/// <para>
/// Aqui a ordenação é expressa em LINQ e não em texto interpolado, então o risco imediato
/// não é injeção de SQL — é o oposto: o EF <b>estouraria</b> num nome desconhecido, e uma
/// tabela de 30 mil linhas que devolve erro quando o comprador clica no cabeçalho é o
/// mesmo defeito visto do outro lado. A whitelist existe para que a resposta declare a
/// coluna efetivamente aplicada (<c>OrderBy</c> da página) em vez de ordenar por uma coisa
/// enquanto o cabeçalho aponta outra.
/// </para>
/// </summary>
internal static class OrdemItensSessao
{
    /// <summary>
    /// Sobra em unidades: é a coluna pela qual o comprador procura o pior item, e a
    /// pergunta que traz ele à tela ("o que encalhou?") já vem ordenada respondida.
    /// </summary>
    public const string Padrao = nameof(ComparacaoSessaoItem.SobraPbsUnidades);

    public static readonly IReadOnlyList<string> Colunas =
    [
        nameof(ComparacaoSessaoItem.LojaId),
        nameof(ComparacaoSessaoItem.Sku),
        nameof(ComparacaoSessaoItem.NomeProduto),
        nameof(ComparacaoSessaoItem.Curva),
        nameof(ComparacaoSessaoItem.CompraSugeridaPbs),
        nameof(ComparacaoSessaoItem.CompraSugeridaMl),
        nameof(ComparacaoSessaoItem.VendidoNaJanela),
        nameof(ComparacaoSessaoItem.DemandaDiaPbs),
        nameof(ComparacaoSessaoItem.DemandaDiaMl),
        nameof(ComparacaoSessaoItem.DemandaDiaReal),
        nameof(ComparacaoSessaoItem.SobraPbsUnidades),
        nameof(ComparacaoSessaoItem.SobraMlUnidades),
        nameof(ComparacaoSessaoItem.SobraPbsValor),
        nameof(ComparacaoSessaoItem.SobraMlValor),
        nameof(ComparacaoSessaoItem.JanelaAlemDoHistorico),
    ];

    /// <summary>Se a coluna pedida está na whitelist. Ausente e desconhecida são o mesmo: não.</summary>
    public static bool Aceita(string? pedida) =>
        pedida is not null && Colunas.Any(c => c.Equals(pedida, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Nome canônico da coluna a aplicar. Coluna fora da whitelist cai em
    /// <see cref="Padrao"/>, e quem chama devolve este nome na resposta — sem isso a tela
    /// mostraria a seta de ordenação num cabeçalho que não ordenou nada.
    /// </summary>
    public static string Resolver(string? pedida) =>
        pedida is not null && Colunas.FirstOrDefault(c => c.Equals(pedida, StringComparison.OrdinalIgnoreCase)) is { } achada
            ? achada
            : Padrao;

    /// <summary>
    /// Aplica a ordenação com desempate por <c>(LojaId, Sku)</c>. O desempate não é enfeite:
    /// sem ele, duas linhas com a mesma sobra podem trocar de lugar entre duas páginas e o
    /// comprador vê o mesmo item duas vezes — ou nenhuma.
    /// </summary>
    public static IOrderedQueryable<ComparacaoSessaoItem> Aplicar(
        IQueryable<ComparacaoSessaoItem> itens, string coluna, bool descendente)
    {
        var ordenado = coluna switch
        {
            nameof(ComparacaoSessaoItem.LojaId) => Por(itens, i => i.LojaId, descendente),
            nameof(ComparacaoSessaoItem.Sku) => Por(itens, i => i.Sku, descendente),
            nameof(ComparacaoSessaoItem.NomeProduto) => Por(itens, i => i.NomeProduto, descendente),
            nameof(ComparacaoSessaoItem.Curva) => Por(itens, i => i.Curva, descendente),
            nameof(ComparacaoSessaoItem.CompraSugeridaPbs) => Por(itens, i => i.CompraSugeridaPbs, descendente),
            nameof(ComparacaoSessaoItem.CompraSugeridaMl) => Por(itens, i => i.CompraSugeridaMl, descendente),
            nameof(ComparacaoSessaoItem.VendidoNaJanela) => Por(itens, i => i.VendidoNaJanela, descendente),
            nameof(ComparacaoSessaoItem.DemandaDiaPbs) => Por(itens, i => i.DemandaDiaPbs, descendente),
            nameof(ComparacaoSessaoItem.DemandaDiaMl) => Por(itens, i => i.DemandaDiaMl, descendente),
            nameof(ComparacaoSessaoItem.DemandaDiaReal) => Por(itens, i => i.DemandaDiaReal, descendente),
            nameof(ComparacaoSessaoItem.SobraMlUnidades) => Por(itens, i => i.SobraMlUnidades, descendente),
            nameof(ComparacaoSessaoItem.SobraPbsValor) => Por(itens, i => i.SobraPbsValor, descendente),
            nameof(ComparacaoSessaoItem.SobraMlValor) => Por(itens, i => i.SobraMlValor, descendente),
            nameof(ComparacaoSessaoItem.JanelaAlemDoHistorico) => Por(itens, i => i.JanelaAlemDoHistorico, descendente),
            _ => Por(itens, i => i.SobraPbsUnidades, descendente),
        };

        return ordenado.ThenBy(i => i.LojaId).ThenBy(i => i.Sku);
    }

    private static IOrderedQueryable<ComparacaoSessaoItem> Por<TKey>(
        IQueryable<ComparacaoSessaoItem> itens,
        Expression<Func<ComparacaoSessaoItem, TKey>> chave,
        bool descendente) =>
        descendente ? itens.OrderByDescending(chave) : itens.OrderBy(chave);
}
