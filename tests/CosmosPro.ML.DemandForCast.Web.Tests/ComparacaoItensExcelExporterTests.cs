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
        ws.Cell(3, 7).GetString().Should().Be("sem cálculo do ML",
            "zero aqui somaria na planilha do comprador como se o ML tivesse mandado não comprar nada");
        ws.Cell(3, 7).Value.IsNumber.Should().BeFalse("o Excel precisa se RECUSAR a somar esta célula");
        ws.Cell(3, 10).GetString().Should().Be("sem cálculo do ML");
        ws.Cell(3, 12).GetString().Should().Be("sem cálculo do ML");

        // E onde há cálculo, número de verdade — o texto não pode ter contaminado a coluna.
        ws.Cell(2, 7).Value.IsNumber.Should().BeTrue();
        ws.Cell(2, 7).GetDouble().Should().Be(3d);
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

        ws.Cell(3, 13).GetString().Should().Be("só o PBS foi calculado");
        ws.Cell(2, 13).GetString().Should().Be("ML", "sobra menor do ML nesta linha");
    }

    [Fact]
    public void Capa_declara_o_filtro_aplicado_e_o_denominador()
    {
        var filtro = new FiltroDeItens(LojaId: 18, Curva: "A");
        using var wb = Abrir(Build(filtro, Itens(), totalSemFiltro: 20_153));
        var ws = wb.Worksheet("Recorte");

        var texto = TextoDaAba(ws);
        texto.Should().Contain("18", "sem a loja na capa, dois recortes ficam indistinguíveis no disco");
        texto.Should().Contain("2 de 20.153 da sugestão",
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
        ws.Cell(1, 4).GetString().Should().Be("Categoria");
    }

    /// <summary>Item sem cadastro no PBS: a planilha explica, em vez de deixar a célula vazia.</summary>
    [Fact]
    public void Item_sem_cadastro_e_sem_categoria_sao_explicados()
    {
        using var wb = Abrir(Build(FiltroDeItens.Nenhum, Itens()));
        var ws = wb.Worksheet("Itens");

        ws.Cell(3, 3).GetString().Should().Contain("não encontrado no cadastro do PBS");
        ws.Cell(3, 4).GetString().Should().Be("(sem categoria)");
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
                SobraMlUnidades: 8m,
                ItensComSobraMl: 1,
                SobraPbsValor: 100m,
                ItensComValorPbs: 2,
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
            JanelaAlemDoHistorico: false, Categoria: "MPX ETICO", SobraMlValor: 80m),
        new(LojaId: 18, Sku: "999999", NomeProduto: null, Curva: null,
            CompraSugeridaPbs: 0m, CompraSugeridaMl: null, VendidoNaJanela: 0m,
            SobraPbsUnidades: 0m, SobraMlUnidades: null, SobraPbsValor: null,
            JanelaAlemDoHistorico: false, Categoria: null, SobraMlValor: null),
    ];

    private static XLWorkbook Abrir(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextoDaAba(IXLWorksheet ws) =>
        string.Join(" | ", ws.CellsUsed().Select(c => c.GetFormattedString()));
}
