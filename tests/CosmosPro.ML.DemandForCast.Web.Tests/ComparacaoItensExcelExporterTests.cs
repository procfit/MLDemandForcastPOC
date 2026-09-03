using System.Globalization;
using ClosedXML.Excel;
using CosmosPro.ML.DemandForCast.Web;

namespace CosmosPro.ML.DemandForCast.Web.Tests;

/// <summary>
/// A planilha dos itens comparados.
///
/// <para>
/// <b>Duas coisas não podem falhar aqui, e as duas são sobre leitura errada, não sobre
/// formatação.</b> Primeira: célula do braço de ML sem cálculo tem de trazer texto, nunca zero
/// — a planilha sai do sistema e vai virar tabela dinâmica na mão do comprador, onde um zero
/// soma como "o ML mandaria não comprar nada". Segunda: a capa tem de declarar o filtro
/// aplicado, senão dois arquivos de recortes diferentes ficam indistinguíveis no disco e alguém
/// compara a loja 18 com a rede inteira sem saber.
/// </para>
/// </summary>
public sealed class ComparacaoItensExcelExporterTests
{
    private static readonly Guid Sessao = Guid.Parse("01a04170-f0c3-79d1-9ec4-87906511cf75");
    private static readonly DateTimeOffset GeradoEm = new(2026, 8, 27, 4, 22, 0, TimeSpan.Zero);

    [Fact]
    public void Celula_sem_calculo_do_ml_traz_texto_e_nunca_zero()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));
        var ws = wb.Worksheet("Itens");

        // Linha 2 = item COM cálculo; linha 3 = item SEM cálculo.
        ws.Cell(3, Col(ws, "Compraria (ML un.)")).GetString().Should().Be("sem cálculo do ML",
            "zero aqui somaria na planilha do comprador como se o ML tivesse mandado não comprar nada");
        ws.Cell(3, Col(ws, "Compraria (ML un.)")).Value.IsNumber.Should().BeFalse("o Excel precisa se RECUSAR a somar esta célula");
        ws.Cell(3, Col(ws, "Sobraria (ML un.)")).GetString().Should().Be("sem cálculo do ML");
        ws.Cell(3, Col(ws, "R$ parado (ML)")).GetString().Should().Be("sem cálculo do ML");

        // E onde há cálculo, número de verdade — o texto não pode ter contaminado a coluna.
        ws.Cell(2, Col(ws, "Compraria (ML un.)")).Value.IsNumber.Should().BeTrue();
        ws.Cell(2, Col(ws, "Compraria (ML un.)")).GetDouble().Should().Be(3d);
    }

    /// <summary>
    /// Sem cálculo de ML não existe vencedor <b>nem empate</b>: dizer "empate" onde falta a
    /// conta afirmaria que os dois métodos chegaram ao mesmo resultado.
    /// </summary>
    [Fact]
    public void Sem_calculo_do_ml_nao_ha_vencedor_nem_empate()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));
        var ws = wb.Worksheet("Itens");

        ws.Cell(3, Col(ws, "Quem ficou mais perto")).GetString().Should().Be("só o PBS foi calculado");
        ws.Cell(2, Col(ws, "Quem ficou mais perto")).GetString().Should().Be("ML", "sobra menor do ML nesta linha");
    }

    [Fact]
    public void Capa_declara_o_filtro_aplicado_e_o_denominador()
    {
        var filtro = new FiltroDeItens(LojaId: 18, Curva: "A");
        using var wb = Abrir(Build(filtro, Itens(), totalSemFiltro: 20_153));
        var ws = wb.Worksheet("Recorte");

        var texto = TextoDaAba(ws);
        texto.Should().Contain("18", "sem a loja na capa, dois recortes ficam indistinguíveis no disco");
        // Separador de milhar vem da cultura do processo (pt-BR em produção, invariante no
        // runner do CI). O esperado é construído com a mesma formatação: o que se afirma é o
        // denominador na capa, não a convenção decimal do host.
        texto.Should().Contain($"2 de {20_153.ToString("N0", CultureInfo.CurrentCulture)} da sugestão",
            "o denominador é o que impede comparar um recorte com a população inteira sem perceber");
        texto.Should().Contain("125595");
    }

    /// <summary>
    /// Filtro nenhum precisa dizer "todas" em vez de deixar a linha vazia: célula em branco na
    /// capa lê-se como "não sei qual filtro estava aplicado".
    /// </summary>
    [Fact]
    public void Sem_filtro_a_capa_diz_todas()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));

        TextoDaAba(wb.Worksheet("Recorte")).Should().Contain("todas");
    }

    /// <summary>
    /// O recorte "sem categoria" tem de aparecer como recorte, não como ausência de filtro — é
    /// o sentinela viajando até a capa.
    /// </summary>
    [Fact]
    public void Recorte_de_ausencia_aparece_declarado_na_capa()
    {
        using var wb = Abrir(Build(new FiltroDeItens(Categoria: FiltroDeItens.Ausente), Itens()));

        TextoDaAba(wb.Worksheet("Recorte")).Should().Contain("apenas itens sem esse dado");
    }

    [Fact]
    public void Aba_de_itens_tem_uma_linha_por_item_mais_o_cabecalho()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));
        var ws = wb.Worksheet("Itens");

        ws.LastRowUsed()!.RowNumber().Should().Be(3, "dois itens mais o cabeçalho");
        Col(ws, "Categoria").Should().BePositive("a aba tem de trazer a coluna de categoria");
    }

    /// <summary>Item sem cadastro no PBS: a planilha explica, em vez de deixar a célula vazia.</summary>
    [Fact]
    public void Item_sem_cadastro_e_sem_categoria_sao_explicados()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));
        var ws = wb.Worksheet("Itens");

        ws.Cell(3, Col(ws, "Produto")).GetString().Should().Contain("não encontrado no cadastro do PBS");
        ws.Cell(3, Col(ws, "Categoria")).GetString().Should().Be("(sem categoria)");
    }

    [Fact]
    public void As_colunas_de_mercado_saem_e_a_ausencia_fica_em_branco()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));
        var ws = wb.Worksheet("Itens");

        // As sete colunas de mercado existem, onde quer que tenham caído.
        Col(ws, "Mês IQVIA").Should().BePositive();
        Col(ws, "Índice vs bairro").Should().BePositive();
        Col(ws, "Alerta de mercado").Should().BePositive();

        // Linha 2: item COM medição.
        ws.Cell(2, Col(ws, "Mês IQVIA")).GetString().Should().Be("06/2025");
        ws.Cell(2, Col(ws, "Bairro (brick)")).GetString().Should().Be("528-RJ VOLTA REDONDA RETIRO");
        ws.Cell(2, Col(ws, "Vendemos no bairro (un.)")).GetDouble().Should().Be(12d);
        ws.Cell(2, Col(ws, "Concorrentes no bairro (un.)")).GetDouble().Should().Be(988d);
        ws.Cell(2, Col(ws, "Índice vs bairro")).GetDouble().Should().BeApproximately(0.1234d, 0.00001d);
        ws.Cell(2, Col(ws, "Dias sem estoque")).GetDouble().Should().Be(3d);
        ws.Cell(2, Col(ws, "Alerta de mercado")).GetString().Should().Be("Possível perda por ruptura");

        // Linha 3: item SEM medição. Célula em BRANCO, não zero -- o comprador ordena a
        // planilha por estas colunas, e zero no índice colocaria o item sem medição junto
        // dos piores, enquanto zero em unidades afirmaria que o bairro não vende o item.
        ws.Cell(3, Col(ws, "Vendemos no bairro (un.)")).IsEmpty().Should().BeTrue();
        ws.Cell(3, Col(ws, "Índice vs bairro")).IsEmpty().Should().BeTrue();
        ws.Cell(3, Col(ws, "Dias sem estoque")).IsEmpty().Should().BeTrue();
        ws.Cell(3, Col(ws, "Índice vs bairro")).Value.IsNumber.Should().BeFalse("o Excel não pode somar o que ninguém mediu");

        // E a coluna de alerta diz qual dos dois casos é, em vez de ficar vazia junto.
        ws.Cell(3, Col(ws, "Alerta de mercado")).GetString().Should().Be("sem dado de mercado");
    }

    /// <summary>
    /// Resolve a coluna pelo <b>cabeçalho</b>, e não por posição fixa. Estes casos já
    /// quebraram inteiros quando colunas novas entraram no meio da planilha (EAN, Fabricante e
    /// os dois estoques): o que eles afirmam é o conteúdo de uma coluna nomeada, não em que
    /// posição ela caiu, e prender a posição transformava cada coluna nova em cinco falhas
    /// que não apontavam defeito nenhum.
    /// </summary>
    private static int Col(IXLWorksheet ws, string cabecalho)
    {
        var ultima = ws.LastColumnUsed()!.ColumnNumber();
        for (var c = 1; c <= ultima; c++)
        {
            if (ws.Cell(1, c).GetString() == cabecalho) return c;
        }

        throw new InvalidOperationException(
            $"A planilha não tem a coluna \"{cabecalho}\". Cabeçalhos: " +
            string.Join(" | ", Enumerable.Range(1, ultima).Select(c => ws.Cell(1, c).GetString())));
    }

    /// <summary>
    /// Cadastro e estoque saem na planilha, e a ausencia deles nao vira zero.
    ///
    /// <para>
    /// <b>Zero no estoque do fim e uma medicao</b> — o item terminou o periodo zerado —,
    /// enquanto celula vazia e "nao ha registro daquele dia". O comprador ordena a planilha
    /// por essa coluna procurando o que encalhou; trocar ausencia por zero jogaria os itens
    /// sem medida para o topo da lista dos que zeraram.
    /// </para>
    ///
    /// <para>
    /// O EAN sai como <b>texto</b>: o codigo do PBS tem 14 posicoes com zero a esquerda, e o
    /// Excel comeria esse zero se a celula fosse numerica.
    /// </para>
    /// </summary>
    [Fact]
    public void Cadastro_e_estoque_saem_na_planilha_e_a_ausencia_nao_vira_zero()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));
        var ws = wb.Worksheet("Itens");

        ws.Cell(2, Col(ws, "EAN")).GetString().Should().Be("07896004711027");
        ws.Cell(2, Col(ws, "EAN")).Value.IsNumber.Should().BeFalse("o zero a esquerda tem de sobreviver");
        ws.Cell(2, Col(ws, "Fabricante")).GetString().Should().Be("EMS");
        ws.Cell(2, Col(ws, "Estoque na sugestão (un.)")).GetDouble().Should().Be(10d);

        ws.Cell(2, Col(ws, "Estoque no fim (un.)")).GetDouble().Should().Be(0d,
            "zero aqui e medicao: o item terminou o periodo zerado");

        // Segundo item nao tem nada disso, e a diferenca precisa aparecer.
        ws.Cell(3, Col(ws, "Estoque na sugestão (un.)")).IsEmpty().Should().BeTrue();
        ws.Cell(3, Col(ws, "Estoque no fim (un.)")).IsEmpty().Should().BeTrue();
        ws.Cell(3, Col(ws, "Estoque no fim (un.)")).Value.IsNumber.Should().BeFalse(
            "o Excel nao pode somar o que ninguem mediu");
    }

    private static byte[] Build(FiltroDeItens filtro, IReadOnlyList<SessaoItem> itens, int totalSemFiltro = 2) =>
        ComparacaoItensExcelExporter.Build(
            Sessao,
            nomeDaSessao: "Comparação de teste",
            sugestaoId: 125_595,
            filtro,
            disponiveis: new FiltrosDisponiveis([18], ["MPX ETICO"], true, ["A"], false),
            totais: new TotaisDosItens(
                Itens: itens.Count,
                CompraPbsUnidades: 5m,
                CompraMlUnidades: 3m,
                ItensComCompraMl: 1,
                VendidoNaJanela: 4m,
                SobraPbsUnidades: 10m,
                SobraPbsComparavelUnidades: 9m,
                SobraMlUnidades: 8m,
                ItensComSobraMl: 1,
                SobraPbsValor: 100m,
                ItensComValorPbs: 2,
                SobraPbsComparavelValor: 90m,
                SobraMlValor: 80m,
                ItensComValorMl: 1),
            totalSemFiltro,
            itens,
            GeradoEm);

    /// <summary>
    /// Um item com braço de ML e um sem — é a única combinação que prova as duas regras ao
    /// mesmo tempo: texto onde falta a conta, número onde ela existe.
    /// </summary>
    private static IReadOnlyList<SessaoItem> Itens() =>
    [
        new(LojaId: 18, Sku: "154643", NomeProduto: "KOIDE D 120ML", Curva: "A",
            CompraSugeridaPbs: 5m, CompraSugeridaMl: 3m, VendidoNaJanela: 4m,
            SobraPbsUnidades: 10m, SobraMlUnidades: 8m, SobraPbsValor: 100m,
            JanelaAlemDoHistorico: false, Categoria: "MPX ETICO", SobraMlValor: 80m,
            MercadoMes: new DateOnly(2025, 6, 1), MercadoBrick: "528-RJ VOLTA REDONDA RETIRO",
            MercadoUnidadesRede: 12m, MercadoUnidadesConcorrentes: 988m,
            MercadoIndiceDesempenho: 0.1234m, MercadoDiasSemEstoque: 3,
            MercadoAlerta: "Ruptura",
            Fabricante: "EMS", Ean: "07896004711027",
            EstoqueNaSugestao: 10m, EstoqueNoFimDoPeriodo: 0m),
        new(LojaId: 18, Sku: "999999", NomeProduto: null, Curva: null,
            CompraSugeridaPbs: 0m, CompraSugeridaMl: null, VendidoNaJanela: 0m,
            SobraPbsUnidades: 0m, SobraMlUnidades: null, SobraPbsValor: null,
            JanelaAlemDoHistorico: false, Categoria: null, SobraMlValor: null),
    ];

    private static XLWorkbook Abrir(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextoDaAba(IXLWorksheet ws) =>
        string.Join(" | ", ws.CellsUsed().Select(c => c.GetFormattedString()));
}
