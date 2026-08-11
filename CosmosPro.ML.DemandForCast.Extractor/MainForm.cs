using System.Text.RegularExpressions;
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
    // Largura menor que os 612 originais para abrir espaço ao semáforo à esquerda e ao
    // indicador de análise à direita, na mesma linha — o box da sugestão não tem altura
    // sobrando, e mover o resto do formulário significaria recalcular todas as posições
    // absolutas abaixo dele. Reduzida de 460 para 320 quando "Escolher lojas…" entrou na
    // mesma linha, entre este rótulo e o indicador de análise.
    private readonly Label _janelaInfo = new() { Width = 320, Height = 30, AutoSize = false };

    /// <summary>
    /// Verde ou vermelho ao lado do texto: o desfecho da análise legível de relance.
    /// <para>
    /// Um <c>Panel</c> colorido, e não um caractere com <c>ForeColor</c>, para não depender de
    /// fonte instalada nem de tamanho de glifo. Escondido enquanto a análise corre — quem
    /// sinaliza processo em curso é a barra em marquee, e um semáforo aceso durante a consulta
    /// afirmaria um desfecho que ainda não existe.
    /// </para>
    /// <para>
    /// A cor não carrega a informação sozinha: o texto ao lado continua dizendo o motivo por
    /// extenso, para quem não distingue as duas cores e para quem precisa saber <i>por quê</i>.
    /// </para>
    /// </summary>
    private readonly Panel _semaforo = new() { Width = 12, Height = 12, Visible = false };

    /// <summary>
    /// Indicador indeterminado da análise da sugestão selecionada (itens, cobertura, janela).
    /// <para>
    /// Existe porque trocar o texto do rótulo não é suficiente: quem olha de relance não
    /// distingue "estou consultando" de "terminei e a sugestão não serve" — as duas coisas são
    /// uma frase parada na tela. A barra em marquee só existe enquanto a consulta corre, então
    /// ausência dela significa desfecho, e a cor do rótulo diz qual.
    /// </para>
    /// </summary>
    private readonly ProgressBar _analisando = new()
    {
        Width = 124,
        Height = 14,
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 30,
        Visible = false,
    };

    private readonly Button _escolherLojas = new() { Text = "Escolher lojas…", Width = 130, Enabled = false };

    private readonly TextBox _pastaSaida = new() { Width = 360 };
    private readonly Button _escolherPasta = new() { Text = "...", Width = 40 };

    private readonly Button _extrair = new() { Text = "Extrair", Width = 120, Height = 32, Enabled = false };
    private readonly Button _cancelar = new() { Text = "Cancelar", Width = 120, Height = 32, Enabled = false };
    private readonly ProgressBar _progresso = new() { Width = 520, Height = 20, Style = ProgressBarStyle.Continuous, Maximum = StageContract.WriteOrder.Length };
    private readonly Button _copiarLog = new() { Text = "Copiar log", Width = 100 };
    private readonly Label _status = new() { AutoSize = true, Text = "Pronto." };

    /// <summary>
    /// Identidade do binário: versão e quando foi gerado. A UI Web mostra isto do extrator
    /// publicado (versão + checksum na página da sessão, versão + data em /admin/extrator), e
    /// sem o equivalente aqui não havia como o operador conferir se o .exe que ele abriu é o
    /// mesmo que a aplicação distribui.
    /// </summary>
    private readonly Label _versaoInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox _painelDeLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 620, Height = 160 };

    private readonly GroupBox _conexaoBox = new() { Text = "Conexão", Location = new Point(12, 12), Size = new Size(696, 150) };
    private readonly GroupBox _sugestaoBox = new() { Text = "Sugestão de compra", Location = new Point(12, 172), Size = new Size(696, 230) };
    private readonly GroupBox _saidaBox = new() { Text = "Saída", Location = new Point(12, 412), Size = new Size(696, 60) };

    private readonly AppConfig _config = AppConfig.Load();
    private readonly ExtratorLog _log;
    private readonly CatalogoService _catalogoService;
    private OperacaoUi? _operacao;

    private IReadOnlyList<SugestaoCatalogoCabecalho> _catalogo = [];
    private ExtractionWindow? _janela;

    // Amarra a última janela CALCULADA (viável ou não) ao id da sugestão a que ela
    // pertence. Existe porque ExecutarAsync chama AtualizarJanela depois de TODA
    // operação -- inclusive uma extração bem-sucedida da própria sugestão selecionada --
    // e ContarSelecao não reconta a mesma sugestão (guarda _sugestaoContada, ver lá).
    // Sem este cache, AtualizarJanela zerava a janela e o Extrair ficava morto até o
    // operador trocar de linha e voltar só para reacionar a contagem. Um slot só, não um
    // dicionário: trocar de sugestão sobrescreve a entrada, e é isso que impede a janela
    // de uma sugestão vazar para outra ao restaurar. Zerado explicitamente quando o
    // recount da MESMA sugestão falha (ver ContarSelecaoAsync) -- senão uma janela de um
    // sucesso anterior poderia reviver depois de uma falha mais recente. Zerado também
    // quando um novo catálogo é aceito (ver CarregarSugestoesAsync) -- senão a mesma
    // sugestão, selecionada de novo depois do recarregamento, restauraria uma
    // viabilidade calculada contra dados que o recarregamento pode ter substituído.
    private (long SugestaoId, ExtractionWindow Janela)? _janelaCache;

    private IReadOnlyList<LojaDaSugestao> _lojasDaSugestao = [];

    // null quer dizer "o operador ainda não abriu o diálogo e confirmou uma escolha" --
    // e essa distinção não pode se perder numa lista vazia (ver ExtrairAsync): antes desta
    // revisão as duas colapsavam no mesmo tipo, e null virava "todas as lojas" lá no fundo
    // de RecorteDeLojas.Aplicar. É exatamente o caminho que a garantia de confidencialidade
    // desta tela existe para fechar.
    private IReadOnlyList<int>? _lojasEscolhidas;

    // Geração própria do fetch de lojas -- separada de _contagemGeracao (ver
    // ContarSelecaoAsync, área protegida): são dois fetches independentes, cada um
    // com seu próprio "quem volta fora de época se cala".
    private long _lojasFetchGeracao;

    // Base do resumo amarrada ao id da sugestão em vez de zerada num ponto fixo:
    // ContarSelecao já zera _lojasDaSugestao/_lojasEscolhidas na troca de seleção,
    // mas é área protegida (não pode ganhar mais uma responsabilidade). Comparar o
    // id na hora de usar (AtualizarResumoDaSelecao) faz a troca de sugestão
    // recapturar a base sozinha, sem depender de mais um ponto de reset espalhado.
    private (long SugestaoId, string Texto)? _baseResumoLojas;

    // Contagem por seleção: fetch próprio, fora de ExecutarAsync (ver ContarSelecaoAsync).
    // Um novo CTS por chamada -- trocar de linha rápido cancela a contagem anterior em
    // vez de enfileirar as duas.
    /// <summary>Sugestão cuja contagem já está em voo ou na tela. Ver <see cref="ContarSelecao"/>.</summary>
    private long? _sugestaoContada;

    /// <summary>Ordem das contagens: só a mais nova pode escrever na tela.</summary>
    private long _contagemGeracao;

    public MainForm()
    {
        Text = "Extrator PBS → Stage";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // 720 e nao 660: o painel de credenciais (usuario + senha + botao Testar) mede ~570px
        // a partir de x=100, entao passava da borda e cortava o botao. Alargar o form e as
        // tres caixas mantem todas as posicoes verticais absolutas intactas.
        ClientSize = new Size(720, 760);

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

        var geradoEm = ZipManifest.GeradoEm();
        _versaoInfo.Text = geradoEm is { } quando
            ? $"Extrator {ZipManifest.VersaoAtual()} · gerado em {quando:dd/MM/yyyy HH:mm}"
            : $"Extrator {ZipManifest.VersaoAtual()} · data de geração desconhecida";

        _testar.Click += async (_, _) => await TestarConexaoAsync();
        _carregarSugestoes.Click += async (_, _) => await CarregarSugestoesAsync();
        _sugestoes.SelectionChanged += (_, _) =>
        {
            AtualizarJanela();
            ContarSelecao();
        };
        _escolherLojas.Click += async (_, _) => await EscolherLojasAsync();
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
        _semaforo.Location = new Point(12, 200);
        _sugestaoBox.Controls.Add(_semaforo);
        _janelaInfo.Location = new Point(32, 196);
        _sugestaoBox.Controls.Add(_janelaInfo);
        _escolherLojas.Location = new Point(362, 196);
        _sugestaoBox.Controls.Add(_escolherLojas);
        _analisando.Location = new Point(500, 200);
        _sugestaoBox.Controls.Add(_analisando);

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
        _versaoInfo.Location = new Point(12, 744);

        Controls.AddRange([_conexaoBox, _sugestaoBox, _saidaBox, _extrair, _cancelar, _progresso, _copiarLog, _status, _painelDeLog, _versaoInfo]);
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

                // O catálogo que acabou de chegar é a fronteira: a análise anterior (janela
                // cacheada, marca de "já contei esta sugestão") descreve dados que este
                // recarregamento pode ter apagado -- e a mesma sugestão pode acabar
                // selecionada de novo (ordenação estável, ela pode continuar sendo a mais
                // recente). Zerar aqui, ANTES do rebind que AplicarFiltro dispara, é o que
                // faz ContarSelecao tratar a próxima seleção como nova em vez de restaurar
                // uma viabilidade calculada contra o catálogo antigo.
                _janelaCache = null;
                _sugestaoContada = null;

                AplicarFiltro();
            });
    }

    /// <summary>
    /// Contagem **é pré-condição** desde que a cobertura passou a vir do <c>DIAS_ESTOQUE</c>
    /// dos itens: é ela que traz o número do qual a janela é derivada, então o Extrair só
    /// habilita quando ela volta viável. Antes era só conforto do operador — a janela saía do
    /// cabeçalho, que estava errado.
    /// <para>
    /// Segue fora de <see cref="ExecutarAsync"/>: não trava input, não usa OperacaoUi e nunca
    /// abre MessageBox. O que ela ganhou foi o direito de habilitar o botão.
    /// <para>
    /// Uma contagem em voo <b>não</b> é cancelada quando outra começa. Cancelar um
    /// <c>SqlCommand</c> em execução derruba a consulta com exceção, e para 10 ms de
    /// trabalho isso não compra nada — só produz uma OperationCanceledException por
    /// troca de seleção, que para o depurador de quem estiver desenvolvendo. O que
    /// importa é não escrever resposta velha na tela, e para isso basta o número da
    /// geração: quem volta fora de época se cala.
    /// </para>
    /// </summary>
    private async Task ContarSelecaoAsync(long sugestaoId, DateTime dataSugestao)
    {
        var geracao = ++_contagemGeracao;
        var connectionString = BuildConnectionString();

        var resultado = await Task.Run(() => _catalogoService.Contar(connectionString, sugestaoId, CancellationToken.None));

        // await sobre Task.Run recupera aqui o SynchronizationContext da UI (o
        // WindowsFormsSynchronizationContext que Application.Run instala) porque
        // este método nunca usa ConfigureAwait(false) -- a continuação volta para a
        // thread da UI sozinha, então escrever em _janelaInfo direto é seguro, sem
        // o Marshal que OperacaoUi.Reportar precisa para o callback do Progress<T>
        // (que nasce sem SynchronizationContext nenhum -- ver ExtrairAsync).
        if (geracao != _contagemGeracao) return;

        if (resultado.IsSuccess)
        {
            var contagem = resultado.Value;

            // A janela nasce aqui, e o botão com ela: a cobertura acabou de chegar.
            _janela = ExtractionWindow.Derive(
                DateOnly.FromDateTime(dataSugestao),
                contagem.DiasCoberturaMax,
                DateOnly.FromDateTime(DateTime.Today));

            // Cacheada por sugestaoId (ver o campo _janelaCache): é o que deixa
            // AtualizarJanela religar o Extrair depois de uma extração bem-sucedida sem
            // reconsultar o ERP.
            _janelaCache = (sugestaoId, _janela);

            var itens = $"{contagem.QtdLinhas:N0} itens · {contagem.QtdLojas:N0} loja(s)";

            if (_janela.Viavel)
            {
                MostrarDesfecho(
                    $"{itens} · {TextoDaJanela(_janela)} · cobertura {contagem.DiasCoberturaMax} dia(s)",
                    viavel: true);
                _extrair.Enabled = true;
            }
            else
            {
                // O motivo por extenso, e não um "inviável" seco: ele diz o que escolher em
                // vez desta, que é a única coisa acionável para quem está na tela.
                MostrarDesfecho($"{itens} · {_janela.MotivoInviabilidade}", viavel: false);
                _extrair.Enabled = false;
            }
        }
        else
        {
            var erro = resultado.ErroOuFallback();
            _janela = null;
            _extrair.Enabled = false;
            MostrarDesfecho(
                $"Não foi possível contar os itens da sugestão {sugestaoId}: {erro.Message}",
                viavel: false);
            _log.Escrever($"ERRO ao contar itens da sugestão {sugestaoId}: {erro.Message}");
            foreach (var (chave, valor) in erro.Metadata)
            {
                _log.EscreverSoNoArquivo($"  {chave}: {valor}");
            }

            // A contagem que falhou não fica marcada como feita: selecionar a mesma
            // linha de novo tem de poder tentar outra vez.
            _sugestaoContada = null;

            // Invalida só a entrada desta sugestão: um recount que falha não pode deixar
            // uma janela de um sucesso anterior disponível para AtualizarJanela reviver.
            if (_janelaCache?.SugestaoId == sugestaoId) _janelaCache = null;
        }
    }

    private void AplicarFiltro()
    {
        var visiveis = CatalogoService.Filtrar(_catalogo, _filtro.Text);
        _sugestoes.DataSource = visiveis
            .Select(c => new SugestaoLinha(
                c.SugestaoId, c.Descricao ?? "(sem descrição)", c.DataHora,
                MetodoTexto(c.TipoCalculo)))
            .ToList();
        ConfigurarColunas();

        if (_catalogo.Count == 0)
        {
            _janela = null;
            MostrarDesfecho("Nenhuma sugestão encontrada no período.", viavel: false);
            _extrair.Enabled = false;
        }
        else if (visiveis.Count == 0)
        {
            MostrarDesfecho($"Nenhuma das {_catalogo.Count:N0} sugestões carregadas casa com o filtro.", viavel: false);
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
            if (_sugestoes.Rows.Count > 0) LimparAnalise();
            _extrair.Enabled = false;
            _escolherLojas.Enabled = false;
            _lojasDaSugestao = [];
            _lojasEscolhidas = null;
            return;
        }

        var catalogo = _catalogo.FirstOrDefault(c => c.SugestaoId == selecionada.SugestaoId);
        if (catalogo is null)
        {
            // DataBoundItem ficou apontando para uma seleção que não existe mais no
            // _catalogo atual (ex.: grid recarregado entre o clique e este handler).
            _janela = null;
            if (_sugestoes.Rows.Count > 0) LimparAnalise();
            _extrair.Enabled = false;
            _escolherLojas.Enabled = false;
            _lojasDaSugestao = [];
            _lojasEscolhidas = null;
            return;
        }

        // A janela **não** é decidida aqui, e essa é a mudança original: a cobertura vem
        // do DIAS_ESTOQUE dos itens, que só a contagem conhece. Antes ela saía do
        // cabeçalho e este método podia decidir na hora — o campo do cabeçalho estava
        // errado (era do método 2 e vinha zerado em 83% das sugestões de eMax/eSeg), e a
        // consequência foi uma extração de 879 MB sem um dia de gabarito.
        //
        // O que mudou aqui: este método também roda depois de TODA operação (ver o
        // finally de ExecutarAsync), inclusive uma extração bem-sucedida da própria
        // sugestão que continua selecionada -- e ContarSelecao não reconta a mesma
        // sugestão (guarda _sugestaoContada). Sem restaurar, o Extrair ficaria morto até
        // o operador trocar de linha e voltar só para reacionar a contagem. Restaurar do
        // cache não é "decidir na hora": a contagem já rodou e ainda vale para esta
        // sugestão -- é dado velho, não um cálculo novo.
        if (_janelaCache is { } cache && cache.SugestaoId == selecionada.SugestaoId)
        {
            _janela = cache.Janela;
            _extrair.Enabled = cache.Janela.Viavel;
            return;
        }

        // Sem cache para ESTA sugestão (contagem ainda não voltou, ou o id não bate):
        // habilitar antes seria oferecer uma extração cuja viabilidade ninguém conferiu.
        _janela = null;
        _extrair.Enabled = false;
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
    /// <summary>
    /// Estado "analisando": barra indeterminada visível e rótulo em cor neutra. A barra é o que
    /// separa processo em curso de desfecho — texto sozinho não separa, porque uma frase parada
    /// na tela parece igual nos dois casos.
    /// </summary>
    private void MostrarAnalise(string texto)
    {
        _semaforo.Visible = false;
        _janelaInfo.ForeColor = SystemColors.GrayText;
        _janelaInfo.Text = texto;
        _analisando.Visible = true;
    }

    /// <summary>
    /// Estado terminal: barra escondida (o sinal de que acabou) e cor dizendo qual desfecho.
    /// Vermelho para recusa e falha, cor normal para viável — cor sozinha não carrega a
    /// informação, o texto continua explicando o motivo por extenso.
    /// </summary>
    private void MostrarDesfecho(string texto, bool viavel)
    {
        _analisando.Visible = false;
        _semaforo.BackColor = viavel ? Color.SeaGreen : Color.Firebrick;
        _semaforo.Visible = true;
        _janelaInfo.ForeColor = viavel ? SystemColors.ControlText : Color.Firebrick;
        _janelaInfo.Text = texto;
    }

    /// <summary>Sem seleção não há o que analisar nem desfecho a mostrar.</summary>
    private void LimparAnalise()
    {
        _analisando.Visible = false;
        _semaforo.Visible = false;
        _janelaInfo.ForeColor = SystemColors.ControlText;
        _janelaInfo.Text = string.Empty;
    }

    private void ContarSelecao()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada) return;

        // DataGridView levanta SelectionChanged várias vezes ao ligar o DataSource, e
        // todas trazem a mesma linha. Sem esta guarda, um único "Carregar sugestões"
        // dispara várias contagens idênticas contra o ERP do cliente.
        if (_sugestaoContada == selecionada.SugestaoId) return;
        _sugestaoContada = selecionada.SugestaoId;

        // Diz **qual** sugestão está sendo analisada: trocar de linha rápido deixa a resposta
        // anterior sendo descartada em silêncio (pela geração), e sem o id na tela o operador
        // não sabe a qual linha a frase se refere.
        MostrarAnalise($"Analisando a sugestão {selecionada.SugestaoId}: itens, cobertura e janela…");
        _ = ContarSelecaoAsync(selecionada.SugestaoId, selecionada.DataHora);

        _lojasDaSugestao = [];
        _lojasEscolhidas = null;
        _escolherLojas.Enabled = true;
    }

    /// <summary>
    /// As lojas só são buscadas quando o comprador pede para escolher: é uma ida ao
    /// banco por sugestão, e a maioria das seleções não termina em extração.
    /// </summary>
    private async Task EscolherLojasAsync()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada) return;

        var sugestaoId = selecionada.SugestaoId;

        if (_lojasDaSugestao.Count == 0)
        {
            var geracao = ++_lojasFetchGeracao;
            _escolherLojas.Enabled = false;

            var connectionString = BuildConnectionString();
            var lidas = await Task.Run(() => _catalogoService.LojasDaSugestao(connectionString, sugestaoId, CancellationToken.None));

            // Mesmo padrão de _contagemGeracao em ContarSelecaoAsync: quem volta fora de
            // época se cala. "Fora de época" é geração superada (um clique novo já está
            // em voo, e esse clique é quem deve religar o botão) OU seleção trocada (a
            // troca já religou o botão via ContarSelecao/AtualizarJanela; tocar nele de
            // novo aqui reabriria a corrida que esta checagem existe para fechar --
            // aplicar as lojas de A, ou abrir o diálogo com elas, sobre a sugestão B).
            var aindaAtual = geracao == _lojasFetchGeracao
                && _sugestoes.CurrentRow?.DataBoundItem is SugestaoLinha atual
                && atual.SugestaoId == sugestaoId;

            if (!aindaAtual) return;

            if (lidas.IsFailed)
            {
                var erro = lidas.ErroOuFallback();
                _log.Escrever($"ERRO ao listar as lojas da sugestão {sugestaoId}: {erro.Message}");
                _escolherLojas.Enabled = true;
                MessageBox.Show(this, erro.Message, "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _lojasDaSugestao = lidas.Value;
            _escolherLojas.Enabled = true;
        }

        // _lojasEscolhidas ainda pode ser null aqui (nenhuma escolha confirmada até agora) --
        // o diálogo só entende "nada marcado", então null e [] chegam a ele da mesma forma.
        if (SelecaoDeLojasDialog.Escolher(this, _lojasDaSugestao, _lojasEscolhidas ?? []) is not { } escolhidas) return;

        _lojasEscolhidas = escolhidas;
        _log.Escrever($"Lojas escolhidas ({escolhidas.Count} de {_lojasDaSugestao.Count}): "
            + string.Join(", ", _lojasDaSugestao.Where(l => escolhidas.Contains(l.LojaId)).Select(l => $"{l.LojaId} {l.Nome}")));
        AtualizarResumoDaSelecao(sugestaoId);
    }

    private void AtualizarResumoDaSelecao(long sugestaoId)
    {
        if (_lojasEscolhidas is not { Count: > 0 } escolhidas || _lojasDaSugestao.Count == 0) return;

        // Recompor sempre a partir da base, nunca do texto atual: prepender no texto
        // atual (versão anterior) empilha "5 de 10 loja(s) · 3 de 10 loja(s) · ..." a
        // cada reconfirmação. A base é amarrada ao id em vez de zerada por fora (ver
        // campo _baseResumoLojas): se o id não bate, a sugestão trocou e a base velha
        // não serve -- recaptura aqui mesmo, sem depender de outro método limpá-la.
        if (_baseResumoLojas is not { } baseAtual || baseAtual.SugestaoId != sugestaoId)
        {
            baseAtual = (sugestaoId, _janelaInfo.Text);
            _baseResumoLojas = baseAtual;
        }

        // A base já traz sua própria contagem de lojas (o total da sugestão, escrito por
        // ContarSelecaoAsync) -- sem remover daqui, "2 de 10 loja(s)" e "10 loja(s)" apareceriam
        // juntos na mesma linha, dizendo o mesmo número duas vezes com significados diferentes.
        // Regex e não índice fixo porque a forma da base muda entre viável, inviável e erro.
        var baseSemContagemDeLojas = Regex.Replace(baseAtual.Texto, @"\s*·\s*[\d.,]+\s+loja\(s\)", "");

        _janelaInfo.Text = $"{escolhidas.Count} de {_lojasDaSugestao.Count} loja(s) · {baseSemContagemDeLojas}";
    }

    private void EscolherPasta()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _pastaSaida.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _pastaSaida.Text = dialog.SelectedPath;
    }

    /// <summary>
    /// Pergunta antes de gravar qualquer coisa. Duas motivações na mesma caixa, por
    /// decisão do desenvolvedor (não duas caixas separadas): confirmar o clique em
    /// Extrair, e avisar quando o ZIP vai substituir um já existente -- hoje
    /// <c>File.Create</c> trunca o anterior sem aviso, e <c>extracao-pbs_{yyyyMMdd-HHmm}</c>
    /// colide entre duas extrações no mesmo minuto.
    /// <para>
    /// A contagem de lojas é a linha que importa: a rede só autoriza a exportação de
    /// algumas lojas, e é para isso que a tela existe. "Não" não deve deixar rastro --
    /// nenhum ZIP, nenhuma linha de log dizendo que algo aconteceu.
    /// </para>
    /// </summary>
    private bool ConfirmarExtracao(
        SugestaoLinha selecionada, ExtractionWindow janela, int lojasEscolhidasCount, string pastaSaida, string zipEsperado)
    {
        var mensagem =
            $"Sugestão {selecionada.SugestaoId} — {selecionada.Descricao} ({selecionada.DataHora:dd/MM/yyyy HH:mm}){Environment.NewLine}" +
            $"{lojasEscolhidasCount} de {_lojasDaSugestao.Count} loja(s) serão exportadas.{Environment.NewLine}" +
            $"{TextoDaJanela(janela)}.{Environment.NewLine}" +
            $"Pasta de saída: {pastaSaida}";

        var arquivoJaExiste = File.Exists(zipEsperado);
        if (arquivoJaExiste)
        {
            mensagem += Environment.NewLine + Environment.NewLine +
                $"Já existe um arquivo \"{Path.GetFileName(zipEsperado)}\" nesta pasta -- continuar vai substituí-lo.";
        }

        mensagem += Environment.NewLine + Environment.NewLine + "Confirma a extração?";

        return MessageBox.Show(this, mensagem, "Extrator", MessageBoxButtons.YesNo,
            arquivoJaExiste ? MessageBoxIcon.Warning : MessageBoxIcon.Question) == DialogResult.Yes;
    }

    private Task ExtrairAsync()
    {
        if (_sugestoes.CurrentRow?.DataBoundItem is not SugestaoLinha selecionada || _janela is not { Viavel: true } janela)
        {
            MessageBox.Show(this, "Selecione uma sugestão com janela viável.", "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }

        // _lojasEscolhidas é null até o operador confirmar uma escolha no diálogo, e null
        // NÃO pode significar "todas" aqui -- essa conversão silenciosa é a falha de
        // confidencialidade que este guard existe para fechar: sem escolha, a extração
        // recusa em vez de decidir por conta própria o que sai da máquina do cliente.
        if (_lojasEscolhidas is not { Count: > 0 } lojasEscolhidas)
        {
            MessageBox.Show(this, "Escolha ao menos uma loja em \"Escolher lojas…\" antes de extrair.", "Extrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }

        var pastaSaida = _pastaSaida.Text.Trim();

        // Mesmo instante que ExtractionService.Run vai usar para nomear o arquivo (ver
        // ZipNaming) -- é o que permite avisar aqui se o ZIP vai substituir um existente.
        // O minuto pode virar entre esta pergunta e o início de fato da extração: o
        // arquivo gravado seria então outro, e o aviso desta pergunta, sem efeito.
        // Inofensivo -- na pior hipótese o operador respondeu a uma pergunta sobre um
        // nome de arquivo que não é mais o que vai ser gravado -- mas quem ler isto
        // depois pode se confundir se não achar a explicação aqui.
        var zipEsperado = ZipNaming.BuildPath(pastaSaida, DateTime.Now);
        if (!ConfirmarExtracao(selecionada, janela, lojasEscolhidas.Count, pastaSaida, zipEsperado))
        {
            return Task.CompletedTask;
        }

        var request = new ExtractionRequest
        {
            ConnectionString = BuildConnectionString(),
            SugestaoId = selecionada.SugestaoId,
            DataInicial = janela.Inicio,
            DataFinal = janela.Fim,
            OutputDirectory = pastaSaida,
            LojaIds = lojasEscolhidas,
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

        // Identifica **o que** está sendo extraído antes de começar. Sem esta linha o log dizia
        // "Extraindo…" e depois "ZIP gerado", e não havia como saber de qual sugestão o arquivo
        // era — trocar a seleção depois de extrair deixava tela e arquivo discordando, sem nada
        // no log para desempatar. Aconteceu: um ZIP da sugestão 7840 foi lido como sendo da
        // 7823, que era a linha selecionada no momento em que se olhou.
        _log.Escrever(
            $"Extraindo a sugestão {selecionada.SugestaoId} ({selecionada.Metodo}) de " +
            $"{selecionada.DataHora:dd/MM/yyyy HH:mm} — janela {janela.Inicio:dd/MM/yyyy} a " +
            $"{janela.Fim:dd/MM/yyyy}.");

        return ExecutarAsync("Extraindo", StageContract.WriteOrder.Length,
            ct => new ExtractionService().Run(request, progresso, ct),
            resultado =>
            {
                _log.Escrever($"ZIP gerado: {resultado.ZipPath} ({resultado.ZipBytes / 1024d / 1024d:N1} MB)");
                _log.Escrever($"Lojas exportadas: {resultado.LojasExportadas.Count} de {resultado.LojasNaSugestao}.");
                foreach (var (file, count) in resultado.RowsByFile) _log.Escrever($"  {file}: {count:N0} linhas");
                foreach (var warning in resultado.Warnings) _log.Escrever($"  AVISO: {warning}");
            });
    }

    private void CopiarLog()
    {
        if (_painelDeLog.TextLength > 0) Clipboard.SetText(_painelDeLog.Text);
        _log.Escrever("Log copiado para a área de transferência.");
    }

    // Sem coluna de cobertura: ela vem do DIAS_ESTOQUE dos itens, e agregar
    // SUGESTOES_COMPRAS_RESULTADO para o catalogo inteiro custava ~20 min na instancia real
    // (ver CatalogoService). O grid lista; quem descobre cobertura e viabilidade e a
    // contagem da linha selecionada.
    private sealed record SugestaoLinha(long SugestaoId, string Descricao, DateTime DataHora, string Metodo);
}
