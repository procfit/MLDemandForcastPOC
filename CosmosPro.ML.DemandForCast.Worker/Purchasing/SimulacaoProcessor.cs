using System.Text.Json;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Features;
using CosmosPro.ML.DemandForCast.Forecasting.Engines;
using CosmosPro.ML.DemandForCast.Purchasing;
using CosmosPro.ML.DemandForCast.Purchasing.Policies;
using CosmosPro.ML.DemandForCast.Purchasing.Simulation;
using CosmosPro.ML.DemandForCast.Worker.Training;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.Worker.Purchasing;

/// <summary>
/// Executa um job de <see cref="SimulacaoCompra"/>: carrega o TreinoJob origem
/// (modelo LightGBM + parâmetros), reusa o <see cref="StageObservationLoader"/>
/// para puxar as mesmas SKUs do treino, faz o replay com a política ROP+forecast
/// e grava o resultado JSON.
///
/// <para>
/// Ferramenta secundária desde F13: não compara mais contra uma reimplementação
/// nossa da regra eMax/eSeg (removida — ver <see cref="CosmosPro.ML.DemandForCast.Purchasing.IPurchasingPolicy"/>).
/// O comparativo do TCC contra o baseline real do ERP vive em outro fluxo (F13).
/// </para>
/// </summary>
internal sealed class SimulacaoProcessor(
    IMinioClient minio,
    IConfiguration config,
    IServiceProvider services,
    ILogger<SimulacaoProcessor> logger)
{
    public sealed record Outcome(string ResultadoJson, long SeriesSimuladas);

    public async Task<Outcome> ProcessAsync(SimulacaoCompra job, CancellationToken ct)
    {
        var connStr = config.GetConnectionString("Stage")
            ?? throw new InvalidOperationException("Connection string 'Stage' não encontrada.");

        // 1) Treino origem — modelo + MaxSkus.
        TreinoJob treino;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
            treino = await db.TreinoJobs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == job.TreinoJobId, ct)
                ?? throw new InvalidOperationException($"TreinoJob {job.TreinoJobId} não encontrado.");
        }
        if (treino.Status != TreinoStatus.Concluido || string.IsNullOrEmpty(treino.ModeloBlobKey))
            throw new InvalidOperationException($"TreinoJob {job.TreinoJobId} ainda não concluiu (Status={treino.Status}).");

        // 2) Observações (mesmas regras do treino: top MaxSkus, ABC dinâmica, ruptura marcada).
        var loader = new StageObservationLoader(connStr, logger);
        // Sem o corte do treino de propósito: a simulação precisa justamente dos dias
        // que o modelo não viu — são a verdade contra a qual a política é medida.
        // Aplicar `treino.TreinoAte` aqui apagaria a janela simulada.
        var observations = await loader.LoadAsync(job.RedeId, treino.MaxSkus, treinoAte: null, ct);
        if (observations.Count == 0)
            throw new InvalidOperationException("Sem observações no Stage para simular.");

        var maxData = observations.Max(o => o.Data);
        var fim = maxData;
        var inicio = fim.AddDays(-job.JanelaDias + 1);
        logger.LogInformation("Janela da simulação: {Inicio} → {Fim} ({Dias} dias).", inicio, fim, job.JanelaDias);

        // 3) Estoque inicial no dia anterior à janela.
        var skus = observations.Select(o => o.Sku).Distinct().ToArray();
        var estoqueLoader = new StageEstoqueInicialLoader(connStr, logger);
        var estoqueInicialRaw = await estoqueLoader.LoadAsync(job.RedeId, skus, inicio, ct);
        var estoqueInicial = estoqueInicialRaw.ToDictionary(
            kv => new PurchasingSimulator.SerieKey(kv.Key.Sku, kv.Key.LojaId),
            kv => kv.Value);

        // 4) Features (mesmo lead time/config do treino — F5 default).
        var features = new FeatureBuilder().Build(observations).ToList();
        logger.LogInformation("{N} features para indexar o forecast.", features.Count);

        // 5) Atributos (categoria, ABC, UF) para drill-down — pega 1ª observação de cada série.
        var atributos = observations
            .GroupBy(o => new PurchasingSimulator.SerieKey(o.Sku, o.LojaId))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var o = g.First();
                    return new PurchasingSimulator.SerieAttributes(
                        o.Sku, o.LojaId, o.Categoria, o.ClasseAbc, o.UF, o.Regiao);
                });

        // 6) Modelo LightGBM do treino, baixado do MinIO.
        using var model = await DownloadModelAsync(treino.ModeloBlobKey!, ct);
        var forecaster = new LightGbmForecaster(model, features);

        // 7) Simula a política ROP+forecast (única — ver nota na doc da classe).
        var options = new SimulationOptions
        {
            DataInicio = inicio,
            DataFim = fim,
            LeadTimeDias = job.LeadTimeDias,
            CicloDias = job.CicloDias,
            FatorServico = job.FatorServico,
        };

        var policies = new IPurchasingPolicy[]
        {
            new ForecastRopPolicy(),
        };

        var simulator = new PurchasingSimulator();
        var result = simulator.Run(options, observations, estoqueInicial, atributos, policies, forecaster);

        // Nome dos produtos (só dos SKUs simulados) para a lista de compra na UI.
        var nomes = await LoadNomesAsync(connStr, job.RedeId, skus, ct);

        var output = new SimulationOutput(DateTimeOffset.UtcNow, treino.Id, nomes, result);
        var json = JsonSerializer.Serialize(output);
        return new Outcome(json, result.SeriesAvaliadas);
    }

    private async Task<LightGbmForecastModel> DownloadModelAsync(string blobKey, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket(TreinoProcessor.ModelsBucket)
            .WithObject(blobKey)
            .WithCallbackStream(s => s.CopyTo(ms)),
            ct);
        ms.Position = 0;
        logger.LogInformation("Modelo {Key} baixado ({Bytes} bytes).", blobKey, ms.Length);
        return LightGbmForecastModel.Load(ms);
    }

    /// <summary>
    /// Nome dos produtos simulados. O filtro por rede não é cosmético: <c>Produtos</c> tem
    /// PK <c>(RedeId, Sku)</c> porque o mesmo código de SKU existe em redes diferentes, e
    /// sem ele o dicionário ficaria com o nome que a última rede lida tiver gravado.
    /// </summary>
    private static async Task<Dictionary<string, string>> LoadNomesAsync(
        string connStr, int redeId, IReadOnlyCollection<string> skus, CancellationToken ct)
    {
        var nomes = new Dictionary<string, string>(skus.Count, StringComparer.OrdinalIgnoreCase);
        if (skus.Count == 0) return nomes;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var names = new List<string>(skus.Count);
        int i = 0;
        foreach (var sku in skus)
        {
            var p = $"@s{i++}";
            names.Add(p);
            cmd.Parameters.AddWithValue(p, sku);
        }
        cmd.Parameters.AddWithValue("@redeId", redeId);
        cmd.CommandText =
            $"SELECT Sku, Nome FROM dbo.Produtos WHERE RedeId = @redeId AND Sku IN ({string.Join(", ", names)})";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            nomes[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        return nomes;
    }
}
