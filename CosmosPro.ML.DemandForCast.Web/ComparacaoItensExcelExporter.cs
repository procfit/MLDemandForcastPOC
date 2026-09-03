using System.Globalization;
using ClosedXML.Excel;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// Gera a planilha .xlsx dos itens de uma comparação, para o comprador aprofundar a análise
/// fora do sistema.
///
/// <para>
/// <b>Exporta exatamente o recorte que a tela mostrava.</b> O filtro viaja para a apiservice e
/// a planilha nasce do que ela devolveu — não de um subconjunto carregado na tela nem da
/// população inteira. Uma planilha que ignorasse o filtro seria pior que nenhuma: o comprador
/// pediria "só a loja 18" e levaria 20 mil linhas sem perceber.
/// </para>
///
/// <para>
/// A primeira aba descreve o recorte em texto — sessão, filtros aplicados e sobre quantos itens
/// os totais falam. Sem isso, dois arquivos exportados de filtros diferentes ficam
/// indistinguíveis no disco de quem baixou, e é assim que se compara sem saber a maçã com a
/// laranja.
/// </para>
/// </summary>
public static class ComparacaoItensExcelExporter
{
    public const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// Texto que ocupa a célula onde o braço de ML não tem número.
    ///
    /// <para>
    /// <b>Nunca zero, nunca traço.</b> Nulo ali significa "não foi possível calcular", e um
    /// zero na planilha somaria na tabela dinâmica do comprador como se o ML tivesse mandado
    /// não comprar nada — a afirmação oposta. Texto numa coluna numérica é feio de propósito:
    /// o Excel se recusa a somá-lo, que é justamente o comportamento correto aqui.
    /// </para>
    /// </summary>
    private const string SemCalculoMl = "sem cálculo do ML";

    public static byte[] Build(
        Guid sessaoId,
        string? nomeDaSessao,
        long? sugestaoId,
        FiltroDeItens filtro,
        FiltrosDisponiveis? disponiveis,
        TotaisDosItens? totais,
        int totalSemFiltro,
        IReadOnlyList<SessaoItem> itens,
        DateTimeOffset geradoEm)
    {
        using var wb = new XLWorkbook();
        EscreverRecorte(wb, sessaoId, nomeDaSessao, sugestaoId, filtro, disponiveis, totais, totalSemFiltro, itens, geradoEm);
        EscreverItens(wb, itens);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void EscreverRecorte(
        XLWorkbook wb,
        Guid sessaoId,
        string? nomeDaSessao,
        long? sugestaoId,
        FiltroDeItens filtro,
        FiltrosDisponiveis? disponiveis,
        TotaisDosItens? totais,
        int totalSemFiltro,
        IReadOnlyList<SessaoItem> itens,
        DateTimeOffset geradoEm)
    {
        var ws = wb.Worksheets.Add("Recorte");
        var l = 1;

        ws.Cell(l, 1).Value = "Comparação PBS × ML — itens exportados";
        ws.Cell(l, 1).Style.Font.Bold = true;
        ws.Cell(l, 1).Style.Font.FontSize = 13;
        l += 2;

        void Par(string rotulo, XLCellValue valor)
        {
            ws.Cell(l, 1).Value = rotulo;
            ws.Cell(l, 1).Style.Font.Bold = true;
            ws.Cell(l, 2).Value = valor;
            l++;
        }

        Par("Comparação", nomeDaSessao ?? sessaoId.ToString());
        Par("Sugestão do PBS", sugestaoId?.ToString() ?? "não declarada");
        Par("Exportado em", geradoEm.ToLocalTime().DateTime);
        ws.Cell(l - 1, 2).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
        l++;

        ws.Cell(l, 1).Value = "Filtros aplicados";
        ws.Cell(l, 1).Style.Font.Bold = true;
        l++;
        Par("Loja", filtro.LojaId?.ToString() ?? "todas");
        Par("Categoria", Rotulo(filtro.Categoria, "todas"));
        Par("Curva do ERP", Rotulo(filtro.Curva, "todas"));
        Par("Itens no recorte", $"{itens.Count:N0} de {totalSemFiltro:N0} da sugestão");
        l++;

        if (disponiveis?.TemItemSemCategoria == true && disponiveis.Categorias.Count == 0)
        {
            ws.Cell(l, 1).Value =
                "Nenhum item desta comparação tem categoria: ela é gravada na materialização, e "
                + "comparações executadas antes dessa coluna existir ficaram sem o dado. Rode uma "
                + "comparação nova para ter categoria.";
            ws.Cell(l, 1).Style.Font.Italic = true;
            l += 2;
        }

        if (totais is null) return;

        ws.Cell(l, 1).Value = "Totais do recorte";
        ws.Cell(l, 1).Style.Font.Bold = true;
        l++;
        Par("Itens analisados", totais.Itens);
        Par("Compra PBS (un.)", totais.CompraPbsUnidades);
        Par("Compra ML (un.)", Numero(totais.CompraMlUnidades));
        Par("→ apurada sobre", $"{totais.ItensComCompraMl:N0} item(ns) com cálculo do ML");
        Par("Vendido no período (un.)", totais.VendidoNaJanela);
        Par("Sobra PBS (un.)", totais.SobraPbsUnidades);
        Par("R$ parado PBS", Numero(totais.SobraPbsValor));
        l++;

        // Bloco proprio, e com o lado do PBS recalculado: a planilha tem de trazer a mesma
        // separacao da tela. Enquanto a diferenca saia do total do PBS contra a soma do ML,
        // quem abrisse o Excel levava o numero errado para dentro de uma apresentacao.
        ws.Cell(l, 1).Value = $"Comparação PBS × ML — {totais.ItensComSobraMl:N0} item(ns)";
        ws.Cell(l, 1).Style.Font.Bold = true;
        l++;
        Par("Sobra PBS (un.) — mesmos itens", Numero(totais.SobraPbsComparavelUnidades));
        Par("Sobra ML (un.)", Numero(totais.SobraMlUnidades));
        Par("Diferença de sobra ML − PBS (un.)", Numero(totais.DiferencaSobraUnidades));
        Par("R$ parado PBS — mesmos itens", Numero(totais.SobraPbsComparavelValor));
        Par("R$ parado ML", Numero(totais.SobraMlValor));
        Par("Diferença em R$", Numero(totais.DiferencaSobraValor));
        l++;

        ws.Cell(l, 1).Value =
            "As somas do braço de ML falam apenas dos itens em que ele pôde ser calculado. Por "
            + "isso a comparação tem bloco próprio, com o PBS recalculado sobre esses mesmos "
            + "itens: subtrair a soma do ML do total geral do PBS mediria a diferença de "
            + "população, não a dos métodos. Célula com \"" + SemCalculoMl + "\" não é zero: é "
            + "ausência de cálculo, e somá-la como zero inverteria a leitura.";
        ws.Cell(l, 1).Style.Font.Italic = true;

        ws.Column(1).Width = 34;
        ws.Column(2).Width = 46;
    }

    private static void EscreverItens(XLWorkbook wb, IReadOnlyList<SessaoItem> itens)
    {
        var ws = wb.Worksheets.Add("Itens");
        string[] cabecalhos =
        [
            "Loja", "SKU", "Produto", "EAN", "Fabricante", "Categoria", "Curva",
            "Estoque na sugestão (un.)", "Estoque no fim (un.)",
            "Comprado (PBS un.)", "Compraria (ML un.)", "Vendido no período (un.)",
            "Sobrou (PBS un.)", "Sobraria (ML un.)",
            "R$ parado (PBS)", "R$ parado (ML)", "Quem ficou mais perto", "Ressalva",
            "Mês IQVIA", "Bairro (brick)", "Vendemos no bairro (un.)",
            "Concorrentes no bairro (un.)", "Índice vs bairro", "Dias sem estoque",
            "Alerta de mercado",
        ];

        for (var c = 0; c < cabecalhos.Length; c++)
        {
            ws.Cell(1, c + 1).Value = cabecalhos[c];
        }
        ws.Row(1).Style.Font.Bold = true;

        var linha = 2;
        foreach (var i in itens)
        {
            ws.Cell(linha, 1).Value = i.LojaId;
            ws.Cell(linha, 2).Value = i.Sku;
            ws.Cell(linha, 3).Value = i.NomeProduto ?? $"(SKU {i.Sku} não encontrado no cadastro do PBS)";
            // Texto, e nao numero: o EAN do PBS tem 14 posicoes com zero a esquerda, e o
            // Excel converteria para numero comendo o zero -- o codigo deixaria de casar com
            // a embalagem e com o proprio ERP.
            ws.Cell(linha, 4).SetValue(i.Ean ?? "");
            ws.Cell(linha, 5).Value = i.Fabricante ?? "";
            ws.Cell(linha, 6).Value = i.Categoria ?? "(sem categoria)";
            ws.Cell(linha, 7).Value = i.Curva ?? "(sem curva)";
            // Celula VAZIA, nunca zero: zero e prateleira vazia, que e medicao.
            ws.Cell(linha, 8).Value = i.EstoqueNaSugestao is { } es ? es : Blank;
            ws.Cell(linha, 9).Value = i.EstoqueNoFimDoPeriodo is { } ef ? ef : Blank;
            ws.Cell(linha, 10).Value = i.CompraSugeridaPbs;
            ws.Cell(linha, 11).Value = Numero(i.CompraSugeridaMl);
            ws.Cell(linha, 12).Value = i.VendidoNaJanela;
            ws.Cell(linha, 13).Value = i.SobraPbsUnidades;
            ws.Cell(linha, 14).Value = Numero(i.SobraMlUnidades);
            ws.Cell(linha, 15).Value = Numero(i.SobraPbsValor);
            ws.Cell(linha, 16).Value = Numero(i.SobraMlValor);
            ws.Cell(linha, 17).Value = QuemFicouMaisPerto(i);
            ws.Cell(linha, 18).Value = i.JanelaAlemDoHistorico ? "período incompleto" : "";

            // Célula VAZIA, nunca zero, quando não houve medição de mercado. A planilha é
            // ordenada pelo comprador: zero no índice colocaria o item sem medição junto
            // dos piores, e zero em unidades afirmaria que o bairro não vende o item.
            ws.Cell(linha, 19).Value = i.MercadoMes is { } m
                ? m.ToString("MM/yyyy", CultureInfo.InvariantCulture)
                : "";
            ws.Cell(linha, 20).Value = i.MercadoBrick ?? "";
            ws.Cell(linha, 21).Value = i.MercadoUnidadesRede is { } ur ? ur : Blank;
            ws.Cell(linha, 22).Value = i.MercadoUnidadesConcorrentes is { } uc ? uc : Blank;
            ws.Cell(linha, 23).Value = i.MercadoIndiceDesempenho is { } ix ? ix : Blank;
            ws.Cell(linha, 24).Value = i.MercadoDiasSemEstoque is { } de ? de : Blank;
            ws.Cell(linha, 25).Value = i.AlertaDeMercadoLegivel
                ?? (i.TemDadoDeMercado ? "dentro do esperado" : "sem dado de mercado");
            linha++;
        }

        if (linha > 2)
        {
            ws.Range(1, 1, linha - 1, cabecalhos.Length).SetAutoFilter();
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns(1, cabecalhos.Length).AdjustToContents();
    }

    /// <summary>
    /// Mesma regra da tela: menor sobra ganha, e sem cálculo de ML não há vencedor — nem
    /// empate. Duplicar a frase aqui é deliberado: a planilha vai para fora do sistema e
    /// precisa se explicar sozinha.
    /// </summary>
    private static string QuemFicouMaisPerto(SessaoItem i) => i.SobraMlUnidades switch
    {
        null => "só o PBS foi calculado",
        var ml when ml < i.SobraPbsUnidades => "ML",
        var ml when ml > i.SobraPbsUnidades => "PBS",
        _ => "empate",
    };

    private static XLCellValue Numero(decimal? v) => v is { } d ? d : SemCalculoMl;

    /// <summary>
    /// Célula em branco para medida de mercado ausente. Diferente de <c>SemCalculoMl</c>: lá
    /// a ausência é um desfecho esperado do braço de ML e a planilha a nomeia; aqui a coluna
    /// só existe quando houve medição, e a coluna "Alerta de mercado" já diz "sem dado de
    /// mercado" na mesma linha.
    /// </summary>
    private static readonly XLCellValue Blank = "";

    private static string Rotulo(string? filtro, string quandoAusente) => filtro switch
    {
        null or "" => quandoAusente,
        FiltroDeItens.Ausente => "apenas itens sem esse dado",
        _ => filtro,
    };
}
