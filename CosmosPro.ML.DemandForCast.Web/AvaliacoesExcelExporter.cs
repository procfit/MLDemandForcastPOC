using ClosedXML.Excel;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// A planilha de tabulação das avaliações: seção G e questionário, uma linha por execução.
///
/// <para>
/// <b>São dados brutos, de propósito.</b> O pedido do patrocinador é explícito: o POC registra,
/// mantém o vínculo e exporta; a tabulação e a análise são feitas depois, no Excel. Então aqui
/// não há média, contagem por faixa nem percentual — números derivados nesta planilha seriam
/// uma segunda versão da análise, competindo com a que ele vai montar.
/// </para>
///
/// <para>
/// <b>A chave é a execução, nunca o comprador.</b> O mesmo comprador avalia várias execuções, e
/// consolidar por ele apagaria a variação que a pesquisa mede. Cada linha traz o
/// <c>ID da execução</c>, que é o campo pelo qual as duas abas se relacionam com qualquer outro
/// recorte que ele cruzar depois.
/// </para>
///
/// <para>
/// <b>A aba "Perguntas" existe porque o instrumento foi renumerado duas vezes.</b> O código
/// <c>B7</c> já designou três afirmações distintas, então uma planilha com colunas B1..B11 e
/// nenhuma legenda é ambígua — e a ambiguidade é silenciosa, do tipo que só aparece na
/// conclusão. A legenda sai do <b>enunciado gravado com cada resposta</b>, não do catálogo
/// atual: só assim uma resposta dada sob a V5 é lida com a pergunta que a V5 fazia.
/// </para>
/// </summary>
internal static class AvaliacoesExcelExporter
{
    public const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Gerar(TabulacaoView tabulacao, string? redeNome, DateTimeOffset geradoEm)
    {
        using var wb = new XLWorkbook();
        Capa(wb, tabulacao, redeNome, geradoEm);
        Avaliacoes(wb, tabulacao, redeNome);
        Perguntas(wb, tabulacao);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Capa(
        XLWorkbook wb, TabulacaoView t, string? redeNome, DateTimeOffset geradoEm)
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

        ws.Cell(l, 1).Value = "Avaliações do POC — dados brutos";
        ws.Cell(l, 1).Style.Font.Bold = true;
        l += 2;

        Par("Gerado em", geradoEm.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        Par("Rede", redeNome ?? $"rede {t.RedeId}");
        Par("Execuções nesta planilha", $"{t.Linhas.Count:N0}");
        Par("Com avaliação (seção G)", $"{t.ComAvaliacao:N0}");
        Par("Com questionário enviado", $"{t.ComQuestionario:N0}");
        l++;

        // O denominador declarado. "12 avaliações" nao diz nada sem as execucoes de onde saíram,
        // e a planilha traz as duas coisas justamente para a conta poder ser feita.
        ws.Cell(l, 1).Value =
            "A planilha lista TODAS as execuções da rede, inclusive as que ninguém avaliou — o "
            + "denominador faz parte do dado. Linha sem avaliação e sem questionário fica com as "
            + "células em branco.";
        ws.Cell(l, 1).Style.Font.Italic = true;
        l += 2;

        if (t.VersoesPresentes.Count > 1)
        {
            ws.Cell(l, 1).Value = "ATENÇÃO";
            ws.Cell(l, 1).Style.Font.Bold = true;
            ws.Cell(l, 2).Value =
                $"Há respostas de {t.VersoesPresentes.Count} versões diferentes do questionário "
                + $"({string.Join(", ", t.VersoesPresentes.Select(v => $"v{v}"))}). O mesmo código "
                + "designa afirmações diferentes entre versões, então NÃO some uma coluna inteira "
                + "sem antes separar por 'Versão do questionário'. A aba Perguntas mostra o "
                + "enunciado de cada código em cada versão.";
            l += 2;
        }

        ws.Cell(l, 1).Value =
            "Nas afirmações da Parte B a célula traz o valor da escala (1 a 5). Nas perguntas de "
            + "caracterização (Parte A) traz o TEXTO da opção escolhida — inclusive na A2, cujas "
            + "faixas de experiência têm ordem mas cuja leitura útil é \"Entre 2 e 5 anos\", e não "
            + "\"2\". Célula em branco é ausência de resposta, nunca zero: zero seria a posição "
            + "mais baixa da escala.";
        ws.Cell(l, 1).Style.Font.Italic = true;

        ws.Column(1).Width = 30;
        ws.Column(2).Width = 80;
    }

    private static void Avaliacoes(XLWorkbook wb, TabulacaoView t, string? redeNome)
    {
        var ws = wb.Worksheets.Add("Avaliacoes");

        // Coluna de texto livre só para o código que de fato tem algum: uma por pergunta deixaria
        // catorze colunas vazias entre as respostas e esconderia as que importam.
        var comTextoLivre = t.Codigos
            .Where(c => t.Linhas.Any(l => !string.IsNullOrWhiteSpace(l.TextoLivre(c))))
            .ToList();

        List<string> cabecalhos =
        [
            "ID da execução", "Data/hora da execução", "Status da execução",
            "Sugestão (ERP)", "Descrição da sugestão", "Rede",
            "Avaliação (seção G)", "Comentário (seção G)", "Avaliado em", "Avaliador",
            "Questionário enviado em", "Versão do questionário", "Respondente",
            .. t.Codigos,
            .. comTextoLivre.Select(c => $"{c} — outro"),
        ];

        for (var c = 0; c < cabecalhos.Count; c++)
        {
            ws.Cell(1, c + 1).Value = cabecalhos[c];
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        var linha = 2;
        foreach (var l in t.Linhas)
        {
            var c = 1;

            // Texto, e não o Guid tipado: é chave de cruzamento, e o Excel não deve reformatá-la.
            ws.Cell(linha, c++).SetValue(l.SessaoId.ToString());
            ws.Cell(linha, c++).Value = l.CriadoEm.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            ws.Cell(linha, c++).Value = l.Status;
            ws.Cell(linha, c++).Value = l.SugestaoId is { } sid ? sid : Vazio;
            ws.Cell(linha, c++).Value = l.SugestaoDescricao ?? "";
            ws.Cell(linha, c++).Value = redeNome ?? $"rede {t.RedeId}";

            ws.Cell(linha, c++).Value = l.AvaliacaoVeredito ?? "";
            ws.Cell(linha, c++).Value = l.AvaliacaoComentario ?? "";
            ws.Cell(linha, c++).Value = l.AvaliacaoEm is { } ae
                ? ae.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : "";
            ws.Cell(linha, c++).Value = l.Avaliador ?? "";

            ws.Cell(linha, c++).Value = l.QuestionarioEnviadoEm is { } qe
                ? qe.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : "";
            ws.Cell(linha, c++).Value = l.VersaoCatalogo is { } v ? v : Vazio;
            ws.Cell(linha, c++).Value = l.Respondente ?? "";

            foreach (var codigo in t.Codigos)
            {
                var valor = l.Valor(codigo, t.EhTexto(codigo));

                // Número entra como número para o Excel poder somar; texto entra como texto.
                // Célula vazia é ausência de resposta, e nunca zero — zero seria uma posição na
                // escala, e a mais baixa.
                if (valor.Length == 0)
                {
                    ws.Cell(linha, c++).Value = Vazio;
                }
                else if (int.TryParse(valor, out var n))
                {
                    ws.Cell(linha, c++).Value = n;
                }
                else
                {
                    ws.Cell(linha, c++).Value = valor;
                }
            }

            foreach (var codigo in comTextoLivre)
            {
                ws.Cell(linha, c++).Value = l.TextoLivre(codigo) ?? "";
            }

            linha++;
        }

        ws.Columns(1, cabecalhos.Count).AdjustToContents();

        // Descrição e comentário são texto longo; AdjustToContents os deixa absurdos.
        ws.Column(5).Width = 40;
        ws.Column(8).Width = 50;
    }

    /// <summary>
    /// A legenda: para cada versão presente, o enunciado que cada código de fato exibiu. Sai do
    /// retrato gravado com a resposta — o catálogo atual só conhece a versão corrente.
    /// </summary>
    private static void Perguntas(XLWorkbook wb, TabulacaoView t)
    {
        var ws = wb.Worksheets.Add("Perguntas");

        ws.Cell(1, 1).Value = "Versão do questionário";
        ws.Cell(1, 2).Value = "Código";
        ws.Cell(1, 3).Value = "Enunciado exibido";
        ws.Cell(1, 4).Value = "Respostas";
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        var legenda = t.Linhas
            .Where(l => l.VersaoCatalogo is not null)
            .SelectMany(l => l.Respostas.Select(r => new
            {
                Versao = l.VersaoCatalogo!.Value,
                r.PerguntaCodigo,
                r.PerguntaTexto,
            }))
            .GroupBy(x => (x.Versao, x.PerguntaCodigo, x.PerguntaTexto))
            .Select(g => new
            {
                g.Key.Versao,
                g.Key.PerguntaCodigo,
                g.Key.PerguntaTexto,
                Respostas = g.Count(),
            })
            .OrderBy(x => x.Versao)
            .ThenBy(x => Ordem(x.PerguntaCodigo))
            .ThenBy(x => x.PerguntaCodigo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var linha = 2;
        foreach (var item in legenda)
        {
            ws.Cell(linha, 1).Value = item.Versao;
            ws.Cell(linha, 2).Value = item.PerguntaCodigo;
            ws.Cell(linha, 3).Value = item.PerguntaTexto;
            ws.Cell(linha, 4).Value = item.Respostas;
            linha++;
        }

        if (legenda.Count == 0)
        {
            ws.Cell(2, 1).Value = "Nenhum questionário respondido ainda.";
            ws.Cell(2, 1).Style.Font.Italic = true;
        }

        ws.Column(1).Width = 22;
        ws.Column(2).Width = 12;
        ws.Column(3).Width = 110;
        ws.Column(4).Width = 12;
    }

    /// <summary>
    /// Ordem natural do código, e não alfabética: sem isto B10 vem antes de B2, e a legenda de
    /// um instrumento de onze afirmações fica fora da ordem em que ele é aplicado.
    /// </summary>
    private static (char Parte, int Numero) Ordem(string codigo)
    {
        if (codigo.Length < 2) return (codigo.Length == 0 ? 'Z' : codigo[0], 0);
        return int.TryParse(codigo[1..], out var n) ? (codigo[0], n) : (codigo[0], int.MaxValue);
    }

    /// <summary>Célula vazia. Zero numa escala de 1 a 5 seria a posição mais baixa.</summary>
    private static readonly XLCellValue Vazio = "";
}
