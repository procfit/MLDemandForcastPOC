using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.SyntheticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.ApiService.Imports;

internal static class ImportsEndpoints
{
    public const string BucketName = "imports";
    private const long MaxUploadBytes = 500L * 1024 * 1024; // 500 MB

    public static IEndpointRouteBuilder MapImportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/imports").WithTags("Imports");

        group.MapPost("/upload", UploadAsync)
             .DisableAntiforgery()
             .WithName("UploadImport")
             .Accepts<IFormFile>("multipart/form-data")
             .Produces<UploadResponse>(StatusCodes.Status202Accepted)
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetByIdAsync)
             .WithName("GetImport")
             .Produces<CargaStageView>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/", ListAsync)
             .WithName("ListImports")
             .Produces<IReadOnlyList<CargaStageView>>();

        group.MapPost("/synthetic", GenerateSyntheticAsync)
             .WithName("GenerateSyntheticImport")
             .Accepts<SyntheticRequest>("application/json")
             .Produces<UploadResponse>(StatusCodes.Status202Accepted)
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> GenerateSyntheticAsync(
        [FromBody] SyntheticRequest req,
        EngineDbContext db,
        IMinioClient minio,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var errors = new List<string>();
        if (req.NumLojas is < 1 or > 200) errors.Add("NumLojas deve estar entre 1 e 200.");
        if (req.NumSkus is < 1 or > 5000) errors.Add("NumSkus deve estar entre 1 e 5000.");
        if (req.DataFim < req.DataInicio) errors.Add("DataFim deve ser >= DataInicio.");
        if ((req.DataFim.DayNumber - req.DataInicio.DayNumber) > 365 * 5)
            errors.Add("Horizonte máximo: 5 anos.");
        if (errors.Count > 0) return Results.BadRequest(new ValidationErrorResponse(errors));

        var options = new SyntheticDatasetOptions
        {
            NumLojas = req.NumLojas,
            NumSkus = req.NumSkus,
            DataInicio = req.DataInicio,
            DataFim = req.DataFim,
            Seed = req.Seed,
        };

        // Geração em pool de threads — pra 20×500×12meses leva poucos segundos
        // mas evita bloquear o request thread.
        logger.LogInformation(
            "Gerando dataset sintético: lojas={Lojas} skus={Skus} de {Inicio} a {Fim} seed={Seed}",
            options.NumLojas, options.NumSkus, options.DataInicio, options.DataFim, options.Seed);

        var result = await Task.Run(() => SyntheticDatasetGenerator.Generate(options), ct);

        logger.LogInformation(
            "Dataset gerado em {Duracao}: vendas={V} estoques={E} compras={C} promos={P} iqvia={I} sinais={S} bytes={B}",
            result.Stats.Duration, result.Stats.Vendas, result.Stats.Estoques, result.Stats.Compras,
            result.Stats.Promocoes, result.Stats.Iqvia, result.Stats.SinaisExternos, result.ZipBytes.Length);

        var carga = new CargaStage
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = CargaStageStatus.Pendente,
            DataAgendamento = DateTimeOffset.UtcNow,
            NomeArquivoOriginal = $"sintetico-{options.NumLojas}lojas-{options.NumSkus}skus-{options.DataInicio:yyyyMMdd}-{options.DataFim:yyyyMMdd}.zip",
            BlobKey = string.Empty,
            UsuarioId = "synthetic-generator",
        };
        carga.BlobKey = $"{carga.Id}.zip";

        await EnsureBucketExistsAsync(minio, BucketName, ct);

        using (var ms = new MemoryStream(result.ZipBytes))
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(BucketName)
                .WithObject(carga.BlobKey)
                .WithStreamData(ms)
                .WithObjectSize(result.ZipBytes.Length)
                .WithContentType("application/zip"),
                ct);
        }

        db.CargasStage.Add(carga);
        await db.SaveChangesAsync(ct);

        return Results.Accepted(
            uri: $"/api/imports/{carga.Id}",
            value: new UploadResponse(carga.Id, carga.Status.ToString(), carga.DataAgendamento));
    }

    private static async Task<IResult> UploadAsync(
        IFormFile file,
        EngineDbContext db,
        IMinioClient minio,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new ValidationErrorResponse(["Arquivo vazio."]));
        }

        if (file.Length > MaxUploadBytes)
        {
            return Results.BadRequest(new ValidationErrorResponse(
                [$"Arquivo excede o limite de {MaxUploadBytes / (1024 * 1024)} MB."]));
        }

        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ValidationErrorResponse(["O upload deve ser um arquivo .zip."]));
        }

        // Valida estrutura do ZIP antes de gastar I/O com MinIO + DB.
        await using (var validateStream = file.OpenReadStream())
        {
            var validation = ImportValidator.Validate(validateStream);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new ValidationErrorResponse(validation.Errors));
            }
        }

        var carga = new CargaStage
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = CargaStageStatus.Pendente,
            DataAgendamento = DateTimeOffset.UtcNow,
            NomeArquivoOriginal = file.FileName,
            // Define BlobKey antes do upload para evitar 2-fase. Reusa o Id.
            BlobKey = string.Empty,
            UsuarioId = "anonymous",
        };
        carga.BlobKey = $"{carga.Id}.zip";

        // Garante bucket (idempotente).
        await EnsureBucketExistsAsync(minio, BucketName, ct);

        // Upload do ZIP para MinIO. Stream direto, sem materializar em memória.
        await using (var uploadStream = file.OpenReadStream())
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(BucketName)
                .WithObject(carga.BlobKey)
                .WithStreamData(uploadStream)
                .WithObjectSize(file.Length)
                .WithContentType("application/zip"),
                ct);
        }

        // Persiste a CargaStage. Em caso de falha do INSERT após o upload, o
        // objeto fica órfão no MinIO — aceitável no POC; um GC periódico ou
        // limpeza manual via DbGate resolve.
        db.CargasStage.Add(carga);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Carga {Id} enfileirada: arquivo={Arquivo} bytes={Bytes}",
            carga.Id, file.FileName, file.Length);

        return Results.Accepted(
            uri: $"/api/imports/{carga.Id}",
            value: new UploadResponse(carga.Id, carga.Status.ToString(), carga.DataAgendamento));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        EngineDbContext db,
        CancellationToken ct)
    {
        var carga = await db.CargasStage
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(ProjectToView)
            .FirstOrDefaultAsync(ct);

        return carga is null ? Results.NotFound() : Results.Ok(carga);
    }

    private static async Task<IResult> ListAsync(
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int take = 50,
        [FromQuery] int redeId = 1)
    {
        var cargas = await db.CargasStage
            .AsNoTracking()
            .Where(c => c.RedeId == redeId)
            .OrderByDescending(c => c.DataAgendamento)
            .Take(Math.Clamp(take, 1, 200))
            .Select(ProjectToView)
            .ToListAsync(ct);
        return Results.Ok(cargas);
    }

    private static readonly System.Linq.Expressions.Expression<Func<CargaStage, CargaStageView>> ProjectToView =
        c => new CargaStageView(
            c.Id,
            c.Status.ToString(),
            c.DataAgendamento,
            c.DataInicioProcessamento,
            c.DataConclusao,
            c.NomeArquivoOriginal,
            c.BlobKey,
            c.MensagemErro,
            c.LinhasImportadas);

    /// <summary>Reusado por <c>ComparacoesEndpoints</c> — mesmo padrão de bucket idempotente.</summary>
    internal static async Task EnsureBucketExistsAsync(IMinioClient minio, string bucket, CancellationToken ct)
    {
        var exists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
        if (!exists)
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);
        }
    }
}

internal sealed record UploadResponse(Guid Id, string Status, DateTimeOffset DataAgendamento);

internal sealed record CargaStageView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    string NomeArquivoOriginal,
    string BlobKey,
    string? MensagemErro,
    long? LinhasImportadas);

internal sealed record ValidationErrorResponse(IReadOnlyList<string> Errors);

internal sealed record SyntheticRequest(
    int NumLojas,
    int NumSkus,
    DateOnly DataInicio,
    DateOnly DataFim,
    int Seed);
