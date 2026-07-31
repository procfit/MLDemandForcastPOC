using CosmosPro.ML.DemandForCast.ApiService.Comparacoes;
using CosmosPro.ML.DemandForCast.Engine.Entities;

namespace CosmosPro.ML.DemandForCast.ApiService.Tests;

/// <summary>
/// A whitelist de ordenação do detalhe da sessão. Mesmo papel da whitelist do
/// <c>StageBrowser</c>: nome de coluna vindo do request não chega ao banco sem passar por
/// aqui.
/// </summary>
public sealed class OrdemItensSessaoTests
{
    [Theory]
    [InlineData("SobraPbsValor; DROP TABLE dbo.ComparacaoSessaoItens")]
    [InlineData("1; SELECT * FROM AspNetUsers")]
    [InlineData("RedeId")]
    [InlineData("SessaoId")]
    [InlineData("")]
    [InlineData(null)]
    public void Coluna_fora_da_whitelist_e_recusada_e_cai_no_padrao(string? pedida)
    {
        OrdemItensSessao.Aceita(pedida).Should().BeFalse();
        OrdemItensSessao.Resolver(pedida).Should().Be(OrdemItensSessao.Padrao,
            "coluna desconhecida ordena pelo padrão declarado, e nunca pelo texto recebido");
    }

    /// <summary>
    /// <c>RedeId</c> e <c>SessaoId</c> ficam fora por motivos diferentes de "não existe":
    /// o primeiro não existe nesta tabela (o escopo é transitivo pelo pai) e o segundo é
    /// constante dentro de uma sessão. Ordenar por eles nunca faz sentido para o comprador.
    /// </summary>
    [Fact]
    public void Whitelist_nao_expoe_escopo_nem_chave_da_sessao()
    {
        OrdemItensSessao.Colunas.Should().NotContain("RedeId");
        OrdemItensSessao.Colunas.Should().NotContain(nameof(ComparacaoSessaoItem.SessaoId));
    }

    [Theory]
    [InlineData("sobrapbsunidades", "SobraPbsUnidades")]
    [InlineData("SKU", "Sku")]
    [InlineData("NomeProduto", "NomeProduto")]
    public void Coluna_da_whitelist_e_resolvida_para_o_nome_canonico(string pedida, string esperada)
    {
        OrdemItensSessao.Aceita(pedida).Should().BeTrue();
        OrdemItensSessao.Resolver(pedida).Should().Be(esperada,
            "o nome que volta na resposta é o canônico, não o que o cliente digitou");
    }

    /// <summary>
    /// Cada coluna da whitelist tem de ter um caso no <c>switch</c> que a aplica. Sem este
    /// teste, uma coluna acrescentada à lista e esquecida no <c>switch</c> ordenaria pelo
    /// padrão em silêncio — a tela desenharia a seta no cabeçalho clicado e mostraria outra
    /// ordem.
    /// </summary>
    [Fact]
    public void Toda_coluna_da_whitelist_ordena_de_fato_por_ela()
    {
        var itens = Amostra().AsQueryable();

        foreach (var coluna in OrdemItensSessao.Colunas)
        {
            var propriedade = typeof(ComparacaoSessaoItem).GetProperty(coluna)!;

            var crescente = OrdemItensSessao.Aplicar(itens, coluna, descendente: false)
                .ToList()
                .Select(i => propriedade.GetValue(i))
                .ToList();

            crescente.Should().BeInAscendingOrder(ComparadorDeValores,
                $"ordenar por '{coluna}' precisa mesmo ordenar por '{coluna}'");

            var decrescente = OrdemItensSessao.Aplicar(itens, coluna, descendente: true)
                .ToList()
                .Select(i => propriedade.GetValue(i))
                .ToList();

            decrescente.Should().BeInDescendingOrder(ComparadorDeValores,
                $"ordenar por '{coluna}' descendente precisa inverter a ordem");
        }
    }

    /// <summary>
    /// Desempate por <c>(LojaId, Sku)</c>: sem ele, duas linhas com a mesma sobra podem
    /// trocar de lugar entre duas páginas e o comprador vê o mesmo item duas vezes — ou
    /// nenhuma.
    /// </summary>
    [Fact]
    public void Linhas_empatadas_na_coluna_pedida_saem_desempatadas_por_loja_e_sku()
    {
        var empatados = new List<ComparacaoSessaoItem>
        {
            Item(loja: 2, sku: "B", sobra: 10m),
            Item(loja: 1, sku: "C", sobra: 10m),
            Item(loja: 1, sku: "A", sobra: 10m),
        }.AsQueryable();

        var ordenado = OrdemItensSessao
            .Aplicar(empatados, nameof(ComparacaoSessaoItem.SobraPbsUnidades), descendente: true)
            .ToList();

        ordenado.Select(i => (i.LojaId, i.Sku)).Should().Equal([(1, "A"), (1, "C"), (2, "B")]);
    }

    /// <summary>
    /// Compara os valores lidos por reflexão. Explícito em vez de
    /// <c>Comparer&lt;object?&gt;.Default</c> só para não depender da conversão de
    /// anulabilidade na assinatura do FluentAssertions.
    /// </summary>
    private static readonly IComparer<object?> ComparadorDeValores =
        Comparer<object?>.Create((a, b) => Comparer<object>.Default.Compare(a!, b!));

    private static List<ComparacaoSessaoItem> Amostra() =>
    [
        Item(loja: 1, sku: "AAA", sobra: 5m, curva: "A", nome: "Alfa",
            compraMl: 1m, sobraMl: 1m, sobraValor: 1m, sobraMlValor: 1m,
            demandaMl: 1m, demandaReal: 1m, demandaPbs: 1m,
            comprado: 1m, vendido: 1m, alemDoHistorico: false),
        Item(loja: 2, sku: "BBB", sobra: 15m, curva: "B", nome: "Beta",
            compraMl: 2m, sobraMl: 2m, sobraValor: 2m, sobraMlValor: 2m,
            demandaMl: 2m, demandaReal: 2m, demandaPbs: 2m,
            comprado: 2m, vendido: 2m, alemDoHistorico: true),
        Item(loja: 3, sku: "CCC", sobra: 25m, curva: "C", nome: "Gama",
            compraMl: 3m, sobraMl: 3m, sobraValor: 3m, sobraMlValor: 3m,
            demandaMl: 3m, demandaReal: 3m, demandaPbs: 3m,
            comprado: 3m, vendido: 3m, alemDoHistorico: true),
    ];

    private static ComparacaoSessaoItem Item(
        int loja,
        string sku,
        decimal sobra,
        string? curva = null,
        string? nome = null,
        decimal? compraMl = null,
        decimal? sobraMl = null,
        decimal? sobraValor = null,
        decimal? sobraMlValor = null,
        decimal? demandaMl = null,
        decimal? demandaReal = null,
        decimal demandaPbs = 0m,
        decimal comprado = 0m,
        decimal vendido = 0m,
        bool alemDoHistorico = false) => new()
        {
            SessaoId = Guid.Empty,
            LojaId = loja,
            Sku = sku,
            NomeProduto = nome,
            Curva = curva,
            CompraSugeridaPbs = comprado,
            CompraSugeridaMl = compraMl,
            VendidoNaJanela = vendido,
            DemandaDiaPbs = demandaPbs,
            DemandaDiaMl = demandaMl,
            DemandaDiaReal = demandaReal,
            SobraPbsUnidades = sobra,
            SobraMlUnidades = sobraMl,
            SobraPbsValor = sobraValor,
            SobraMlValor = sobraMlValor,
            JanelaAlemDoHistorico = alemDoHistorico,
        };
}
