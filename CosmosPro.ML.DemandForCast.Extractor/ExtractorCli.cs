using System.Globalization;
using System.Text;
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
        LojasNaoSelecionadasErro or LojaForaDaSugestaoErro or EmpresaDivergeDeFilialErro => CliExitCode.ArgumentosInvalidos,
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

        // Segunda ida ao banco, e obrigatória: a cobertura vive no DIAS_ESTOQUE dos itens,
        // não no cabeçalho (ver catalogo_sugestoes_contagens.sql). É um seek pelo índice de
        // SUGESTAO_COMPRA — o mesmo custo que a contagem já tinha.
        var contagem = servico.Contar(connectionString, options.SugestaoId, ct);
        if (contagem.IsFailed) return Falhar(contagem, options.StackTrace);

        var diasCobertura = contagem.Value.DiasCoberturaMax;
        var janela = ExtractionWindow.Derive(DateOnly.FromDateTime(sugestao.DataHora), diasCobertura, hoje);
        if (!janela.Viavel)
        {
            Console.Error.WriteLine(janela.MotivoInviabilidade);
            return CliExitCode.JanelaInviavel;
        }

        Console.WriteLine($"Sugestão {sugestao.SugestaoId} — {Descricao(sugestao)} — {sugestao.DataHora:dd/MM/yyyy HH:mm} — {Metodo(sugestao.TipoCalculo)}");
        Console.WriteLine($"{contagem.Value.QtdLinhas:N0} itens em {contagem.Value.QtdLojas:N0} loja(s).");
        Console.WriteLine($"Janela de dados: {janela.Inicio:dd/MM/yyyy} a {janela.Fim:dd/MM/yyyy} ({diasCobertura} dias de cobertura).");
        Console.WriteLine($"Pasta de saída: {options.OutputDirectory}");
        Console.WriteLine();

        var request = new ExtractionRequest
        {
            ConnectionString = connectionString,
            SugestaoId = sugestao.SugestaoId,
            DataInicial = janela.Inicio,
            DataFinal = janela.Fim,
            OutputDirectory = options.OutputDirectory,
            LojaIds = options.LojaIds,
        };

        var extracao = new ExtractionService().Run(request, new ConsoleProgress(), ct);
        if (extracao.IsFailed) return Falhar(extracao, options.StackTrace);

        var resultado = extracao.Value;
        Console.WriteLine();
        Console.WriteLine($"ZIP gerado: {resultado.ZipPath} ({resultado.ZipBytes / 1024d / 1024d:N1} MB)");
        Console.WriteLine($"Lojas exportadas: {resultado.LojasExportadas.Count} de {resultado.LojasNaSugestao}.");
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
        var erro = resultado.ErroOuFallback();
        Console.Error.WriteLine(erro.Message);

        foreach (var (chave, valor) in erro.Metadata)
        {
            if (chave == ExtratorErro.ChaveDetalhe && !comStackTrace) continue;
            Console.Error.WriteLine($"  {chave}: {valor}");
        }

        if (!comStackTrace) Console.Error.WriteLine($"Rode de novo com {CliParser.FlagStackTrace} para ver a pilha de chamadas.");

        return CliExitCodeMap.De(erro);
    }

    /// <summary>
    /// O catálogo sai numa escrita só, montado em memória. O console do modo linha de
    /// comando roda com <c>AutoFlush</c> ligado, para o progresso da extração aparecer
    /// enquanto ela acontece — mas isso custa um flush por linha, e medido contra a
    /// instância real são 3,5 ms cada: 19.610 sugestões de 12 meses levavam 66 s para
    /// serem impressas, contra 0,27 s de consulta. A listagem é despejo em massa e não
    /// ganha nada em aparecer linha a linha.
    /// </summary>
    private static void EscreverTabela(IReadOnlyList<SugestaoCatalogoCabecalho> catalogo, DateOnly hoje)
    {
        var saida = new StringBuilder();
        saida.AppendLine($"{"Sugestão",10}  {"Data",16}  {"Método",20}  Descrição");

        foreach (var c in catalogo)
        {
            saida.AppendLine(
                $"{c.SugestaoId,10}  {c.DataHora,16:dd/MM/yyyy HH:mm}  {Truncar(Metodo(c.TipoCalculo), 20),20}  {Descricao(c)}");
        }

        saida.AppendLine();
        saida.AppendLine($"{catalogo.Count} sugestão(ões).");
        saida.AppendLine(
            "Cobertura e janela não aparecem aqui: elas vêm do DIAS_ESTOQUE dos itens, e agregar " +
            "SUGESTOES_COMPRAS_RESULTADO para o catálogo inteiro custava ~20 min na instância real. " +
            "O `--extract <id>` faz essa leitura para a sugestão escolhida e recusa, com o motivo, " +
            "se a cobertura for zero, ainda não tiver terminado, ou passar do horizonte do modelo.");
        Console.Out.Write(saida.ToString());
    }

    /// <summary>Uma escrita só, pelo mesmo motivo de <see cref="EscreverTabela"/>.</summary>
    private static void EscreverTsv(IReadOnlyList<SugestaoCatalogoCabecalho> catalogo, DateOnly hoje)
    {
        var saida = new StringBuilder();

        // Colunas de cobertura/janela/viabilidade saíram: elas exigiriam agregar
        // SUGESTOES_COMPRAS_RESULTADO para o catálogo inteiro. O consumidor de TSV que quiser
        // isso pede por sugestão. Ver EscreverTabela.
        saida.AppendLine(string.Join('\t',
            "SugestaoId", "DataHora", "TipoCalculo", "Metodo", "Descricao"));

        foreach (var c in catalogo)
        {
            saida.AppendLine(string.Join('\t',
                c.SugestaoId.ToString(CultureInfo.InvariantCulture),
                c.DataHora.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                c.TipoCalculo.ToString(CultureInfo.InvariantCulture),
                Metodo(c.TipoCalculo),
                SemTabulacao(Descricao(c))));
        }

        Console.Out.Write(saida.ToString());
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
