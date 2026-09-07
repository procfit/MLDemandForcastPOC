using ClosedXML.Excel;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// A planilha das oportunidades de sortimento.
///
/// <para>
/// <b>Exporta o recorte que está na tela</b>, e não o mercado inteiro — pedido explícito do
/// patrocinador. E declara os filtros na capa pelo mesmo motivo da planilha de itens: ela é
/// levada solta para reunião, e sem o recorte declarado o número dela não tem como ser
/// conferido nem reproduzido.
/// </para>
///
/// <para>
/// <b>Há um preço só, e não dois.</b> Na planilha de itens comparados existe o preço da rede ao
/// lado do do mercado; aqui isso é impossível por definição — a lista é dos produtos que não
/// estão no cadastro da rede, então ela não os vende e não tem preço a comparar. Uma coluna
/// "preço da rede" viria vazia em toda linha.
/// </para>
/// </summary>
internal static class OportunidadesExcelExporter
{
    /// <summary>Celula vazia. Zero num preco diria que o mercado vende de graca.</summary>
    private static readonly XLCellValue Blank = "";

    public const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Gerar(
        OportunidadesPagina pagina,
        decimal corteMinimo,
        string? brick,
        string? area,
        string? laboratorio,
        bool truncado,
        DateTimeOffset geradoEm)
    {
        using var wb = new XLWorkbook();
        Capa(wb, pagina, corteMinimo, brick, area, laboratorio, truncado, geradoEm);
        Itens(wb, pagina);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Capa(
        XLWorkbook wb, OportunidadesPagina pagina, decimal corteMinimo,
        string? brick, string? area, string? laboratorio, bool truncado, DateTimeOffset geradoEm)
    {
        var ws = wb.Worksheets.Add("Recorte");
        var l = 1;

        void Par(string rotulo, object valor)
        {
            ws.Cell(l, 1).Value = rotulo;
            ws.Cell(l, 1).Style.Font.Bold = true;
            ws.Cell(l, 2).Value = XLCellValue.FromObject(valor);
            l++;
        }

        ws.Cell(l, 1).Value = "Oportunidades de sortimento — IQVIA";
        ws.Cell(l, 1).Style.Font.Bold = true;
        l += 2;

        Par("Gerado em", geradoEm.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        Par("Mês do mercado", pagina.Mes?.ToString("MM/yyyy") ?? "não apurado");
        Par("Códigos no seu cadastro", $"{pagina.EansNoCatalogo:N0}");
        l++;

        ws.Cell(l, 1).Value = "Filtros aplicados";
        ws.Cell(l, 1).Style.Font.Bold = true;
        l++;
        Par("Vende no bairro, por mês", $"{corteMinimo:N0} unidades ou mais");
        Par("Bairro", string.IsNullOrWhiteSpace(brick) ? "todos" : brick);
        Par("Área da farmácia", string.IsNullOrWhiteSpace(area) ? "todas" : area);
        Par("Laboratório", string.IsNullOrWhiteSpace(laboratorio) ? "todos" : laboratorio);
        Par("Oportunidades no recorte", $"{pagina.Total:N0}");
        Par("Linhas nesta planilha", $"{pagina.Itens.Count:N0}");

        // Planilha incompleta que se apresenta como completa e o pior desfecho: quem levar este
        // arquivo para reuniao tem de saber que o numero da capa e maior que o das linhas.
        if (truncado)
        {
            ws.Cell(l, 1).Value = "ATENÇÃO";
            ws.Cell(l, 1).Style.Font.Bold = true;
            ws.Cell(l, 2).Value =
                "A lista foi truncada: o recorte tem mais oportunidades do que esta planilha "
                + "carrega. Aumente o corte de unidades ou filtre por bairro, área ou "
                + "laboratório para reduzir o recorte.";
            l++;
        }

        l += 2;

        ws.Cell(l, 1).Value =
            "São produtos que o mercado vende no bairro e que NÃO estão no seu cadastro. O preço "
            + "unitário é o valor que a IQVIA reporta dividido pelas unidades que ela reporta — e a "
            + "IQVIA normaliza preços entre os participantes do painel, então ele é um índice e não "
            + "o preço de balcão do concorrente. Serve para dimensionar a oportunidade, não para "
            + "montar tabela de preço. Não há coluna de preço da sua rede porque, por definição, "
            + "ela não vende estes itens.";
        ws.Cell(l, 1).Style.Font.Italic = true;

        ws.Column(1).Width = 30;
        ws.Column(2).Width = 46;
    }

    private static void Itens(XLWorkbook wb, OportunidadesPagina pagina)
    {
        var ws = wb.Worksheets.Add("Oportunidades");
        string[] cabecalhos =
        [
            "Produto (nome na IQVIA)", "Código de barras", "Bairro (brick)",
            "Concorrentes venderam (un.)", "Preço unit. IQVIA",
            "Laboratório", "Área", "Classe terapêutica",
        ];

        for (var c = 0; c < cabecalhos.Length; c++)
        {
            ws.Cell(1, c + 1).Value = cabecalhos[c];
        }
        ws.Row(1).Style.Font.Bold = true;

        var linha = 2;
        foreach (var o in pagina.Itens)
        {
            // O nome da IQVIA pode faltar; aí vale o próprio código, nunca célula vazia.
            ws.Cell(linha, 1).Value = o.Descricao ?? $"(sem nome na IQVIA — código {o.Ean})";

            // Texto, e não número: o código de barras tem zero à esquerda, e o Excel o comeria.
            ws.Cell(linha, 2).SetValue(o.Ean);
            ws.Cell(linha, 3).Value = o.Brick;
            ws.Cell(linha, 4).Value = o.UnidadesConcorrentes;

            // Célula vazia, nunca zero: sem unidades não há preço, e zero diria que o mercado
            // vende de graça — e esta é uma das colunas por onde o comprador ordena.
            ws.Cell(linha, 5).Value = o.PrecoMedioMercado is { } p ? p : Blank;

            ws.Cell(linha, 6).Value = o.Laboratorio ?? "";
            ws.Cell(linha, 7).Value = o.AreaFarmacia ?? "";
            ws.Cell(linha, 8).Value = o.Classe4 ?? "";
            linha++;
        }

        ws.Columns(1, cabecalhos.Length).AdjustToContents();
    }
}
