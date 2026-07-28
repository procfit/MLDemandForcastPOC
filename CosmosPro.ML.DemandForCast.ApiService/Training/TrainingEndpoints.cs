using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.Training;

internal static class TrainingEndpoints
{
    public static IEndpointRouteBuilder MapTrainingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/training").WithTags("Training");

        group.MapPost("/run", EnqueueAsync)
             .WithName("EnqueueTraining")
             .Produces<TreinoJobView>(StatusCodes.Status202Accepted)
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListAsync)
             .WithName("ListTraining")
             .Produces<IReadOnlyList<TreinoJobView>>();

        group.MapGet("/{id:guid}", GetByIdAsync)
             .WithName("GetTraining")
             .Produces<TreinoJobView>()
             .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> EnqueueAsync(
        [FromBody] EnqueueTrainingRequest? req,
        EngineDbContext db,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var maxSkus = req?.MaxSkus ?? 80;
        if (maxSkus is < 1 or > 5000)
            return Results.BadRequest(new ValidationErrorResponse(["MaxSkus deve estar entre 1 e 5000."]));

        var job = new TreinoJob
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = TreinoStatus.Pendente,
            DataAgendamento = DateTimeOffset.UtcNow,
            MaxSkus = maxSkus,
        };
        db.TreinoJobs.Add(job);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Treino {Id} enfileirado (maxSkus={MaxSkus}).", job.Id, maxSkus);
        return Results.Accepted($"/api/training/{job.Id}", ToView(job));
    }

    private static async Task<IResult> ListAsync(
        EngineDbContext db, CancellationToken ct, [FromQuery] int take = 50, [FromQuery] int redeId = 1)
    {
        var jobs = await db.TreinoJobs
            .AsNoTracking()
            .Where(j => j.RedeId == redeId)
            .OrderByDescending(j => j.DataAgendamento)
            .Take(Math.Clamp(take, 1, 200))
            // ResultadoJson pode ser grande — não traz na listagem.
            .Select(j => new TreinoJobView(
                j.Id, j.Status.ToString(), j.DataAgendamento, j.DataInicioProcessamento,
                j.DataConclusao, j.MaxSkus, j.FeaturesGeradas, j.ModeloBlobKey, j.MensagemErro, null))
            .ToListAsync(ct);
        return Results.Ok(jobs);
    }

    private static async Task<IResult> GetByIdAsync(Guid id, EngineDbContext db, CancellationToken ct)
    {
        var job = await db.TreinoJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return job is null ? Results.NotFound() : Results.Ok(ToView(job));
    }

    private static TreinoJobView ToView(TreinoJob j) => new(
        j.Id, j.Status.ToString(), j.DataAgendamento, j.DataInicioProcessamento,
        j.DataConclusao, j.MaxSkus, j.FeaturesGeradas, j.ModeloBlobKey, j.MensagemErro, j.ResultadoJson);
}

internal sealed record EnqueueTrainingRequest(int? MaxSkus);

internal sealed record TreinoJobView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    int MaxSkus,
    long? FeaturesGeradas,
    string? ModeloBlobKey,
    string? MensagemErro,
    string? ResultadoJson);
