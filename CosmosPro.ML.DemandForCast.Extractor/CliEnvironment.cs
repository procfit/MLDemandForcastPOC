using System.Globalization;

namespace CosmosPro.ML.DemandForCast.Extractor;

internal sealed record CliEnvironmentResult(AppConfig? Config, string Senha, string? Erro);

/// <summary>
/// Resolve os dados de conexão a partir de variáveis de ambiente. A senha fica
/// fora de <c>argv</c> de propósito: argumentos de processo são legíveis por
/// qualquer processo da máquina, e este extrator aponta para o ERP de produção
/// do cliente.
/// <para>
/// O prefixo é parâmetro (<c>--env-prefix</c>) para uma segunda rede não exigir
/// mudança de código: <c>NATUSFARMA_PBS_PROD_</c> + <c>MSSQL_SERVER</c> dá o nome
/// já usado nesta máquina por outra ferramenta.
/// </para>
/// </summary>
internal static class CliEnvironment
{
    public const string PrefixoPadrao = "PBS_";
    public const int PortaPadrao = 1433;

    public const string SufixoServidor = "MSSQL_SERVER";
    public const string SufixoBanco = "MSSQL_DATABASE";
    public const string SufixoUsuario = "MSSQL_USER";
    public const string SufixoSenha = "MSSQL_PASSWORD";
    public const string SufixoPorta = "MSSQL_PORT";
    public const string SufixoApplicationName = "MSSQL_APP_NAME";

    public static CliEnvironmentResult Resolve(CliOptions options, Func<string, string?> ler)
    {
        string Nome(string sufixo) => options.EnvPrefix + sufixo;

        string? Valor(string sufixo)
        {
            var bruto = ler(Nome(sufixo));
            return string.IsNullOrWhiteSpace(bruto) ? null : bruto.Trim();
        }

        if (Valor(SufixoServidor) is not { } servidor) return Ausente(Nome(SufixoServidor));
        if (Valor(SufixoBanco) is not { } banco) return Ausente(Nome(SufixoBanco));

        var usuario = string.Empty;
        var senha = string.Empty;
        if (!options.IntegratedSecurity)
        {
            if (Valor(SufixoUsuario) is not { } u) return Ausente(Nome(SufixoUsuario));
            if (Valor(SufixoSenha) is not { } s) return Ausente(Nome(SufixoSenha));
            usuario = u;
            senha = s;
        }

        var porta = PortaPadrao;
        if (options.Porta is { } portaDaLinhaDeComando)
        {
            porta = portaDaLinhaDeComando;
        }
        else if (Valor(SufixoPorta) is { } portaTexto)
        {
            if (!int.TryParse(portaTexto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                return new CliEnvironmentResult(null, string.Empty,
                    $"A variável de ambiente {Nome(SufixoPorta)} deve conter uma porta entre 1 e 65535; contém '{portaTexto}'.");
            }
            porta = p;
        }

        var applicationName = options.ApplicationName ?? Valor(SufixoApplicationName) ?? string.Empty;

        var config = new AppConfig
        {
            Servidor = servidor,
            Porta = porta,
            Banco = banco,
            WindowsAuth = options.IntegratedSecurity,
            Usuario = usuario,
            ApplicationName = applicationName,
        };

        return new CliEnvironmentResult(config, senha, null);
    }

    private static CliEnvironmentResult Ausente(string nome) =>
        new(null, string.Empty, $"Variável de ambiente obrigatória não definida: {nome}.");
}
