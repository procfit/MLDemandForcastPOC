using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Preferências gravadas ao lado do executável. A senha nunca é persistida.
/// </summary>
internal sealed class AppConfig
{
    public string Servidor { get; set; } = string.Empty;
    public int Porta { get; set; } = 1433;
    public string Banco { get; set; } = string.Empty;
    public bool WindowsAuth { get; set; }
    public string Usuario { get; set; } = string.Empty;
    public string PastaSaida { get; set; } = string.Empty;

    /// <summary>
    /// Valor de <c>Application Name</c> na conexão. Vazio = deixa o default do
    /// provider. Instalações do PBS costumam ter um <i>logon trigger</i> que
    /// aceita só certos APP_NAME(); se a conexão for recusada "devido à execução
    /// do acionador", é aqui que se ajusta — sem recompilar.
    /// </summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>
    /// Limites de espera das leituras curtas. Ficam aqui porque rede de cliente
    /// varia e trocar isto não pode exigir recompilação. A extração não tem limite
    /// (ver ExtractionService): ela varre dezenas de milhões de linhas por natureza,
    /// e um timeout ali só produziria falha no meio de um ZIP que estava indo bem.
    /// </summary>
    public int TimeoutConexaoSegundos { get; set; } = 15;
    public int TimeoutConsultaSegundos { get; set; } = 30;
    public int TimeoutContagemSegundos { get; set; } = 15;

    /// <summary>Quantos meses para trás o catálogo procura sugestões.</summary>
    public int MesesRetroativos { get; set; } = 12;

    internal const int TimeoutConexaoPadrao = 15;
    internal const int TimeoutConsultaPadrao = 30;
    internal const int TimeoutContagemPadrao = 15;
    internal const int MesesRetroativosPadrao = 12;

    /// <summary>O arquivo é editado à mão; valor fora de faixa não pode virar espera infinita.</summary>
    internal static int Segundos(int valor, int padrao) => valor is > 0 and <= 3600 ? valor : padrao;

    private static string FilePath
    {
        get
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "extrator.config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig()
                : new AppConfig();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pasta somente leitura: perder a preferência não justifica travar o app.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal static class ConnectionStringFactory
{
    public static string Build(AppConfig config, string senha)
    {
        var builder = new SqlConnectionStringBuilder
        {
            // Diferente do driver tedious, o SqlClient entende "host,porta".
            DataSource = config.Porta == 1433 ? config.Servidor : $"{config.Servidor},{config.Porta}",
            InitialCatalog = config.Banco,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = AppConfig.Segundos(config.TimeoutConexaoSegundos, AppConfig.TimeoutConexaoPadrao),

            // Reconecta conexão ociosa quebrada. Não salva comando em execução --
            // o caminho até o PBS do cliente derruba consulta em voo, e isso é
            // tratado por Retentativa.
            ConnectRetryCount = 3,
            ConnectRetryInterval = 10,
        };

        if (!string.IsNullOrWhiteSpace(config.ApplicationName))
        {
            builder.ApplicationName = config.ApplicationName;
        }

        if (config.WindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = config.Usuario;
            builder.Password = senha;
        }

        return builder.ConnectionString;
    }
}
