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
            [SugestoesCompra] = ["SugestaoId", "Descricao", "DataHora", "TipoCalculo", "LeadTimeDias", "DiasCurvaA", "DiasCurvaB", "DiasCurvaC", "DiasCurvaD", "DiasCurvaE", "Efetividade", "ConsideraPedidosPendentes", "IncluiEstoqueZerado"],
            [SugestoesCompraItens] = ["SugestaoId", "LojaId", "Sku", "Curva", "DemandaDia", "DemandaDiaPonderada", "EstoqueSaldo", "EstoqueSeguranca", "EstoqueMaximo", "EstoqueMinimo", "DiasEstoque", "PedidosPendentes", "CompraSugerida", "CompraAutorizada", "PrecoCompra", "FatorEmbalagem", "Falteiro"],
        };

    /// <summary>Ordem de escrita no ZIP — dimensões antes dos fatos, para o log fazer sentido.</summary>
    public static readonly string[] WriteOrder =
        [Lojas, Produtos, Vendas, EstoquesDiarios, Compras, Promocoes,
         SugestoesCompra, SugestoesCompraItens];
}
