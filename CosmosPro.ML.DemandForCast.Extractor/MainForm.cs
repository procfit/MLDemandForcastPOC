using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor;

internal sealed class MainForm : Form
{
    private readonly TextBox _servidor = new() { Width = 260 };
    private readonly NumericUpDown _porta = new() { Width = 80, Minimum = 1, Maximum = 65535, Value = 1433 };
    private readonly TextBox _banco = new() { Width = 260 };
    private readonly RadioButton _authWindows = new() { Text = "Windows", AutoSize = true };
    private readonly RadioButton _authSql = new() { Text = "SQL Server", AutoSize = true, Checked = true };
    private readonly TextBox _usuario = new() { Width = 160 };
    private readonly TextBox _senha = new() { Width = 160, UseSystemPasswordChar = true };
    private readonly Button _testar = new() { Text = "Testar conexão", Width = 130 };

    private readonly Button _carregarSugestoes = new() { Text = "Carregar sugestões", Width = 140 };
    private readonly NumericUpDown _meses = new() { Width = 60, Minimum = 1, Maximum = 60, Value = 12 };
    private readonly TextBox _filtro = new() { Width = 200, PlaceholderText = "filtrar por id ou descrição" };
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
    private readonly Button _copiarLog = new() { Text = "Copiar log", Width = 100 };
    private readonly Label _status = new() { AutoSize = true, Text = "Pronto." };
    private readonly TextBox _painelDeLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 620, Height = 160 };

    private readonly GroupBox _conexaoBox = new() { Text = "Conexão", Location = new Point(12, 12), Size = new Size(636, 150) };
    private readonly GroupBox _sugestaoBox = new() { Text = "Sugestão de compra", Location = new Point(12, 172), Size = new Size(636, 230) };
    private readonly GroupBox _saidaBox = new() { Text = "Saída", Location = new Point(12, 412), Size = new Size(636, 60) };

    private readonly AppConfig _config = AppConfig.Load();
    private readonly ExtratorLog _log;
    private readonly CatalogoService _catalogoService;
    private OperacaoUi? _operacao;

    private IReadOnlyList<SugestaoCatalogoCabecalho> _catalogo = [];
    private ExtractionWindow? _janela;

    // Contagem por seleção: fetch próprio, fora de ExecutarAsync (ver ContarSelecaoAsync).
    // Um novo CTS por chamada -- trocar de linha rápido cancela a contagem anterior em
    // vez de enfileirar as duas.
    private CancellationTokenSource? _contagemCts;

    public MainForm()
    {
        Text = "Extrator PBS → Stage";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 760);

        // ExtratorLog.Escrever roda este callback direto na thread de quem chamou --
        // inclusive a thread do pool do Task.Run de ExecutarAsync, quando é Retentativa
        // quem está logando um retry. ExtratorLog não pode fazer o marshaling (tem que
        // ficar livre de WinForms), então tem que ser aqui.
        _log = new ExtratorLog(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            tela: linha => MarshalParaPainelDeLog(linha));
        _catalogoService = new CatalogoService(_config, _log);

        BuildLayout();
        ApplyConfig();

        _testar.Click += async (_, _) => await TestarConexaoAsync();
        _carregarSugestoes.Click += async (_, _) => await CarregarSugestoesAsync();
        _sugestoes.SelectionChanged += (_, _) =>
        {
            AtualizarJanela();
            ContarSelecao();
        };
        _escolherPasta.Click += (_, _) => EscolherPasta();
        _extrair.Click += async (_, _) => await ExtrairAsync();
        _cancelar.Click += (_, _) => _operacao?.Cancelar();
        _authWindows.CheckedChanged += (_, _) => AtualizarCamposAuth();
        _filtro.TextChanged += (_, _) => AplicarFiltro();
        _copiarLog.Click += (_, _) => CopiarLog();
        _meses.Value = _config.MesesRetroativos is >= 1 and <= 60 ? _config.MesesRetroativos : 12;
    }

    private void BuildLayout()
    {
        AddRow(_conexaoBox, "Servidor:", _servidor, 0);
        AddRow(_conexaoBox, "Porta:", _porta, 1);
        AddRow(_conexaoBox, "Banco:", _banco, 2);

        var autenticacao = new FlowLayoutPanel { Location = new Point(100, 100), Size = new Size(260, 24), AutoSize = true };
        autenticacao.Controls.AddRange([_authSql, _authWindows]);
        _conexaoBox.Controls.Add(new Label { Text = "Autenticação:", Location = new Point(12, 103), AutoSize = true });
        _conexaoBox.Controls.Add(autenticacao);

        var credenciais = new FlowLayoutPanel { Location = new Point(100, 124), Size = new Size(520, 26), AutoSize = true };
        credenciais.Controls.AddRange([
            new Label { Text = "Usuário:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _usuario,
            new Label { Text = "Senha:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) }, _senha,
            _testar,
        ]);
        _conexaoBox.Controls.Add(credenciais);

        _carregarSugestoes.Location = new Point(12, 24);
        _sugestaoBox.Controls.Add(_carregarSugestoes);
        _sugestaoBox.Controls.Add(new Label { Text = "Meses:", Location = new Point(162, 27), AutoSize = true });
        _meses.Location = new Point(210, 24);
        _sugestaoBox.Controls.Add(_meses);
        _filtro.Location = new Point(282, 24);
        _sugestaoBox.Controls.Add(_filtro);
        _sugestoes.Location = new Point(12, 60);
        _sugestaoBox.Controls.Add(_sugestoes);
        _janelaInfo.Location = new Point(12, 196);
        _sugestaoBox.Controls.Add(_janelaInfo);

        _saidaBox.Controls.Add(new Label { Text = "Pasta:", Location = new Point(12, 26), AutoSize = true });
        _pastaSaida.Location = new Point(100, 23);
        _escolherPasta.Location = new Point(470, 22);
        _saidaBox.Controls.Add(_pastaSaida);
        _saidaBox.Controls.Add(_escolherPasta);

        _extrair.Location = new Point(12, 486);
        _cancelar.Location = new Point(140, 486);
        _progresso.Location = new Point(12, 528);
        _copiarLog.Location = new Point(540, 526);
        _status.Location = new Point(12, 556);
        _painelDeLog.Location = new Point(12, 580);

        Controls.AddRange([_conexaoBox, _sugestaoBox, _saidaBox, _extrair, _cancelar, _progresso, _copiarLog, _status, _painelDeLog]);
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

    // Mesma forma de OperacaoUi.Marshal: quem chama pode estar na thread do pool (o
    // callback de log de Retentativa, dentro do Task.Run de ExecutarAsync), e uma
    // linha de log perdida não pode derrubar a operação que a gerou.
    private void MarshalParaPainelDeLog(string linha)
    {
        try
        {
            if (_painelDeLog.InvokeRequired) _painelDeLog.BeginInvoke(() => _painelDeLog.AppendText(linha + Environment.NewLine));
            else _painelDeLog.AppendText(linha + Environment.NewLine);
        }
        catch (ObjectDisposedException)
        {
            // Form fechado no meio da operação: sem painel para receber a linha.
        }
        catch (InvalidOperationException)
        {
            // Handle ainda não criado (form fechado antes de aparecer, ou ainda não
            // mostrado): mesmo motivo do caso acima.
        }
    }

    /// <summary>
    /// Uma operação longa, com tudo o que ela deve ao operador: inputs travados,
    /// Cancelar ativo, relógio andando e o desfecho no log — inclusive quando falha.
    /// </summary>
    private async Task ExecutarAsync<T>(
        string titulo, int? totalDeEtapas, Func<CancellationToken, Result<T>> operacao, Action<T> aoConcluir)
    {
        var alvos = new AlvosDaOperacao(
            [_conexaoBox, _sugestaoBox, _saidaBox, _extrair, _testar, _carregarSugestoes],
            _cancelar, _progresso, _status);

        try
        {
            // Bloco explicito, não using-declaração: uma using-declaração só descartaria
            // a OperacaoUi no fim do método, depois deste finally -- e o Dispose() dela
            // restaura _extrair.Enabled (e os demais alvos) ao valor de ANTES da operação,
            // desfazendo o que AtualizarJanela() está prestes a recalcular a partir da
            // seleção atual. Fechando o bloco aqui, o Dispose roda primeiro: restaura o
            // estado genérico, e só depois AtualizarJanela decide o estado de verdade.
            using (var escopo = OperacaoUi.Iniciar(alvos, titulo, totalDeEtapas))
            {
                _operacao = escopo;
                _log.Escrever($"{titulo}...");

                try
                {
                    var resultado = await Task.Run(() => operacao(escopo.Token), escopo.Token);

                    // A operação pode devolver Result.Ok mesmo depois de cancelada -- por
                    // exemplo, um passo de leitura que já tinha o dado em mãos quando o
                    // token virou. Sem este checkpoint o rodapé diria "Concluído" para uma
                    // extração que o operador cancelou, com o catch abaixo nunca disparando.
                    escopo.Token.ThrowIfCancellationRequested();

                    if (resultado.IsSuccess)
                    {
                        aoConcluir(resultado.Value);
                        escopo.Concluir($"Concluído em {escopo.Decorrido}.");
                    }
                    else
                    {
                        var erro = resultado.ErroOuFallback();
                        escopo.Concluir("Falhou.");
                        _log.Escrever($"ERRO: {erro.Message}");
                        foreach (var (chave, valor) in erro.Metadata)
                        {
                            _log.EscreverSoNoArquivo($"  {chave}: {valor}");
                        }
                        MessageBox.Show(this, erro.Message + Environment.NewLine + Environment.NewLine
                            + $"Detalhe completo em {_log.CaminhoDeHoje}", "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch (OperationCanceledException)
                {
                    escopo.Concluir("Cancelado.");
                    _log.Escrever("Cancelado pelo usuário.");
                }
            }
        }
        finally
        {
            _operacao = null;
            AtualizarJanela();
        }
    }

    private Task TestarConexaoAsync() =>
        ExecutarAsync("Testando conexão", null,
            ct => _catalogoService.Lojas(BuildConnectionString(), ct),
            lojas =>
            {
                _log.Escrever($"Conexão OK. {lojas.Count} lojas ativas encontradas.");
                _config.Save();
            });

    private Task CarregarSugestoesAsync()
    {
        var meses = (int)_meses.Value;
        var dataInicio = DateOnly.FromDateTime(DateTime.Today).AddMonths(-meses);

        return ExecutarAsync($"Carregando sugestões dos últimos {meses} meses", null,
            ct => _catalogoService.Carregar(BuildConnectionString(), dataInicio, ct),
            catalogo =>
            {
                _catalogo = catalogo;
                _config.MesesRetroativos = meses;
                _config.Save();
                _log.Escrever($"{catalogo.Count:N0} sugestões carregadas.");
                AplicarFiltro();
            });
    }

    /// <summary>
    /// Contagem é conforto do operador, não pré-condição: não passa por ExecutarAsync,
    /// não trava input nenhum, não usa OperacaoUi e nunca abre MessageBox. Tem o próprio
    /// CancellationTokenSource, cancelado e substituído a cada seleção nova -- trocar de
    /// linha rápido abandona a contagem anterior em vez de enfileirar as duas.
    /// </summary>
    private async Task ContarSelecaoAsync(long sugestaoId, string textoDaJanela)
    {
        _contagemCts?.Cancel();
        var cts = new CancellationTokenSource();
        _contagemCts = cts;
        var token = cts.Token;

        try
        {
            var connectionString = BuildConnectionString();
            var resultado = await Task.Run(() => _catalogoService.Contar(connectionString, sugestaoId, token), token);

            // await sobre Task.Run recupera aqui o SynchronizationContext da UI (o
            // WindowsFormsSynchronizationContext que Application.Run instala) porque
            // este método nunca usa ConfigureAwait(false) -- a continuação volta para a
            // thread da UI sozinha, então escrever em _janelaInfo direto é seguro, sem
            // o Marshal que OperacaoUi.Reportar precisa para o callback do Progress<T>
            // (que nasce sem SynchronizationContext nenhum -- ver ExtrairAsync).
            if (token.IsCancellationRequested) return;

            if (resultado.IsSuccess)
            {
                var contagem = resultado.Value;
                _janelaInfo.Text = $"{contagem.QtdLinhas:N0} itens · {contagem.QtdLojas:N0} loja(s) · {textoDaJanela}";
            }
            else
            {
                var erro = resultado.ErroOuFallback();
                _janelaInfo.Text = $"Não foi possível contar os itens da sugestão {sugestaoId}: {erro.Message}";
                _log.Escrever($"ERRO ao contar itens da sugestão {sugestaoId}: {erro.Message}");
                foreach (var (chave, valor) in erro.Metadata)
                {
                    _log.EscreverSoNoArquivo($"  {chave}: {valor}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Seleção trocou antes da contagem terminar: abandono normal, não falha --
            // sem log, sem MessageBox, sem tocar em _janelaInfo (quem escreveu por
            // último foi a seleção atual, e é isso que deve continuar na tela).
        }
        finally
        {
            // Descarta o CTS desta chamada -- não o de _contagemCts, que já pode
            // apontar para uma seleção mais nova. Cada chamada é dona só do seu
            // próprio cts, e só o descarta depois do try/catch acima já ter
            // terminado de usá-lo -- descartar por fora, na hora da troca, arriscaria
            // derrubar um Task.Run que ainda estivesse em voo com este token.
            cts.Dispose();
        }
    }

    private void AplicarFiltro()
    {
        var visiveis = CatalogoService.Filtrar(_catalogo, _filtro.Text);
        _sugestoes.DataSource = visiveis
            .Select(c => new SugestaoLinha(
                c.SugestaoId, c.Descricao ?? "(sem descrição)", c.DataHora,
                MetodoTexto(c.TipoCalculo), c.DiasCoberturaMax))
            .ToList();
        ConfigurarColunas();

        if (_catalogo.Count == 0)
        {
            _janela = null;
            _janelaInfo.Text = "Nenhuma sugestão encontrada no período.";
            _extrair.Enabled = false;
        }
        else if (visiveis.Count == 0)
        {
            _janelaInfo.Text = $"Nenhuma das {_catalogo.Count:N0} sugestões carregadas casa com o filtro.";
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
        Renomear(nameof(SugestaoLinha.Cobertura), "Cobert.");
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
            // Só limpa quando há linhas navegáveis mas nenhuma selecionada. Com grid
            // vazio (0 linhas), AplicarFiltro já escreveu a única explicação que o
            // operador vê -- período sem sugestão, ou filtro sem match -- e apagar
            // aqui deixaria a tela com grade vazia e nenhum motivo.
            if (_sugestoes.Rows.Count > 0) _janelaInfo.Text = string.Empty;
            _extrair.Enabled = false;
            return;
        }

        var catalogo = _catalogo.FirstOrDefault(c => c.SugestaoId == selecionada.SugestaoId);
        if (catalogo is null)
        {
            // DataBoundItem ficou apontando para uma seleção que não existe mais no
            // _catalogo atual (ex.: grid recarregado entre o clique e este handler).
            _janela = null;
            if (_sugestoes.Rows.Count > 0) _janelaInfo.Text = string.Empty;
            _extrair.Enabled = false;
            return;
        }

        _janela = ExtractionWindow.Derive(
            DateOnly.FromDateTime(catalogo.DataHora), catalogo.DiasCoberturaMax, DateOnly.FromDateTime(DateTime.Today));

        if (_janela.Viavel)
        {
            // Não escreve a linha de informação: quem a escreve é ContarSelecao, no
            // fluxo da seleção. Este método também roda no finally do ExecutarAsync,
            // e escrever aqui apagaria as contagens já na tela toda vez que uma
            // operação terminasse.
            _extrair.Enabled = true;
        }
        else
        {
            _janelaInfo.Text = _janela.MotivoInviabilidade;
            _extrair.Enabled = false;
        }
    }

    private static string TextoDaJanela(ExtractionWindow janela) =>
        $"janela de dados {janela.Inicio:dd/MM/yyyy} a {janela.Fim:dd/MM/yyyy}";

    /// <summary>
    /// Contar é reação a trocar de seleção, e só a isso — por isso mora aqui e não
    /// dentro de <see cref="AtualizarJanela"/>. Um clique em "Carregar sugestões"
    /// chama AtualizarJanela duas vezes: uma pelo SelectionChanged que a ligação do
    /// DataSource dispara, outra pelo finally do ExecutarAsync. Com o disparo dentro
    /// dela, a segunda cancelava a contagem que a primeira tinha começado, e o caminho
    /// feliz custava duas consultas ao ERP e uma OperationCanceledException por clique.
    /// </summary>
    private void ContarSelecao()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada) return;
        if (_janela is not { Viavel: true } janela) return;

        var textoDaJanela = TextoDaJanela(janela);
        _janelaInfo.Text = textoDaJanela;
        _ = ContarSelecaoAsync(selecionada.SugestaoId, textoDaJanela);
    }

    private void EscolherPasta()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _pastaSaida.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _pastaSaida.Text = dialog.SelectedPath;
    }

    private Task ExtrairAsync()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada || _janela is not { Viavel: true } janela)
        {
            MessageBox.Show(this, "Selecione uma sugestão com janela viável.", "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
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

        // Progress<T> captura o SynchronizationContext no momento em que é construído.
        // Precisa nascer aqui, na thread da UI, antes do Task.Run que ExecutarAsync faz
        // internamente -- construído dentro do delegate (thread do pool), o callback
        // chegaria sem contexto nenhum. OperacaoUi.Reportar faz marshaling defensivo
        // para esse caso, mas depender disso seria apostar ao contrário do que este
        // ponto deveria garantir.
        var progresso = new Progress<ExtractionProgress>(p =>
            _operacao?.Reportar($"[{p.FileIndex}/{p.FileCount}] {p.FileName} — {p.RowsWritten:N0} linhas", p.FileIndex));

        return ExecutarAsync("Extraindo", StageContract.WriteOrder.Length,
            ct => new ExtractionService().Run(request, progresso, ct),
            resultado =>
            {
                _log.Escrever($"ZIP gerado: {resultado.ZipPath} ({resultado.ZipBytes / 1024d / 1024d:N1} MB)");
                foreach (var (file, count) in resultado.RowsByFile) _log.Escrever($"  {file}: {count:N0} linhas");
                foreach (var warning in resultado.Warnings) _log.Escrever($"  AVISO: {warning}");
            });
    }

    private void CopiarLog()
    {
        if (_painelDeLog.TextLength > 0) Clipboard.SetText(_painelDeLog.Text);
        _log.Escrever("Log copiado para a área de transferência.");
    }

    private sealed record SugestaoLinha(long SugestaoId, string Descricao, DateTime DataHora, string Metodo, int Cobertura);
}
