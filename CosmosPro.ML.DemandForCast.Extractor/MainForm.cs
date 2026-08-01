using System.Globalization;

namespace CosmosPro.ML.DemandForCast.Extractor;

internal sealed class MainForm : Form
{
    // Sugestões mais antigas que isto não aparecem no catálogo — evita carregar
    // anos de histórico que o comprador não vai reconhecer.
    private const int MesesRetroativosCatalogo = 12;

    private readonly TextBox _servidor = new() { Width = 260 };
    private readonly NumericUpDown _porta = new() { Width = 80, Minimum = 1, Maximum = 65535, Value = 1433 };
    private readonly TextBox _banco = new() { Width = 260 };
    private readonly RadioButton _authWindows = new() { Text = "Windows", AutoSize = true };
    private readonly RadioButton _authSql = new() { Text = "SQL Server", AutoSize = true, Checked = true };
    private readonly TextBox _usuario = new() { Width = 160 };
    private readonly TextBox _senha = new() { Width = 160, UseSystemPasswordChar = true };
    private readonly Button _testar = new() { Text = "Testar conexão", Width = 130 };

    private readonly Button _carregarSugestoes = new() { Text = "Carregar sugestões", Width = 140 };
    private readonly DataGridView _sugestoes = new()
    {
        Width = 612,
        Height = 130,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Label _janelaInfo = new() { Width = 612, Height = 30, AutoSize = false };

    private readonly TextBox _pastaSaida = new() { Width = 360 };
    private readonly Button _escolherPasta = new() { Text = "...", Width = 40 };

    private readonly Button _extrair = new() { Text = "Extrair", Width = 120, Height = 32, Enabled = false };
    private readonly Button _cancelar = new() { Text = "Cancelar", Width = 120, Height = 32, Enabled = false };
    private readonly ProgressBar _progresso = new() { Width = 520, Height = 20, Style = ProgressBarStyle.Continuous, Maximum = StageContract.WriteOrder.Length };
    private readonly Label _status = new() { AutoSize = true, Text = "Pronto." };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 620, Height = 160 };

    private readonly AppConfig _config = AppConfig.Load();
    private CancellationTokenSource? _cts;

    private IReadOnlyList<SugestaoCatalogo> _catalogo = [];
    private ExtractionWindow? _janela;

    public MainForm()
    {
        Text = "Extrator PBS → Stage";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 720);

        BuildLayout();
        ApplyConfig();

        _testar.Click += async (_, _) => await TestarConexaoAsync();
        _carregarSugestoes.Click += async (_, _) => await CarregarSugestoesAsync();
        _sugestoes.SelectionChanged += (_, _) => AtualizarJanela();
        _escolherPasta.Click += (_, _) => EscolherPasta();
        _extrair.Click += async (_, _) => await ExtrairAsync();
        _cancelar.Click += (_, _) => _cts?.Cancel();
        _authWindows.CheckedChanged += (_, _) => AtualizarCamposAuth();
    }

    private void BuildLayout()
    {
        var conexao = new GroupBox { Text = "Conexão", Location = new Point(12, 12), Size = new Size(636, 150) };
        AddRow(conexao, "Servidor:", _servidor, 0);
        AddRow(conexao, "Porta:", _porta, 1);
        AddRow(conexao, "Banco:", _banco, 2);

        var autenticacao = new FlowLayoutPanel { Location = new Point(100, 100), Size = new Size(260, 24), AutoSize = true };
        autenticacao.Controls.AddRange([_authSql, _authWindows]);
        conexao.Controls.Add(new Label { Text = "Autenticação:", Location = new Point(12, 103), AutoSize = true });
        conexao.Controls.Add(autenticacao);

        var credenciais = new FlowLayoutPanel { Location = new Point(100, 124), Size = new Size(520, 26), AutoSize = true };
        credenciais.Controls.AddRange([
            new Label { Text = "Usuário:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _usuario,
            new Label { Text = "Senha:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) }, _senha,
            _testar,
        ]);
        conexao.Controls.Add(credenciais);

        var sugestao = new GroupBox { Text = "Sugestão de compra", Location = new Point(12, 172), Size = new Size(636, 230) };
        _carregarSugestoes.Location = new Point(12, 24);
        sugestao.Controls.Add(_carregarSugestoes);
        _sugestoes.Location = new Point(12, 60);
        sugestao.Controls.Add(_sugestoes);
        _janelaInfo.Location = new Point(12, 196);
        sugestao.Controls.Add(_janelaInfo);

        var saida = new GroupBox { Text = "Saída", Location = new Point(12, 412), Size = new Size(636, 60) };
        saida.Controls.Add(new Label { Text = "Pasta:", Location = new Point(12, 26), AutoSize = true });
        _pastaSaida.Location = new Point(100, 23);
        _escolherPasta.Location = new Point(470, 22);
        saida.Controls.Add(_pastaSaida);
        saida.Controls.Add(_escolherPasta);

        _extrair.Location = new Point(12, 486);
        _cancelar.Location = new Point(140, 486);
        _progresso.Location = new Point(12, 528);
        _status.Location = new Point(12, 556);
        _log.Location = new Point(12, 580);

        Controls.AddRange([conexao, sugestao, saida, _extrair, _cancelar, _progresso, _status, _log]);
    }

    private static void AddRow(Control parent, string label, Control field, int row)
    {
        var y = 24 + (row * 26);
        parent.Controls.Add(new Label { Text = label, Location = new Point(12, y + 3), AutoSize = true });
        field.Location = new Point(100, y);
        parent.Controls.Add(field);
    }

    private void ApplyConfig()
    {
        _servidor.Text = _config.Servidor;
        _porta.Value = _config.Porta is >= 1 and <= 65535 ? _config.Porta : 1433;
        _banco.Text = _config.Banco;
        _authWindows.Checked = _config.WindowsAuth;
        _authSql.Checked = !_config.WindowsAuth;
        _usuario.Text = _config.Usuario;
        _pastaSaida.Text = string.IsNullOrWhiteSpace(_config.PastaSaida)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : _config.PastaSaida;
        AtualizarCamposAuth();
    }

    private void AtualizarCamposAuth()
    {
        _usuario.Enabled = !_authWindows.Checked;
        _senha.Enabled = !_authWindows.Checked;
    }

    private AppConfig CaptureConfig()
    {
        _config.Servidor = _servidor.Text.Trim();
        _config.Porta = (int)_porta.Value;
        _config.Banco = _banco.Text.Trim();
        _config.WindowsAuth = _authWindows.Checked;
        _config.Usuario = _usuario.Text.Trim();
        _config.PastaSaida = _pastaSaida.Text.Trim();
        return _config;
    }

    private string BuildConnectionString() => ConnectionStringFactory.Build(CaptureConfig(), _senha.Text);

    private async Task TestarConexaoAsync()
    {
        await RunGuardedAsync("Testando conexão...", async () =>
        {
            var connectionString = BuildConnectionString();
            var lojas = await Task.Run(() => ExtractionService.LoadLojas(connectionString, CancellationToken.None));
            Log($"Conexão OK. {lojas.Count} lojas ativas encontradas.");
            _config.Save();
        });
    }

    private async Task CarregarSugestoesAsync()
    {
        await RunGuardedAsync("Carregando sugestões...", async () =>
        {
            var connectionString = BuildConnectionString();
            var dataInicio = DateOnly.FromDateTime(DateTime.Today).AddMonths(-MesesRetroativosCatalogo);
            var catalogo = await Task.Run(() => ExtractionService.LoadCatalogoSugestoes(connectionString, dataInicio, CancellationToken.None));

            _catalogo = catalogo;
            PopularGrid(catalogo);
            Log($"{catalogo.Count} sugestões carregadas.");
        });
    }

    private void PopularGrid(IReadOnlyList<SugestaoCatalogo> catalogo)
    {
        _sugestoes.DataSource = catalogo
            .Select(c => new SugestaoLinha(c.SugestaoId, c.Descricao ?? "(sem descrição)", c.DataHora, MetodoTexto(c.TipoCalculo), c.QtdLinhas, c.QtdLojas))
            .ToList();
        ConfigurarColunas();

        if (catalogo.Count == 0)
        {
            _janela = null;
            _janelaInfo.Text = "Nenhuma sugestão encontrada no período.";
            _extrair.Enabled = false;
        }
    }

    private void ConfigurarColunas()
    {
        void Renomear(string coluna, string titulo)
        {
            if (_sugestoes.Columns[coluna] is { } c) c.HeaderText = titulo;
        }

        Renomear(nameof(SugestaoLinha.SugestaoId), "Sugestão");
        Renomear(nameof(SugestaoLinha.Descricao), "Descrição");
        Renomear(nameof(SugestaoLinha.DataHora), "Data");
        Renomear(nameof(SugestaoLinha.Metodo), "Método");
        Renomear(nameof(SugestaoLinha.QtdLinhas), "Linhas");
        Renomear(nameof(SugestaoLinha.QtdLojas), "Lojas");
    }

    private static string MetodoTexto(byte tipoCalculo) => tipoCalculo switch
    {
        1 => "Emax e Eseg",
        2 => "Dias de Reposição",
        _ => $"Tipo {tipoCalculo}",
    };

    private void AtualizarJanela()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada)
        {
            _janela = null;
            _janelaInfo.Text = string.Empty;
            _extrair.Enabled = false;
            return;
        }

        var catalogo = _catalogo.FirstOrDefault(c => c.SugestaoId == selecionada.SugestaoId);
        if (catalogo is null)
        {
            // DataBoundItem ficou apontando para uma seleção que não existe mais no
            // _catalogo atual (ex.: grid recarregado entre o clique e este handler).
            _janela = null;
            _janelaInfo.Text = string.Empty;
            _extrair.Enabled = false;
            return;
        }

        _janela = ExtractionWindow.Derive(
            DateOnly.FromDateTime(catalogo.DataHora), catalogo.DiasCoberturaMax, DateOnly.FromDateTime(DateTime.Today));

        if (_janela.Viavel)
        {
            _janelaInfo.Text = $"Janela de dados a extrair: {_janela.Inicio:dd/MM/yyyy} a {_janela.Fim:dd/MM/yyyy}.";
            _extrair.Enabled = true;
        }
        else
        {
            _janelaInfo.Text = _janela.MotivoInviabilidade;
            _extrair.Enabled = false;
        }
    }

    private void EscolherPasta()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _pastaSaida.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _pastaSaida.Text = dialog.SelectedPath;
    }

    private async Task ExtrairAsync()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada || _janela is not { Viavel: true } janela)
        {
            MessageBox.Show(this, "Selecione uma sugestão com janela viável.", "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var request = new ExtractionRequest
        {
            ConnectionString = BuildConnectionString(),
            SugestaoId = selecionada.SugestaoId,
            DataInicial = janela.Inicio,
            DataFinal = janela.Fim,
            OutputDirectory = _pastaSaida.Text.Trim(),
        };
        _config.Save();

        _cts = new CancellationTokenSource();
        _cancelar.Enabled = true;
        _progresso.Value = 0;

        var progress = new Progress<ExtractionProgress>(p =>
        {
            _progresso.Value = Math.Min(p.FileIndex, _progresso.Maximum);
            _status.Text = $"[{p.FileIndex}/{p.FileCount}] {p.FileName} — {p.RowsWritten:N0} linhas";
        });

        await RunGuardedAsync("Extraindo...", async () =>
        {
            var service = new ExtractionService();
            var token = _cts.Token;
            var result = await Task.Run(() => service.Run(request, progress, token), token);

            Log($"ZIP gerado: {result.ZipPath} ({result.ZipBytes / 1024d / 1024d:N1} MB)");
            foreach (var (file, count) in result.RowsByFile) Log($"  {file}: {count:N0} linhas");
            foreach (var warning in result.Warnings) Log($"  AVISO: {warning}");
            _progresso.Value = _progresso.Maximum;
        });

        _cancelar.Enabled = false;
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>Centraliza o tratamento de erro para a UI nunca quebrar por exceção de I/O ou SQL.</summary>
    private async Task RunGuardedAsync(string statusInicial, Func<Task> action)
    {
        _extrair.Enabled = false;
        _testar.Enabled = false;
        _carregarSugestoes.Enabled = false;
        _status.Text = statusInicial;
        Log(statusInicial);

        try
        {
            await action();
            _status.Text = "Concluído.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelado.";
            Log("Cancelado pelo usuário — o ZIP parcial foi descartado.");
        }
        catch (Exception ex)
        {
            _status.Text = "Falhou.";
            var mensagem = ex.Message + DicaLogonTrigger(ex);
            Log($"ERRO: {mensagem}");
            MessageBox.Show(this, mensagem, "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _testar.Enabled = true;
            _carregarSugestoes.Enabled = true;
            AtualizarJanela(); // reabilita "Extrair" conforme a seleção atual, não incondicionalmente
        }
    }

    /// <summary>
    /// Erro 17892 = logon trigger recusou a sessão. No PBS isso costuma ser
    /// filtro por APP_NAME(), e a mensagem crua do SQL Server não ajuda em nada.
    /// </summary>
    private static string DicaLogonTrigger(Exception ex) =>
        ex is Microsoft.Data.SqlClient.SqlException { Number: 17892 }
            ? Environment.NewLine + Environment.NewLine
              + "O servidor tem um logon trigger que recusou a conexão — normalmente por causa do "
              + "nome da aplicação. Ajuste 'ApplicationName' em extrator.config.json (vazio usa o "
              + "padrão do provider) e tente de novo."
            : string.Empty;

    private void Log(string message) =>
        _log.AppendText($"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {message}{Environment.NewLine}");

    private sealed record SugestaoLinha(long SugestaoId, string Descricao, DateTime DataHora, string Metodo, int QtdLinhas, int QtdLojas);
}
