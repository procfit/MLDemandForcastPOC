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

        if (AbrirConexao(connectionString) is { } erroDeConexao)
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
            Console.Error.WriteLine(MensagemDeFalha(ex));
            return CliExitCode.FalhaNaExtracao;
        }
    }

    /// <summary>
    /// Uma conexão de teste antes do trabalho real é o que permite distinguir
    /// "não consegui falar com o SQL Server" de "a extração quebrou" no código de
    /// saída — depois de aberta, um <c>SqlException</c> pode ser qualquer coisa.
    /// </summary>
    private static string? AbrirConexao(string connectionString)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return MensagemDeFalha(ex);
        }
    }

    private static int Listar(CliOptions options, string connectionString, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var catalogo = ExtractionService.LoadCatalogoSugestoes(connectionString, hoje.AddMonths(-options.MesesRetroativos), ct);

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

    private static int Extrair(CliOptions options, string connectionString, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var catalogo = ExtractionService.LoadCatalogoSugestoes(connectionString, hoje.AddMonths(-options.MesesRetroativos), ct);
        var sugestao = catalogo.FirstOrDefault(c => c.SugestaoId == options.SugestaoId);

        if (sugestao is null)
        {
            Console.Error.WriteLine(
                $"Sugestão {options.SugestaoId} não encontrada nos últimos {options.MesesRetroativos} meses. " +
                "Confira o id com --list, ou aumente --months-back se ela for mais antiga.");
            return CliExitCode.SugestaoNaoEncontrada;
        }

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
        var resultado = service.Run(request, new ConsoleProgress(), ct);

        Console.WriteLine();
        Console.WriteLine($"ZIP gerado: {resultado.ZipPath} ({resultado.ZipBytes / 1024d / 1024d:N1} MB)");
        foreach (var (arquivo, linhas) in resultado.RowsByFile)
        {
            Console.WriteLine($"  {arquivo,-28} {linhas,12:N0} linhas");
        }
        foreach (var aviso in resultado.Warnings)
        {
            Console.WriteLine($"  AVISO: {aviso}");
        }

        return CliExitCode.Sucesso;
    }

    private static void EscreverTabela(IReadOnlyList<SugestaoCatalogo> catalogo, DateOnly hoje)
    {
        Console.WriteLine($"{"Sugestão",10}  {"Data",16}  {"Método",20}  {"Cobert.",7}  {"Linhas",8}  {"Lojas",5}  {"Janela",23}  Descrição");

        foreach (var c in catalogo)
        {
            var janela = ExtractionWindow.Derive(DateOnly.FromDateTime(c.DataHora), c.DiasCoberturaMax, hoje);
            var textoJanela = janela.Viavel
                ? $"{janela.Inicio:dd/MM/yyyy}-{janela.Fim:dd/MM/yyyy}"
                : "inviável";

            Console.WriteLine(
                $"{c.SugestaoId,10}  {c.DataHora,16:dd/MM/yyyy HH:mm}  {Truncar(Metodo(c.TipoCalculo), 20),20}  " +
                $"{c.DiasCoberturaMax,7}  {c.QtdLinhas,8:N0}  {c.QtdLojas,5}  {textoJanela,23}  {Descricao(c)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{catalogo.Count} sugestão(ões). 'inviável' = a cobertura ainda não terminou, então não há como julgar quem acertou.");
    }

    private static void EscreverTsv(IReadOnlyList<SugestaoCatalogo> catalogo, DateOnly hoje)
    {
        Console.WriteLine(string.Join('\t',
            "SugestaoId", "DataHora", "TipoCalculo", "Metodo", "DiasCobertura",
            "QtdLinhas", "QtdLojas", "Viavel", "JanelaInicio", "JanelaFim", "Descricao"));

        foreach (var c in catalogo)
        {
            var janela = ExtractionWindow.Derive(DateOnly.FromDateTime(c.DataHora), c.DiasCoberturaMax, hoje);
            Console.WriteLine(string.Join('\t',
                c.SugestaoId.ToString(CultureInfo.InvariantCulture),
                c.DataHora.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                c.TipoCalculo.ToString(CultureInfo.InvariantCulture),
                Metodo(c.TipoCalculo),
                c.DiasCoberturaMax.ToString(CultureInfo.InvariantCulture),
                c.QtdLinhas.ToString(CultureInfo.InvariantCulture),
                c.QtdLojas.ToString(CultureInfo.InvariantCulture),
                janela.Viavel ? "true" : "false",
                janela.Inicio.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                janela.Fim.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SemTabulacao(Descricao(c))));
        }
    }

    private static string Descricao(SugestaoCatalogo c) =>
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
    /// Erro 17892 = logon trigger recusou a sessão; no PBS costuma ser filtro por
    /// APP_NAME(), e a mensagem crua do SQL Server não diz isso.
    /// </summary>
    private static string MensagemDeFalha(Exception ex) =>
        ex is SqlException { Number: 17892 }
            ? ex.Message + Environment.NewLine
              + "O servidor tem um logon trigger que recusou a conexão — normalmente por causa do "
              + "nome da aplicação. Tente de novo com --app-name <nome>."
            : ex.Message;

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
