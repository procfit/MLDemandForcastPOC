using ClosedXML.Excel;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// A planilha de tabulação das avaliações, em <b>duas abas de dado</b>: a seção G por execução e
/// o questionário por comprador.
///
/// <para>
/// <b>Por que duas, e não uma.</b> A seção G é por execução; o questionário é por comprador,
/// respondido uma vez só (19/09/2026). Numa aba só, as respostas do questionário apareceriam
/// repetidas em cada execução da mesma pessoa, e qualquer contagem feita sobre a coluna sairia
/// multiplicada pelo número de execuções — a planilha pareceria ter N questionários onde há um.
/// O que liga as duas abas é o <b>código do participante</b>.
/// </para>
///
/// <para>
/// <b>São dados brutos, de propósito.</b> O pedido do patrocinador é explícito: o POC registra,
/// mantém o vínculo e exporta; a tabulação e a análise são feitas depois, no Excel. Então aqui
/// não há média, contagem por faixa nem percentual — números derivados nesta planilha seriam
/// uma segunda versão da análise, competindo com a que ele vai montar.
/// </para>
///
/// <para>
/// <b>O participante aparece por código, nunca por nome.</b> P01, P02, P03… O servidor não
/// consulta a tabela de usuários para montar esta planilha, então o dado identificável não passa
/// por aqui — é a solução que o patrocinador propôs para poder contar pessoas e repetições sem
/// identificar ninguém.
/// </para>
///
/// <para>
/// <b>A aba "Perguntas" traz o enunciado gravado com cada resposta</b>, e não o catálogo atual.
/// É o que torna uma resposta legível independentemente de o instrumento ter sido editado depois
/// — e é por isso que ela não depende de nenhum número de versão.
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
        Execucoes(wb, tabulacao, redeNome);
        Questionarios(wb, tabulacao, redeNome);
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
        Par("Execuções nesta planilha", $"{t.Execucoes.Count:N0}");
        Par("Execuções avaliadas (seção G)", $"{t.ComAvaliacao:N0}");
        Par("Participantes distintos", $"{t.Participantes:N0}");
        Par("Questionários respondidos", $"{t.Respondidos:N0}");
        l++;

        ws.Cell(l, 1).Value =
            "Esta planilha tem DUAS abas de dado, e elas medem coisas diferentes. 'Execucoes' "
            + "traz a seção G, que avalia UMA execução. 'Questionarios' traz o questionário "
            + "sobre o protótipo, que cada comprador responde UMA VEZ, depois de pelo menos duas "
            + "execuções avaliadas. Não junte as duas por linha: o que as liga é o código do "
            + "participante.";
        ws.Cell(l, 1).Style.Font.Italic = true;
        l += 2;

        ws.Cell(l, 1).Value =
            "As colunas Avaliador e Respondente trazem um CÓDIGO do participante (P01, P02, …), "
            + "nunca o nome ou o e-mail. O mesmo código é sempre a mesma pessoa dentro desta "
            + "rede, então agrupar por ele diz quantas execuções cada participante avaliou. A "
            + "numeração é por rede: em análise que junte duas redes, o que identifica o "
            + "participante é o par (Rede, código).";
        ws.Cell(l, 1).Style.Font.Italic = true;
        l += 2;

        // O denominador declarado. "12 avaliações" nao diz nada sem as execucoes de onde saíram,
        // e a planilha traz as duas coisas justamente para a conta poder ser feita.
        ws.Cell(l, 1).Value =
            "A aba Execucoes lista TODAS as execuções da rede, inclusive as que ninguém avaliou "
            + "— o denominador faz parte do dado. Linha sem avaliação fica com as células em "
            + "branco.";
        ws.Cell(l, 1).Style.Font.Italic = true;
        l += 2;

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

    /// <summary>A seção G: uma linha por execução, inclusive as não avaliadas.</summary>
    private static void Execucoes(XLWorkbook wb, TabulacaoView t, string? redeNome)
    {
        var ws = wb.Worksheets.Add("Execucoes");

        List<string> cabecalhos =
        [
            "ID da execução", "Data/hora da execução", "Status da execução",
            "Sugestão (ERP)", "Descrição da sugestão", "Rede",
            "Avaliação (seção G)", "Pontos fortes e fracos", "Avaliado em", "Avaliador",
        ];

        for (var c = 0; c < cabecalhos.Count; c++)
        {
            ws.Cell(1, c + 1).Value = cabecalhos[c];
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        var linha = 2;
        foreach (var l in t.Execucoes)
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

            linha++;
        }

        ws.Columns(1, cabecalhos.Count).AdjustToContents();

        // Descrição e comentário são texto longo; AdjustToContents os deixa absurdos.
        ws.Column(5).Width = 40;
        ws.Column(8).Width = 50;
    }

    /// <summary>O questionário: uma linha por comprador.</summary>
    private static void Questionarios(XLWorkbook wb, TabulacaoView t, string? redeNome)
    {
        var ws = wb.Worksheets.Add("Questionarios");

        // Coluna de texto livre só para o código que de fato tem algum: uma por pergunta deixaria
        // catorze colunas vazias entre as respostas e esconderia as que importam.
        var comTextoLivre = t.Codigos
            .Where(c => t.Questionarios.Any(q => !string.IsNullOrWhiteSpace(q.TextoLivre(c))))
            .ToList();

        List<string> cabecalhos =
        [
            "Respondente", "Rede", "Respondido em",
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
        foreach (var q in t.Questionarios)
        {
            var c = 1;

            ws.Cell(linha, c++).Value = q.Respondente ?? "";
            ws.Cell(linha, c++).Value = redeNome ?? $"rede {t.RedeId}";
            ws.Cell(linha, c++).Value = q.EnviadoEm is { } e
                ? e.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : "";

            foreach (var codigo in t.Codigos)
            {
                var valor = q.Valor(codigo, t.EhTexto(codigo));

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
                ws.Cell(linha, c++).Value = q.TextoLivre(codigo) ?? "";
            }

            linha++;
        }

        if (t.Questionarios.Count == 0)
        {
            ws.Cell(2, 1).Value = "Nenhum comprador respondeu o questionário ainda.";
            ws.Cell(2, 1).Style.Font.Italic = true;
        }

        ws.Columns(1, cabecalhos.Count).AdjustToContents();
    }

    /// <summary>
    /// A legenda: para cada código, o enunciado que ele de fato exibiu. Sai do retrato gravado
    /// com a resposta, e não do catálogo atual — é o que mantém a resposta legível se o
    /// instrumento for editado depois.
    ///
    /// <para>
    /// <b>Duas linhas para o mesmo código significam que o catálogo foi editado com respostas já
    /// coletadas.</b> A aba mostra as duas em vez de escolher uma: escolher seria a planilha
    /// afirmando que todo mundo leu o mesmo enunciado.
    /// </para>
    /// </summary>
    private static void Perguntas(XLWorkbook wb, TabulacaoView t)
    {
        var ws = wb.Worksheets.Add("Perguntas");

        ws.Cell(1, 1).Value = "Código";
        ws.Cell(1, 2).Value = "Enunciado exibido";
        ws.Cell(1, 3).Value = "Respostas";
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        var legenda = t.Questionarios
            .SelectMany(q => q.Respostas)
            .GroupBy(r => (r.PerguntaCodigo, r.PerguntaTexto))
            .Select(g => new
            {
                g.Key.PerguntaCodigo,
                g.Key.PerguntaTexto,
                Respostas = g.Count(),
            })
            .OrderBy(x => Ordem(x.PerguntaCodigo))
            .ThenBy(x => x.PerguntaCodigo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var linha = 2;
        foreach (var item in legenda)
        {
            ws.Cell(linha, 1).Value = item.PerguntaCodigo;
            ws.Cell(linha, 2).Value = item.PerguntaTexto;
            ws.Cell(linha, 3).Value = item.Respostas;
            linha++;
        }

        if (legenda.Count == 0)
        {
            ws.Cell(2, 1).Value = "Nenhum questionário respondido ainda.";
            ws.Cell(2, 1).Style.Font.Italic = true;
        }

        ws.Column(1).Width = 12;
        ws.Column(2).Width = 110;
        ws.Column(3).Width = 12;
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
