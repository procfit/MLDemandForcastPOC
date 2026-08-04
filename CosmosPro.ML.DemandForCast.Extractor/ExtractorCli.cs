using System.Globalization;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Modo linha de comando: listar as sugestões de compra do PBS e extrair uma
/// delas sem abrir a interface gráfica, para um operador em terminal ou para um
/// teste ponta a ponta automatizado.
/// </summary>
internal static class ExtractorCli
{
    public static int Execute(string[] args)
    {
        using var console = ConsoleAttachment.Attach();

        var parse = CliParser.Parse(args);
        if (parse.Erro is { } erroDeArgumento)
        {
            Console.Error.WriteLine(erroDeArgumento);
            return CliExitCode.ArgumentosInvalidos;
        }

        var options = parse.Options!;
        if (options.Command == CliCommand.Help)
        {
            Console.WriteLine(CliParser.HelpText);
            return CliExitCode.Sucesso;
        }

        var ambiente = CliEnvironment.Resolve(options, Environment.GetEnvironmentVariable);
        if (ambiente.Erro is { } erroDeConfiguracao)
        {
            Console.Error.WriteLine(erroDeConfiguracao);
            return CliExitCode.ConfiguracaoAusente;
        }

        var connectionString = ConnectionStringFactory.Build(ambiente.Config!, ambiente.Senha);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Cancelando...");
        };

        if (AbrirConexao(connectionString, options.StackTrace) is { } erroDeConexao)
        {
            Console.Error.WriteLine(erroDeConexao);
            return CliExitCode.FalhaDeConexao;
        }

        try
        {
            return options.Command == CliCommand.List
                ? Listar(options, connectionString, cts.Token)
                : Extrair(options, connectionString, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelado pelo operador — o ZIP parcial foi descartado.");
            return CliExitCode.Cancelado;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(MensagemDeFalha(ex, options.StackTrace));
            return CliExitCode.FalhaNaExtracao;
        }
    }

    /// <summary>
    /// Uma conexão de teste antes do trabalho real é o que permite distinguir
    /// "não consegui falar com o SQL Server" de "a extração quebrou" no código de
    /// saída — depois de aberta, um <c>SqlException</c> pode ser qualquer coisa.
    /// </summary>
    private static string? AbrirConexao(string connectionString, bool comStackTrace)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return MensagemDeFalha(ex, comStackTrace);
        }
    }

    // Ponte temporária (Task 4): Task 8 mata o throw abaixo e wire de verdade o Result.
    private static int Listar(CliOptions options, string connectionString, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var servico = new CatalogoService(new AppConfig(), new ExtratorLog(AppContext.BaseDirectory));
        var resultado = servico.Carregar(connectionString, hoje.AddMonths(-options.MesesRetroativos), ct);
        if (resultado.IsFailed) throw new InvalidOperationException(resultado.Errors[0].Message);
        var catalogo = resultado.Value;

        if (catalogo.Count == 0)
        {
            Console.Error.WriteLine(
                $"Nenhuma sugestão de compra nos últimos {options.MesesRetroativos} meses. " +
                "Aumente --months-back para procurar mais para trás.");
            return CliExitCode.Sucesso;
        }

        if (options.Tsv)
        {
            EscreverTsv(catalogo, hoje);
        }
        else
        {
            EscreverTabela(catalogo, hoje);
        }

        return CliExitCode.Sucesso;
    }

    // Ponte temporária (Task 4): Task 8 mata o throw abaixo e wire de verdade o Result,
    // inclusive a distinção de SugestaoNaoEncontradaErro para CliExitCode.SugestaoNaoEncontrada.
    private static int Extrair(CliOptions options, string connectionString, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        // Busca direta pelo id, sem passar pelo catálogo: quem chega aqui já escolheu, e
        // varrer a lista inteira para achar uma sugestão custava minutos na instância
        // real. Por isso também não há mais limite de meses retroativos nesta rota.
        var servico = new CatalogoService(new AppConfig(), new ExtratorLog(AppContext.BaseDirectory));
        var resultado = servico.PorId(connectionString, options.SugestaoId, ct);
        if (resultado.IsFailed) throw new InvalidOperationException(resultado.Errors[0].Message);
        var sugestao = resultado.Value;

        var janela = ExtractionWindow.Derive(
            DateOnly.FromDateTime(sugestao.DataHora), sugestao.DiasCoberturaMax, hoje);

        if (!janela.Viavel)
        {
            Console.Error.WriteLine(janela.MotivoInviabilidade);
            return CliExitCode.JanelaInviavel;
        }

        Console.WriteLine($"Sugestão {sugestao.SugestaoId} — {Descricao(sugestao)} — {sugestao.DataHora:dd/MM/yyyy HH:mm} — {Metodo(sugestao.TipoCalculo)}");
        Console.WriteLine($"Janela de dados: {janela.Inicio:dd/MM/yyyy} a {janela.Fim:dd/MM/yyyy} ({sugestao.DiasCoberturaMax} dias de cobertura).");
        Console.WriteLine($"Pasta de saída: {options.OutputDirectory}");
        Console.WriteLine();

        var request = new ExtractionRequest
        {
            ConnectionString = connectionString,
            SugestaoId = sugestao.SugestaoId,
            DataInicial = janela.Inicio,
            DataFinal = janela.Fim,
            OutputDirectory = options.OutputDirectory,
        };

        var service = new ExtractionService();
        var resultadoExtracao = service.Run(request, new ConsoleProgress(), ct);

        Console.WriteLine();
        Console.WriteLine($"ZIP gerado: {resultadoExtracao.ZipPath} ({resultadoExtracao.ZipBytes / 1024d / 1024d:N1} MB)");
        foreach (var (arquivo, linhas) in resultadoExtracao.RowsByFile)
        {
            Console.WriteLine($"  {arquivo,-28} {linhas,12:N0} linhas");
        }
        foreach (var aviso in resultadoExtracao.Warnings)
        {
            Console.WriteLine($"  AVISO: {aviso}");
        }

        return CliExitCode.Sucesso;
    }

    private static void EscreverTabela(IReadOnlyList<SugestaoCatalogoCabecalho> catalogo, DateOnly hoje)
    {
        Console.WriteLine($"{"Sugestão",10}  {"Data",16}  {"Método",20}  {"Cobert.",7}  {"Janela",23}  Descrição");

        foreach (var c in catalogo)
        {
            var janela = ExtractionWindow.Derive(DateOnly.FromDateTime(c.DataHora), c.DiasCoberturaMax, hoje);
            var textoJanela = janela.Viavel
                ? $"{janela.Inicio:dd/MM/yyyy}-{janela.Fim:dd/MM/yyyy}"
                : "inviável";

            Console.WriteLine(
                $"{c.SugestaoId,10}  {c.DataHora,16:dd/MM/yyyy HH:mm}  {Truncar(Metodo(c.TipoCalculo), 20),20}  " +
                $"{c.DiasCoberturaMax,7}  {textoJanela,23}  {Descricao(c)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{catalogo.Count} sugestão(ões). 'inviável' = a cobertura ainda não terminou, então não há como julgar quem acertou.");
    }

    private static void EscreverTsv(IReadOnlyList<SugestaoCatalogoCabecalho> catalogo, DateOnly hoje)
    {
        Console.WriteLine(string.Join('\t',
            "SugestaoId", "DataHora", "TipoCalculo", "Metodo", "DiasCobertura",
            "Viavel", "JanelaInicio", "JanelaFim", "Descricao"));

        foreach (var c in catalogo)
        {
            var janela = ExtractionWindow.Derive(DateOnly.FromDateTime(c.DataHora), c.DiasCoberturaMax, hoje);
            Console.WriteLine(string.Join('\t',
                c.SugestaoId.ToString(CultureInfo.InvariantCulture),
                c.DataHora.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                c.TipoCalculo.ToString(CultureInfo.InvariantCulture),
                Metodo(c.TipoCalculo),
                c.DiasCoberturaMax.ToString(CultureInfo.InvariantCulture),
                janela.Viavel ? "true" : "false",
                janela.Inicio.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                janela.Fim.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SemTabulacao(Descricao(c))));
        }
    }

    private static string Descricao(SugestaoCatalogoCabecalho c) =>
        string.IsNullOrWhiteSpace(c.Descricao) ? "(sem descrição)" : c.Descricao.Trim();

    private static string Metodo(byte tipoCalculo) => tipoCalculo switch
    {
        1 => "Emax e Eseg",
        2 => "Dias de Reposição",
        _ => $"Tipo {tipoCalculo}",
    };

    private static string SemTabulacao(string texto) => texto.Replace('\t', ' ').ReplaceLineEndings(" ");

    private static string Truncar(string texto, int limite) =>
        texto.Length <= limite ? texto : texto[..(limite - 1)] + "…";

    /// <summary>
    /// Mensagem de falha do modo linha de comando. A primeira linha já nomeia a
    /// etapa quando o erro veio de dentro da extração (ver
    /// <see cref="ExtractionStepException"/>); depois vem o tipo do erro, que
    /// distingue um problema de dado de um problema de conversão, e por fim a
    /// pilha — só sob pedido, porque ela sepulta a mensagem que interessa.
    /// <para>
    /// Erro 17892 = logon trigger recusou a sessão; no PBS costuma ser filtro por
    /// APP_NAME(), e a mensagem crua do SQL Server não diz isso.
    /// </para>
    /// </summary>
    internal static string MensagemDeFalha(Exception ex, bool comStackTrace)
    {
        var linhas = new List<string> { ex.Message };

        if (ex is SqlException { Number: 17892 })
        {
            linhas.Add(
                "O servidor tem um logon trigger que recusou a conexão — normalmente por causa do "
                + "nome da aplicação. Tente de novo com --app-name <nome>.");
        }

        // O tipo da causa, não o do embrulho: InvalidCastException aponta para
        // coluna sem CONVERT na query, e é isso que o operador precisa reportar.
        linhas.Add($"Tipo do erro: {(ex.InnerException ?? ex).GetType().FullName}");

        linhas.Add(comStackTrace
            ? ex.ToString()
            : $"Rode de novo com {CliParser.FlagStackTrace} para ver a pilha de chamadas.");

        return string.Join(Environment.NewLine, linhas);
    }

    /// <summary>
    /// Escreve na thread que reportou, e não via <see cref="Progress{T}"/>: sem
    /// contexto de sincronização, o <c>Progress</c> despacharia para o pool e as
    /// linhas sairiam fora de ordem.
    /// </summary>
    private sealed class ConsoleProgress : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) =>
            Console.WriteLine($"[{value.FileIndex}/{value.FileCount}] {value.FileName,-28} {value.RowsWritten,12:N0} linhas");
    }
}
