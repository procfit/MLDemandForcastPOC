namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Nomes e ordem das colunas de cada CSV do ZIP de import. Espelha
/// <c>Worker.TableSchemas</c> e <c>ApiService.Imports.ImportSchemas</c> — a
/// duplicação é deliberada (o extrator roda isolado na máquina do cliente e não
/// referencia o resto da solução), e o teste de contrato garante que não divirjam.
/// </summary>
internal static class StageContract
{
    public const string Lojas = "lojas.csv";
    public const string Produtos = "produtos.csv";
    public const string Vendas = "vendas.csv";
    public const string EstoquesDiarios = "estoques_diarios.csv";
    public const string Compras = "compras.csv";
    public const string Promocoes = "promocoes.csv";
    /// <summary>
    /// Único CSV do ZIP <b>sem tabela correspondente no Stage</b>: ele vai para
    /// <c>engine.RedeCatalogoEans</c>, porque a tela de oportunidades não pertence a sessão
    /// nenhuma e o Stage é apagado a cada import. Por isso ele não aparece no mapeamento
    /// CSV→tabela de <c>StageContractTests</c> — há um teste afirmando essa ausência, para
    /// ela não ser "corrigida" com uma tabela de Stage que ninguém leria.
    /// </summary>
    public const string CatalogoEans = "catalogo_eans.csv";

    public const string SugestoesCompra = "sugestoes_compra.csv";
    public const string SugestoesCompraItens = "sugestoes_compra_itens.csv";

    public static readonly IReadOnlyDictionary<string, string[]> Headers =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Lojas] = ["LojaId", "Nome", "UF", "Cidade", "Regiao", "Perfil", "DiasOperacaoSemana", "DataAbertura", "Ativo", "Cnpj"],
            [Produtos] = ["Sku", "Nome", "Categoria", "Subcategoria", "Fabricante", "PrincipioAtivo", "Apresentacao", "Ean", "RegistroAnvisa", "ListaControle", "ClasseTerapeutica", "Ativo"],
            [Vendas] = ["Data", "LojaId", "Sku", "Quantidade", "PrecoUnitario", "ValorTotal"],
            [EstoquesDiarios] = ["Data", "LojaId", "Sku", "QuantidadeEmEstoque"],
            [Compras] = ["DataPedido", "DataRecebimento", "LojaId", "Sku", "Quantidade", "Fornecedor"],
            [Promocoes] = ["DataInicio", "DataFim", "Sku", "LojaId", "Tipo", "DescontoPct"],
            [CatalogoEans] = ["Sku", "Ean", "Nome"],
            [SugestoesCompra] = ["SugestaoId", "Descricao", "DataHora", "TipoCalculo", "LeadTimeDias", "DiasCurvaA", "DiasCurvaB", "DiasCurvaC", "DiasCurvaD", "DiasCurvaE", "Efetividade", "ConsideraPedidosPendentes", "IncluiEstoqueZerado"],
            [SugestoesCompraItens] = ["SugestaoId", "LojaId", "Sku", "Curva", "DemandaDia", "DemandaDiaPonderada", "EstoqueSaldo", "EstoqueSeguranca", "EstoqueMaximo", "EstoqueMinimo", "DiasEstoque", "PedidosPendentes", "CompraSugerida", "CompraAutorizada", "PrecoCompra", "FatorEmbalagem", "Falteiro"],
        };

    /// <summary>Ordem de escrita no ZIP — dimensões antes dos fatos, para o log fazer sentido.</summary>
    public static readonly string[] WriteOrder =
        [Lojas, Produtos, Vendas, EstoquesDiarios, Compras, Promocoes, CatalogoEans,
         SugestoesCompra, SugestoesCompraItens];
}
