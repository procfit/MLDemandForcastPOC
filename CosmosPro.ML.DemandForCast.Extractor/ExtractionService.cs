using System.Data;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Executa as queries de extração e grava o ZIP no formato que o import do
/// projeto espera. Síncrono de propósito: roda inteiro numa thread de fundo e
/// o cancelamento é checado a cada bloco de linhas.
/// </summary>
internal sealed class ExtractionService
{
    // Vendas e estoque varrem dezenas de milhões de linhas; timeout fixo só
    // atrapalharia. O cancelamento é feito pelo usuário, via CancellationToken.
    private const int CommandTimeoutSeconds = 0;
    private const int ProgressRowInterval = 25_000;

    public ExtractionResult Run(ExtractionRequest request, IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(request.OutputDirectory, $"extracao-pbs_{stamp}.zip");

        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        try
        {
            using (var output = File.Create(zipPath))
            using (var zip = new CsvZipWriter(output))
            using (var connection = new SqlConnection(request.ConnectionString))
            {
                connection.Open();

                var total = StageContract.WriteOrder.Length;
                rows[StageContract.Lojas] = CopyQuery(connection, "lojas.sql", StageContract.Lojas, zip, request, 1, total, progress, ct, InspectLoja(warnings));
                rows[StageContract.Produtos] = CopyQuery(connection, "produtos.sql", StageContract.Produtos, zip, request, 2, total, progress, ct);
                rows[StageContract.Vendas] = CopyQuery(connection, "vendas.sql", StageContract.Vendas, zip, request, 3, total, progress, ct);
                rows[StageContract.EstoquesDiarios] = CopyEstoques(connection, zip, request, 4, total, progress, ct);
                rows[StageContract.Compras] = CopyQuery(connection, "compras.sql", StageContract.Compras, zip, request, 5, total, progress, ct);
                rows[StageContract.Promocoes] = CopyQuery(connection, "promocoes.sql", StageContract.Promocoes, zip, request, 6, total, progress, ct);

                // Sem fonte no ERP: o IQVIA é dado de mercado externo. O arquivo
                // precisa existir porque o validador do import exige os sete CSVs.
                using (zip.CreateEntry(StageContract.MercadoIqvia, StageContract.Headers[StageContract.MercadoIqvia])) { }
                rows[StageContract.MercadoIqvia] = 0;
                progress.Report(new ExtractionProgress(StageContract.MercadoIqvia, 7, total, 0));
            }
        }
        catch
        {
            // ZIP parcial é pior que nenhum: ele passa na validação de header do
            // import e entraria no Stage como se estivesse completo.
            TryDelete(zipPath);
            throw;
        }

        if (rows[StageContract.Vendas] == 0)
        {
            warnings.Add("Nenhuma venda no período/lojas selecionados — confira os parâmetros.");
        }
        if (rows[StageContract.EstoquesDiarios] == 0)
        {
            warnings.Add("Nenhum estoque no período — o histórico de ESTOQUE_LANCAMENTOS costuma cobrir apenas os últimos meses.");
        }

        return new ExtractionResult(zipPath, new FileInfo(zipPath).Length, rows, warnings);
    }

    /// <summary>Lê as lojas disponíveis para o usuário escolher na UI.</summary>
    public static IReadOnlyList<LojaOption> LoadLojas(string connectionString, CancellationToken ct)
    {
        const string sql = """
            SELECT EMPRESA_USUARIA, COALESCE(NULLIF(LTRIM(RTRIM(NOME_FANTASIA)),''), NOME)
            FROM dbo.EMPRESAS_USUARIAS
            WHERE ATIVO = 'S'
            ORDER BY EMPRESA_USUARIA;
            """;

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();

        var lojas = new List<LojaOption>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            lojas.Add(new LojaOption(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1)));
        }
        return lojas;
    }

    private static long CopyQuery(
        SqlConnection connection,
        string queryFile,
        string entryName,
        CsvZipWriter zip,
        ExtractionRequest request,
        int fileIndex,
        int fileCount,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct,
        Action<IDataRecord>? inspect = null)
    {
        var header = StageContract.Headers[entryName];
        using var entry = zip.CreateEntry(entryName, header);
        using var command = CreateCommand(connection, SqlResources.Load(queryFile), request);
        using var cancelRegistration = ct.Register(command.Cancel);
        using var reader = command.ExecuteReader();

        EnsureShape(reader, header, entryName);

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            inspect?.Invoke(reader);
            entry.WriteRow(reader);
            if (entry.RowCount % ProgressRowInterval == 0)
            {
                progress.Report(new ExtractionProgress(entryName, fileIndex, fileCount, entry.RowCount));
            }
        }

        progress.Report(new ExtractionProgress(entryName, fileIndex, fileCount, entry.RowCount));
        return entry.RowCount;
    }

    private static long CopyEstoques(
        SqlConnection connection,
        CsvZipWriter zip,
        ExtractionRequest request,
        int fileIndex,
        int fileCount,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
    {
        var header = StageContract.Headers[StageContract.EstoquesDiarios];
        using var entry = zip.CreateEntry(StageContract.EstoquesDiarios, header);
        using var command = CreateCommand(connection, SqlResources.Load("estoques_movimentos.sql"), request);
        using var cancelRegistration = ct.Register(command.Cancel);
        using var reader = command.ExecuteReader();

        foreach (var linha in StockCarryForward.Densify(ReadMovements(reader, ct), request.DataFinal))
        {
            ct.ThrowIfCancellationRequested();
            entry.WriteRow(linha.Data, linha.LojaId, linha.Sku, linha.Saldo);
            if (entry.RowCount % ProgressRowInterval == 0)
            {
                progress.Report(new ExtractionProgress(StageContract.EstoquesDiarios, fileIndex, fileCount, entry.RowCount));
            }
        }

        progress.Report(new ExtractionProgress(StageContract.EstoquesDiarios, fileIndex, fileCount, entry.RowCount));
        return entry.RowCount;
    }

    private static IEnumerable<StockMovement> ReadMovements(SqlDataReader reader, CancellationToken ct)
    {
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            // Cinto de segurança: a query já descarta saldo nulo, mas sem isto um
            // NULL derrubaria a extração inteira no meio do streaming.
            if (reader.IsDBNull(3)) continue;

            yield return new StockMovement(
                reader.GetInt32(0),
                reader.GetString(1),
                DateOnly.FromDateTime(reader.GetDateTime(2)),
                reader.GetDecimal(3));
        }
    }

    private static Action<IDataRecord> InspectLoja(List<string> warnings) => record =>
    {
        if (record.GetValue(2) as string == "NI")
        {
            warnings.Add($"Loja {record.GetValue(0)} está sem endereço cadastrado — UF/Cidade saíram como 'NI'.");
        }
    };

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, ExtractionRequest request)
    {
        var placeholders = request.LojaIds
            .Select((_, i) => "@loja" + i.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var command = new SqlCommand(sql.Replace("{{LOJAS}}", string.Join(',', placeholders)), connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };

        for (var i = 0; i < request.LojaIds.Count; i++)
        {
            command.Parameters.Add(placeholders[i], SqlDbType.Int).Value = request.LojaIds[i];
        }
        command.Parameters.Add("@dataInicial", SqlDbType.Date).Value = request.DataInicial.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@dataFinal", SqlDbType.Date).Value = request.DataFinal.ToDateTime(TimeOnly.MinValue);
        return command;
    }

    /// <summary>
    /// Falha cedo se a query divergir do header do Stage — sem isso um erro de
    /// ordem de coluna só apareceria no import, com o dado já embaralhado.
    /// </summary>
    private static void EnsureShape(IDataReader reader, IReadOnlyList<string> header, string entryName)
    {
        if (reader.FieldCount != header.Count)
        {
            throw new InvalidOperationException(
                $"'{entryName}': query devolveu {reader.FieldCount} colunas, esperado {header.Count}.");
        }

        for (var i = 0; i < header.Count; i++)
        {
            if (!string.Equals(reader.GetName(i), header[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{entryName}': coluna {i + 1} é '{reader.GetName(i)}', esperado '{header[i]}'.");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Arquivo travado por antivírus/indexador: o erro original é o que importa.
        }
    }
}

internal static class SqlResources
{
    public static string Load(string fileName)
    {
        var resourceName = $"CosmosPro.ML.DemandForCast.Extractor.Queries.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Query embarcada não encontrada: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
