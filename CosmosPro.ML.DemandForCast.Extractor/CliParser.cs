using System.Globalization;

namespace CosmosPro.ML.DemandForCast.Extractor;

internal enum CliCommand
{
    Help,
    List,
    Extract,
}

/// <summary>
/// Códigos de saída do modo linha de comando. São contrato: quem chama o extrator
/// de um script decide o que fazer olhando o número, não o texto da mensagem.
/// </summary>
internal static class CliExitCode
{
    public const int Sucesso = 0;
    public const int ArgumentosInvalidos = 1;
    public const int ConfiguracaoAusente = 2;
    public const int FalhaDeConexao = 3;
    public const int SugestaoNaoEncontrada = 4;
    public const int JanelaInviavel = 5;
    public const int FalhaNaExtracao = 6;
    public const int Cancelado = 7;
}

internal sealed record CliOptions
{
    public required CliCommand Command { get; init; }
    public string EnvPrefix { get; init; } = CliEnvironment.PrefixoPadrao;
    public int? Porta { get; init; }
    public string? ApplicationName { get; init; }
    public bool IntegratedSecurity { get; init; }
    public int MesesRetroativos { get; init; } = CliParser.MesesRetroativosPadrao;
    public bool Tsv { get; init; }
    public long SugestaoId { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
}

internal sealed record CliParseResult(CliOptions? Options, string? Erro);

internal static class CliParser
{
    public const int MesesRetroativosPadrao = 12;

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        CliCommand? command = null;
        var envPrefix = CliEnvironment.PrefixoPadrao;
        int? porta = null;
        string? applicationName = null;
        var integratedSecurity = false;
        var meses = MesesRetroativosPadrao;
        var tsv = false;
        long? sugestaoId = null;
        string? output = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h" or "-?" or "/?":
                    return new CliParseResult(new CliOptions { Command = CliCommand.Help }, null);

                case "--list":
                    if (DefinirComando(ref command, CliCommand.List) is { } erroList) return Falha(erroList);
                    break;

                case "--extract":
                    if (DefinirComando(ref command, CliCommand.Extract) is { } erroExtract) return Falha(erroExtract);
                    break;

                case "--integrated-security":
                    integratedSecurity = true;
                    break;

                case "--tsv":
                    tsv = true;
                    break;

                case "--suggestion-id":
                {
                    if (LerValor(args, ref i) is not { } valor) return Falha(ValorFaltando(arg));
                    if (!long.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    {
                        return Falha($"'{arg}' espera um id inteiro positivo; recebeu '{valor}'.");
                    }
                    sugestaoId = id;
                    break;
                }

                case "--output":
                {
                    if (LerValor(args, ref i) is not { } valor) return Falha(ValorFaltando(arg));
                    if (string.IsNullOrWhiteSpace(valor)) return Falha($"'{arg}' espera uma pasta.");
                    output = valor;
                    break;
                }

                case "--months-back":
                {
                    if (LerValor(args, ref i) is not { } valor) return Falha(ValorFaltando(arg));
                    if (!int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
                    {
                        return Falha($"'{arg}' espera um número de meses maior que zero; recebeu '{valor}'.");
                    }
                    meses = n;
                    break;
                }

                case "--env-prefix":
                {
                    if (LerValor(args, ref i) is not { } valor) return Falha(ValorFaltando(arg));
                    envPrefix = valor;
                    break;
                }

                case "--port":
                {
                    if (LerValor(args, ref i) is not { } valor) return Falha(ValorFaltando(arg));
                    if (!int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                    {
                        return Falha($"'{arg}' espera uma porta entre 1 e 65535; recebeu '{valor}'.");
                    }
                    porta = p;
                    break;
                }

                case "--app-name":
                {
                    if (LerValor(args, ref i) is not { } valor) return Falha(ValorFaltando(arg));
                    applicationName = valor;
                    break;
                }

                default:
                    return Falha($"Argumento desconhecido: '{arg}'. Use --help para ver as opções.");
            }
        }

        if (command is null)
        {
            return Falha("Informe --list ou --extract. Use --help para ver as opções.");
        }

        if (command == CliCommand.List)
        {
            if (sugestaoId is not null) return Falha("'--suggestion-id' só vale com --extract.");
            if (output is not null) return Falha("'--output' só vale com --extract.");
        }
        else
        {
            if (tsv) return Falha("'--tsv' só vale com --list.");
            if (sugestaoId is null) return Falha("'--extract' exige '--suggestion-id <id>'.");
            if (output is null) return Falha("'--extract' exige '--output <pasta>'.");
        }

        return new CliParseResult(
            new CliOptions
            {
                Command = command.Value,
                EnvPrefix = envPrefix,
                Porta = porta,
                ApplicationName = applicationName,
                IntegratedSecurity = integratedSecurity,
                MesesRetroativos = meses,
                Tsv = tsv,
                SugestaoId = sugestaoId ?? 0,
                OutputDirectory = output ?? string.Empty,
            },
            null);
    }

    private static string? DefinirComando(ref CliCommand? atual, CliCommand novo)
    {
        if (atual is null)
        {
            atual = novo;
            return null;
        }
        return atual == novo
            ? $"'{Flag(novo)}' foi informado mais de uma vez."
            : "Informe apenas um modo: --list ou --extract.";
    }

    private static string Flag(CliCommand command) => command == CliCommand.List ? "--list" : "--extract";

    private static string? LerValor(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count) return null;
        i++;
        return args[i];
    }

    private static string ValorFaltando(string flag) => $"'{flag}' exige um valor.";

    private static CliParseResult Falha(string erro) => new(null, erro);

    public static string HelpText { get; } = BuildHelpText();

    private static string BuildHelpText()
    {
        const int larguraNomeVariavel = 26;
        string Variavel(string sufixo) => (CliEnvironment.PrefixoPadrao + sufixo).PadRight(larguraNomeVariavel);

        return $"""
            Extrator PBS -> Stage {ZipManifest.VersaoAtual()}

            Sem argumentos, abre a interface gráfica. Com argumentos, roda em modo linha
            de comando, sem interface.

            USO
              extrator.exe
              extrator.exe --list [opções]
              extrator.exe --extract --suggestion-id <id> --output <pasta> [opções]

            MODOS
              --list                  Lista as sugestões de compra disponíveis no PBS.
              --extract               Extrai uma sugestão e grava o ZIP.
              --help, -h              Mostra esta ajuda.

            OPÇÕES
              --suggestion-id <id>    Id da sugestão a extrair. Obrigatório com --extract.
              --output <pasta>        Pasta onde gravar o ZIP. Obrigatório com --extract.
              --months-back <n>       Quantos meses para trás procurar sugestões no PBS.
                                      Padrão: {MesesRetroativosPadrao}. Vale para --list e para --extract.
              --tsv                   Sai em TSV com cabeçalho, para script. Só com --list.
              --env-prefix <prefixo>  Prefixo das variáveis de ambiente de conexão.
                                      Padrão: {CliEnvironment.PrefixoPadrao}
              --port <n>              Porta do SQL Server. Precede a variável de ambiente.
              --app-name <nome>       Valor de Application Name na conexão. Use quando um
                                      logon trigger do PBS recusar a sessão (erro 17892).
              --integrated-security   Autentica pelo usuário do Windows; dispensa as
                                      variáveis de usuário e senha.

            VARIÁVEIS DE AMBIENTE
              A senha nunca entra na linha de comando: argumentos de processo são visíveis
              para qualquer processo da máquina. Com o prefixo padrão '{CliEnvironment.PrefixoPadrao}':

              {Variavel(CliEnvironment.SufixoServidor)}Host do SQL Server. Obrigatória.
              {Variavel(CliEnvironment.SufixoBanco)}Banco do PBS. Obrigatória.
              {Variavel(CliEnvironment.SufixoUsuario)}Usuário SQL. Obrigatória, salvo --integrated-security.
              {Variavel(CliEnvironment.SufixoSenha)}Senha SQL. Obrigatória, salvo --integrated-security.
              {Variavel(CliEnvironment.SufixoPorta)}Porta. Opcional; padrão {CliEnvironment.PortaPadrao}. --port tem precedência.
              {Variavel(CliEnvironment.SufixoApplicationName)}Application Name. Opcional. --app-name tem precedência.

              Com '--env-prefix NATUSFARMA_PBS_PROD_' os nomes viram
              NATUSFARMA_PBS_PROD_{CliEnvironment.SufixoServidor} e assim por diante. Uma segunda
              rede é só um prefixo diferente, sem mudança de código.

            CÓDIGOS DE SAÍDA
              {CliExitCode.Sucesso}  sucesso
              {CliExitCode.ArgumentosInvalidos}  argumentos inválidos
              {CliExitCode.ConfiguracaoAusente}  configuração ausente (variável de ambiente obrigatória não definida)
              {CliExitCode.FalhaDeConexao}  falha de conexão com o SQL Server
              {CliExitCode.SugestaoNaoEncontrada}  sugestão não encontrada no catálogo
              {CliExitCode.JanelaInviavel}  janela de dados inviável para a sugestão escolhida
              {CliExitCode.FalhaNaExtracao}  falha na extração
              {CliExitCode.Cancelado}  cancelado pelo operador (Ctrl+C)

            EXEMPLOS
              extrator.exe --list --env-prefix NATUSFARMA_PBS_PROD_ --port 1435
              extrator.exe --extract --suggestion-id 12345 --output C:\extracoes --env-prefix NATUSFARMA_PBS_PROD_ --port 1435
            """;
    }
}
