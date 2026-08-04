using System.Globalization;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Erro tipado -> código de saída. Um mapa só: antes o form e o CLI interpretavam a
/// exceção cada um por conta própria, e podiam discordar sobre o que aconteceu.
/// </summary>
internal static class CliExitCodeMap
{
    public static int De(ExtratorErro erro) => erro switch
    {
        ConexaoErro or ConexaoPerdidaErro or LogonTriggerErro => CliExitCode.FalhaDeConexao,
        SugestaoNaoEncontradaErro or SugestaoSemItensErro => CliExitCode.SugestaoNaoEncontrada,
        JanelaInviavelErro => CliExitCode.JanelaInviavel,
        _ => CliExitCode.FalhaNaExtracao,
    };
}

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

        var config = ambiente.Config!;
        var connectionString = ConnectionStringFactory.Build(config, ambiente.Senha);
        var log = new ExtratorLog(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            tela: Console.Error.WriteLine);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Cancelando...");
        };

        try
        {
            return options.Command == CliCommand.List
                ? Listar(options, config, connectionString, log, cts.Token)
                : Extrair(options, config, connectionString, log, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelado pelo operador — o ZIP parcial foi descartado.");
            return CliExitCode.Cancelado;
        }
    }

    private static int Listar(CliOptions options, AppConfig config, string connectionString, ExtratorLog log, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var resultado = new CatalogoService(config, log)
            .Carregar(connectionString, hoje.AddMonths(-options.MesesRetroativos), ct);

        if (resultado.IsFailed) return Falhar(resultado, options.StackTrace);

        var catalogo = resultado.Value;
        if (catalogo.Count == 0)
        {
            Console.Error.WriteLine(
                $"Nenhuma sugestão de compra nos últimos {options.MesesRetroativos} meses. "
                + "Aumente --months-back para procurar mais para trás.");
            return CliExitCode.Sucesso;
        }

        if (options.Tsv) EscreverTsv(catalogo, hoje);
        else EscreverTabela(catalogo, hoje);

        return CliExitCode.Sucesso;
    }

    private static int Extrair(CliOptions options, AppConfig config, string connectionString, ExtratorLog log, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var servico = new CatalogoService(config, log);

        var cabecalho = servico.PorId(connectionString, options.SugestaoId, ct);
        if (cabecalho.IsFailed) return Falhar(cabecalho, options.StackTrace);

        var sugestao = cabecalho.Value;
        var janela = ExtractionWindow.Derive(DateOnly.FromDateTime(sugestao.DataHora), sugestao.DiasCoberturaMax, hoje);
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

        var extracao = new ExtractionService().Run(request, new ConsoleProgress(), ct);
        if (extracao.IsFailed) return Falhar(extracao, options.StackTrace);

        var resultado = extracao.Value;
        Console.WriteLine();
        Console.WriteLine($"ZIP gerado: {resultado.ZipPath} ({resultado.ZipBytes / 1024d / 1024d:N1} MB)");
        foreach (var (arquivo, linhas) in resultado.RowsByFile) Console.WriteLine($"  {arquivo,-28} {linhas,12:N0} linhas");
        foreach (var aviso in resultado.Warnings) Console.WriteLine($"  AVISO: {aviso}");

        return CliExitCode.Sucesso;
    }

    /// <summary>
    /// A primeira linha é a mensagem de negócio; depois vem a metadata da falha
    /// (etapa, query, número SQL, duração), que é o que se cola num chamado. A pilha
    /// só sob pedido, porque ela sepulta o que interessa.
    /// </summary>
    internal static int Falhar<T>(Result<T> resultado, bool comStackTrace)
    {
        var erro = resultado.Errors.OfType<ExtratorErro>().First();
        Console.Error.WriteLine(erro.Message);

        foreach (var (chave, valor) in erro.Metadata)
        {
            if (chave == ExtratorErro.ChaveDetalhe && !comStackTrace) continue;
            Console.Error.WriteLine($"  {chave}: {valor}");
        }

        if (!comStackTrace) Console.Error.WriteLine($"Rode de novo com {CliParser.FlagStackTrace} para ver a pilha de chamadas.");

        return CliExitCodeMap.De(erro);
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
