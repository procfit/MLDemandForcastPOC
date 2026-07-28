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
    public List<int> Lojas { get; set; } = [];

    /// <summary>
    /// Valor de <c>Application Name</c> na conexão. Vazio = deixa o default do
    /// provider. Instalações do PBS costumam ter um <i>logon trigger</i> que
    /// aceita só certos APP_NAME(); se a conexão for recusada "devido à execução
    /// do acionador", é aqui que se ajusta — sem recompilar.
    /// </summary>
    public string ApplicationName { get; set; } = string.Empty;

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
            ConnectTimeout = 15,
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
