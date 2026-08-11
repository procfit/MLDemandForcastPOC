using System.Globalization;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Escolha das lojas que entram no ZIP. Diálogo separado porque uma sugestão pode ter
/// uma centena de lojas, que não cabem no form principal sem espremer o grid.
/// <para>
/// Nada vem marcado por padrão: marcar três é menos trabalho que desmarcar noventa e
/// cinco, e o padrão que erra tem de errar para o lado de não exportar.
/// </para>
/// </summary>
internal sealed class SelecaoDeLojasDialog : Form
{
    private readonly CheckedListBox _lista = new() { Width = 460, Height = 300, CheckOnClick = true };
    private readonly TextBox _filtro = new() { Width = 300, PlaceholderText = "filtrar por id ou nome" };
    private readonly Button _todas = new() { Text = "Marcar todas", Width = 110 };
    private readonly Button _nenhuma = new() { Text = "Desmarcar", Width = 90 };
    private readonly Button _ok = new() { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
    private readonly Button _cancelar = new() { Text = "Cancelar", Width = 90, DialogResult = DialogResult.Cancel };
    private readonly Label _resumo = new() { Width = 460, AutoSize = false };

    private readonly IReadOnlyList<LojaDaSugestao> _lojas;
    private readonly HashSet<int> _marcadas;

    private SelecaoDeLojasDialog(IReadOnlyList<LojaDaSugestao> lojas, IReadOnlyList<int> jaEscolhidas)
    {
        _lojas = lojas;
        _marcadas = [.. jaEscolhidas];

        Text = "Escolher lojas";
        // FixedDialog, não Sizable: os controles usam posição absoluta e nenhum tem
        // âncora -- arrastar a borda para dentro cortava OK/Cancelar, e o diálogo não
        // tem motivo para redimensionar (a lista já tem scroll próprio).
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        // 538, não 500: a linha filtro+botões (12 + 300 + 8 + 110 + 6 + 90) só cabe com
        // margem direita de 12px simétrica à esquerda se o cliente tiver 538px de largura.
        ClientSize = new Size(538, 420);
        AcceptButton = _ok;
        CancelButton = _cancelar;

        _filtro.Location = new Point(12, 12);
        _todas.Location = new Point(320, 11);
        _nenhuma.Location = new Point(436, 11);
        _lista.Location = new Point(12, 44);
        _resumo.Location = new Point(12, 352);
        _ok.Location = new Point(296, 380);
        _cancelar.Location = new Point(396, 380);
        Controls.AddRange([_filtro, _todas, _nenhuma, _lista, _resumo, _ok, _cancelar]);

        _filtro.TextChanged += (_, _) => Popular();
        _lista.ItemCheck += (_, e) => AoMarcar(e);
        _todas.Click += (_, _) => MarcarTodas(true);
        _nenhuma.Click += (_, _) => MarcarTodas(false);

        Popular();
    }

    /// <summary>Devolve <c>null</c> quando o operador cancela — diferente de lista vazia.</summary>
    public static IReadOnlyList<int>? Escolher(
        IWin32Window dono, IReadOnlyList<LojaDaSugestao> lojas, IReadOnlyList<int> jaEscolhidas)
    {
        using var dialogo = new SelecaoDeLojasDialog(lojas, jaEscolhidas);
        return dialogo.ShowDialog(dono) == DialogResult.OK ? [.. dialogo._marcadas.Order()] : null;
    }

    internal static IReadOnlyList<LojaDaSugestao> Filtrar(IReadOnlyList<LojaDaSugestao> lojas, string? termo)
    {
        if (string.IsNullOrWhiteSpace(termo)) return lojas;

        var busca = termo.Trim();
        return [.. lojas.Where(l =>
            l.LojaId.ToString(CultureInfo.InvariantCulture) == busca
            || l.Nome.Contains(busca, StringComparison.OrdinalIgnoreCase))];
    }

    private void Popular()
    {
        _lista.BeginUpdate();
        _lista.Items.Clear();
        foreach (var loja in Filtrar(_lojas, _filtro.Text))
        {
            _lista.Items.Add(loja, _marcadas.Contains(loja.LojaId));
        }
        _lista.EndUpdate();
        AtualizarResumo();
    }

    private void AoMarcar(ItemCheckEventArgs e)
    {
        if (_lista.Items[e.Index] is not LojaDaSugestao loja) return;

        if (e.NewValue == CheckState.Checked) _marcadas.Add(loja.LojaId);
        else _marcadas.Remove(loja.LojaId);

        // O ItemCheck roda ANTES de o item mudar de estado, então o resumo precisa ser
        // recalculado depois que a fila de mensagens aplicar a mudança.
        BeginInvoke(AtualizarResumo);
    }

    /// <summary>
    /// Atua sobre TODAS as lojas da sugestão, não só sobre as que o filtro deixa visíveis
    /// na lista -- filtrar, clicar Desmarcar e confirmar tem que zerar a escolha de
    /// verdade. Antes desta correção, filtrar escondia o resto e Desmarcar deixava as
    /// lojas escondidas marcadas do jeito que estavam.
    /// </summary>
    private void MarcarTodas(bool marcar)
    {
        if (marcar) foreach (var loja in _lojas) _marcadas.Add(loja.LojaId);
        else _marcadas.Clear();

        for (var i = 0; i < _lista.Items.Count; i++) _lista.SetItemChecked(i, marcar);
        AtualizarResumo();
    }

    private void AtualizarResumo()
    {
        _resumo.Text = $"{_marcadas.Count} de {_lojas.Count} loja(s) selecionada(s).";
        _ok.Enabled = _marcadas.Count > 0;
    }
}
