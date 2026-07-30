using ClosedXML.Excel;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// Gera planilhas .xlsx a partir do resultado já parseado de uma simulação de compra (F8).
/// Trabalha sobre os dados que a tela já tem em memória — não faz I/O nem chama a API.
/// Células de data/número são tipadas nativamente para o Excel respeitar o locale (pt-BR).
/// </summary>
public static class PurchasingExcelExporter
{
    public const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string ClassicPolicy = "emax-eseg";
    private const string ForecastPolicy = "forecast-rop";

    private static string LabelPolitica(string p) => p switch
    {
        ClassicPolicy => "eMax/eSeg",
        ForecastPolicy => "ROP+forecast",
        _ => p,
    };

    public static byte[] BuildSugestaoHoje(SimulationOutput output)
    {
        using var wb = new XLWorkbook();
        WriteSugestaoSheet(wb, output);
        return ToBytes(wb);
    }

    public static byte[] BuildLivroPedidos(SimulationOutput output, string? policy)
    {
        using var wb = new XLWorkbook();
        WriteLivroSheet(wb, output, policy);
        return ToBytes(wb);
    }

    public static byte[] BuildCompleto(SimulationOutput output, string? livroPolicy)
    {
        using var wb = new XLWorkbook();
        WriteSugestaoSheet(wb, output);
        WriteLivroSheet(wb, output, livroPolicy);
        return ToBytes(wb);
    }

    private static void WriteSugestaoSheet(XLWorkbook wb, SimulationOutput output)
    {
        var ws = wb.Worksheets.Add("Sugestão de hoje");
        var politicas = output.Resultado.Politicas;
        var produtos = output.Produtos ?? new Dictionary<string, string>();
        // ClassicPolicy só existe em ResultadoJson de execuções anteriores a F13 (EMaxESegPolicy
        // removida). Sem ela, não há "clássico" contra o qual calcular um delta.
        var temClassico = politicas.Any(p => p.Policy == ClassicPolicy);

        // Cabeçalho: SKU, Produto, Loja, [Pos. / Pedir] por política, Δ (ML − clássico) se aplicável.
        var header = new List<string> { "SKU", "Produto", "Loja" };
        foreach (var p in politicas)
        {
            var label = LabelPolitica(p.Policy);
            header.Add($"Pos. {label}");
            header.Add($"Pedir {label}");
        }
        if (temClassico) header.Add("Δ (ML − clássico)");
        for (int c = 0; c < header.Count; c++)
            ws.Cell(1, c + 1).Value = header[c];

        var rows = FundirListaCompra(politicas, produtos);
        int row = 2;
        foreach (var r in rows)
        {
            int col = 1;
            ws.Cell(row, col++).Value = r.Sku;
            ws.Cell(row, col++).Value = r.Nome;
            ws.Cell(row, col++).Value = r.LojaId;
            foreach (var p in politicas)
            {
                ws.Cell(row, col++).Value = r.PosicaoByPolicy.GetValueOrDefault(p.Policy);
                ws.Cell(row, col++).Value = r.QtdByPolicy.GetValueOrDefault(p.Policy);
            }
            if (temClassico)
            {
                var delta = r.QtdByPolicy.GetValueOrDefault(ForecastPolicy) - r.QtdByPolicy.GetValueOrDefault(ClassicPolicy);
                ws.Cell(row, col).Value = delta;
            }
            row++;
        }

        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    private static void WriteLivroSheet(XLWorkbook wb, SimulationOutput output, string? policy)
    {
        var ws = wb.Worksheets.Add("Livro de pedidos");
        var produtos = output.Produtos ?? new Dictionary<string, string>();
        var politicas = output.Resultado.Politicas;
        var pol = policy ?? politicas.FirstOrDefault()?.Policy;
        var pedidos = (politicas.FirstOrDefault(p => p.Policy == pol)?.Pedidos ?? [])
            .OrderByDescending(o => o.Data)
            .ToList();

        string[] header = ["Data", "SKU", "Produto", "Loja", "Posição", "Ponto pedido (s)", "Alvo (S)", "Qtd pedida"];
        for (int c = 0; c < header.Length; c++)
            ws.Cell(1, c + 1).Value = header[c];

        int row = 2;
        foreach (var o in pedidos)
        {
            ws.Cell(row, 1).Value = o.Data.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 2).Value = o.Sku;
            ws.Cell(row, 3).Value = produtos.GetValueOrDefault(o.Sku) ?? "";
            ws.Cell(row, 4).Value = o.LojaId;
            ws.Cell(row, 5).Value = o.PosicaoAntes;
            ws.Cell(row, 6).Value = o.ReorderPoint;
            ws.Cell(row, 7).Value = o.OrderUpToLevel;
            ws.Cell(row, 8).Value = o.Quantidade;
            row++;
        }

        ws.Column(1).Style.DateFormat.Format = "dd/mm/yyyy";
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    /// <summary>
    /// Funde a "foto do último dia" (ListaCompraFinal) das políticas por (SKU, loja),
    /// espelhando a montagem da grade "Sugestão de hoje" na tela.
    /// </summary>
    private static List<FusedRow> FundirListaCompra(
        IReadOnlyList<PolicySimulationResultView> politicas,
        IReadOnlyDictionary<string, string> produtos)
    {
        var acc = new Dictionary<(string Sku, int Loja), (Dictionary<string, decimal> Pos, Dictionary<string, decimal> Qtd)>();
        foreach (var p in politicas)
        {
            foreach (var item in p.ListaCompraFinal ?? [])
            {
                var key = (item.Sku, item.LojaId);
                if (!acc.TryGetValue(key, out var entry))
                {
                    entry = (new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
                             new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));
                    acc[key] = entry;
                }
                entry.Pos[p.Policy] = item.Posicao;
                entry.Qtd[p.Policy] = item.QuantidadeSugerida;
            }
        }

        return acc
            .Select(kv => new FusedRow(
                kv.Key.Sku,
                produtos.GetValueOrDefault(kv.Key.Sku) ?? "",
                kv.Key.Loja,
                kv.Value.Pos,
                kv.Value.Qtd))
            .OrderByDescending(b => b.QtdByPolicy.Values.DefaultIfEmpty(0m).Max())
            .ThenBy(b => b.Sku, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static byte[] ToBytes(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private sealed record FusedRow(
        string Sku,
        string Nome,
        int LojaId,
        IReadOnlyDictionary<string, decimal> PosicaoByPolicy,
        IReadOnlyDictionary<string, decimal> QtdByPolicy);
}
