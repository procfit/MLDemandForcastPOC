using System.Globalization;

namespace CosmosPro.ML.DemandForCast.Extractor;

internal sealed record AlvosDaOperacao(
    IReadOnlyList<Control> Travar, Button Cancelar, ProgressBar Progresso, Label Status);

/// <summary>
/// Uma operação longa = um <c>using</c>. Enquanto ele vive, os inputs estão travados,
/// o Cancelar responde, a barra anda e o rodapé mostra o tempo decorrido. No
/// <c>Dispose</c> tudo volta, inclusive se a operação morreu no meio.
/// <para>
/// Existe porque as quatro coisas que faziam o extrator parecer travado eram
/// exatamente estas quatro, e cada rota do form as resolvia por conta própria — ou
/// não resolvia: o catálogo não tinha token de cancelamento nenhum.
/// </para>
/// </summary>
internal sealed class OperacaoUi : IDisposable
{
    private readonly AlvosDaOperacao _alvos;
    private readonly string _titulo;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Forms.Timer _cronometro = new() { Interval = 1000 };
    private readonly System.Diagnostics.Stopwatch _decorrido = System.Diagnostics.Stopwatch.StartNew();
    private readonly bool[] _estadoAnterior;
    private readonly ProgressBarStyle _estiloAnterior;
    private string? _detalhe;

    private OperacaoUi(AlvosDaOperacao alvos, string titulo, int? totalDeEtapas)
    {
        _alvos = alvos;
        _titulo = titulo;
        _estadoAnterior = [.. alvos.Travar.Select(c => c.Enabled)];
        _estiloAnterior = alvos.Progresso.Style;

        foreach (var controle in alvos.Travar) controle.Enabled = false;
        alvos.Cancelar.Enabled = true;

        // Marquee quando não há total conhecido: barra parada em zero durante
        // trabalho real afirma que nada está acontecendo.
        alvos.Progresso.Style = totalDeEtapas is null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (totalDeEtapas is { } total)
        {
            alvos.Progresso.Maximum = total;
            alvos.Progresso.Value = 0;
        }

        _cronometro.Tick += (_, _) => AtualizarStatus();
        _cronometro.Start();
        AtualizarStatus();
    }

    public static OperacaoUi Iniciar(AlvosDaOperacao alvos, string titulo, int? totalDeEtapas) =>
        new(alvos, titulo, totalDeEtapas);

    public CancellationToken Token => _cts.Token;

    public void Cancelar() => _cts.Cancel();

    public void Reportar(string detalhe, int? etapaAtual)
    {
        _detalhe = detalhe;
        if (etapaAtual is { } etapa && _alvos.Progresso.Style == ProgressBarStyle.Continuous)
        {
            _alvos.Progresso.Value = Math.Clamp(etapa, _alvos.Progresso.Minimum, _alvos.Progresso.Maximum);
        }
        AtualizarStatus();
    }

    public void Concluir(string texto)
    {
        _cronometro.Stop();
        _alvos.Status.Text = texto;
    }

    public string Decorrido => Duracao(_decorrido.Elapsed);

    internal static string TextoDeStatus(string titulo, TimeSpan decorrido, string? detalhe) =>
        detalhe is null
            ? $"{titulo}… {Duracao(decorrido)}"
            : $"{titulo}… {Duracao(decorrido)} — {detalhe}";

    private static string Duracao(TimeSpan decorrido) =>
        decorrido.TotalSeconds < 60
            ? $"{((int)decorrido.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s"
            : $"{((int)decorrido.TotalMinutes).ToString(CultureInfo.InvariantCulture)}min"
              + decorrido.Seconds.ToString("00", CultureInfo.InvariantCulture);

    private void AtualizarStatus() => _alvos.Status.Text = TextoDeStatus(_titulo, _decorrido.Elapsed, _detalhe);

    public void Dispose()
    {
        _cronometro.Stop();
        _cronometro.Dispose();

        for (var i = 0; i < _alvos.Travar.Count; i++) _alvos.Travar[i].Enabled = _estadoAnterior[i];
        _alvos.Cancelar.Enabled = false;
        _alvos.Progresso.Style = _estiloAnterior;

        _cts.Dispose();
    }
}
