using System.Text.Json;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Features;
using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting;
using CosmosPro.ML.DemandForCast.Forecasting.Engines;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.Worker.Training;

/// <summary>
/// Executa um job de treino: carrega o Stage → features (F5) → backtest walk-forward
/// dos engines (naïve, média móvel, LightGBM) → treina o LightGBM final em todo o
/// histórico → salva o modelo (.zip) no MinIO e devolve o resultado serializado.
/// </summary>
internal sealed class TreinoProcessor(
    IMinioClient minio,
    IConfiguration config,
    ILogger<TreinoProcessor> logger)
{
    public const string ModelsBucket = "models";

    private static readonly WalkForwardOptions Backtest = new()
    {
        NumberOfFolds = 4,
        TestWindowDays = 14,
        MinTrainDays = 35,
    };

    public sealed record Outcome(string ModeloBlobKey, string ResultadoJson, long Features);

    public async Task<Outcome> ProcessAsync(TreinoJob job, CancellationToken ct)
    {
        var connStr = config.GetConnectionString("Stage")
            ?? throw new InvalidOperationException("Connection string 'Stage' não encontrada.");

        var loader = new StageObservationLoader(connStr, logger);
        var observations = await loader.LoadAsync(job.RedeId, job.MaxSkus, job.TreinoAte, ct);
        if (observations.Count == 0)
            throw new InvalidOperationException("Sem observações no Stage. Importe dados antes de treinar.");

        // Data efetivamente mais recente que entrou no ajuste. Com corte, é no máximo
        // o dia anterior a ele; sem corte, é o fim do histórico importado. É este o
        // valor que a comparação declara como ModeloTreinadoAte — derivar do corte
        // pedido seria adivinhar, porque o Stage pode simplesmente parar antes.
        var ultimaDataTreinada = observations.Max(o => o.Data);

        var features = new FeatureBuilder().Build(observations).ToList();
        logger.LogInformation("{N} features geradas.", features.Count);
        if (features.Count == 0)
            throw new InvalidOperationException("Histórico insuficiente para gerar features (mínimo ~35 dias por série).");

        // Backtest walk-forward de cada engine.
        var backtest = new WalkForwardBacktest(Backtest);
        var engines = new IForecastEngine[]
        {
            new NaiveSeasonalEngine(),
            new MovingAverageEngine(),
            new LightGbmForecastEngine(),
        };

        var engineResults = new List<EngineResult>();
        foreach (var engine in engines)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Backtest do engine {Engine}…", engine.Name);
            var r = backtest.Run(engine, features);
            engineResults.Add(ToEngineResult(r));
            logger.LogInformation("{Engine}: WAPE global {Wape:P1} (n={N}).", engine.Name, r.Global.Wape, r.Global.N);
        }

        var melhor = engineResults
            .Where(e => e.Global.N > 0)
            .OrderBy(e => e.Global.Wape)
            .FirstOrDefault()?.Engine ?? "n/d";

        // Treina o modelo "de produção" (LightGBM) em TODAS as features válidas.
        var validas = features.Where(f => f.IsValidTarget).ToList();
        using var finalModel = (LightGbmForecastModel)new LightGbmForecastEngine().Fit(validas);

        var blobKey = $"modelo-{job.Id}.zip";
        await SaveModelAsync(finalModel, blobKey, ct);

        var result = new TrainingResult(
            GeradoEm: DateTimeOffset.UtcNow,
            SkusUsados: job.MaxSkus,
            TotalObservacoes: observations.Count,
            TotalFeatures: features.Count,
            Folds: Backtest.NumberOfFolds,
            TestWindowDias: Backtest.TestWindowDays,
            Engines: engineResults,
            MelhorEngine: melhor,
            TreinoAte: job.TreinoAte,
            UltimaDataTreinada: ultimaDataTreinada);

        var json = JsonSerializer.Serialize(result, TrainingResultJson.Options);
        return new Outcome(blobKey, json, features.Count);
    }

    private async Task SaveModelAsync(LightGbmForecastModel model, string blobKey, CancellationToken ct)
    {
        await EnsureBucketAsync(ModelsBucket, ct);
        using var ms = new MemoryStream();
        model.Save(ms);
        ms.Position = 0;
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(ModelsBucket)
            .WithObject(blobKey)
            .WithStreamData(ms)
            .WithObjectSize(ms.Length)
            .WithContentType("application/zip"),
            ct);
        logger.LogInformation("Modelo salvo no MinIO: {Bucket}/{Key} ({Bytes} bytes).", ModelsBucket, blobKey, ms.Length);
    }

    private async Task EnsureBucketAsync(string bucket, CancellationToken ct)
    {
        var exists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
        if (!exists)
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);
    }

    private static EngineResult ToEngineResult(BacktestResult r)
    {
        var porDim = r.PorDimensao.ToDictionary(
            d => d.Key,
            d => (IReadOnlyDictionary<string, MetricsDto>)d.Value.ToDictionary(
                kv => kv.Key, kv => MetricsDto.From(kv.Value)));

        return new EngineResult(r.Engine, MetricsDto.From(r.Global), porDim);
    }
}
