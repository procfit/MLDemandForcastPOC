using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.Purchasing;

internal static class PurchasingEndpoints
{
    public static IEndpointRouteBuilder MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchasing").WithTags("Purchasing");

        group.MapPost("/simulate", EnqueueAsync)
             .WithName("EnqueueSimulation")
             .Produces<SimulacaoView>(StatusCodes.Status202Accepted)
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListAsync)
             .WithName("ListSimulations")
             .Produces<IReadOnlyList<SimulacaoView>>();

        group.MapGet("/{id:guid}", GetByIdAsync)
             .WithName("GetSimulation")
             .Produces<SimulacaoView>()
             .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> EnqueueAsync(
        [FromBody] EnqueueSimulationRequest? req,
        EngineDbContext db,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (req is null || req.TreinoJobId == Guid.Empty)
            return Results.BadRequest(new ValidationErrorResponse(["TreinoJobId é obrigatório."]));

        var treino = await db.TreinoJobs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == req.TreinoJobId, ct);
        if (treino is null)
            return Results.BadRequest(new ValidationErrorResponse([$"TreinoJob {req.TreinoJobId} não existe."]));
        if (treino.Status != TreinoStatus.Concluido)
            return Results.BadRequest(new ValidationErrorResponse([$"TreinoJob {req.TreinoJobId} ainda não concluiu (Status={treino.Status})."]));

        var janela = req.JanelaDias ?? 60;
        var lt = req.LeadTimeDias ?? 7;
        var ciclo = req.CicloDias ?? 7;
        var z = req.FatorServico ?? 1.65;

        var erros = new List<string>();
        if (janela is < 14 or > 365) erros.Add("JanelaDias deve estar entre 14 e 365.");
        if (lt is < 1 or > 30) erros.Add("LeadTimeDias deve estar entre 1 e 30.");
        if (ciclo is < 1 or > 30) erros.Add("CicloDias deve estar entre 1 e 30.");
        if (z is < 0 or > 4) erros.Add("FatorServico deve estar entre 0 e 4.");
        if (erros.Count > 0) return Results.BadRequest(new ValidationErrorResponse(erros));

        var job = new SimulacaoCompra
        {
            Id = Guid.CreateVersion7(),
            // Herdada do treino, não recebida do request: garante que a simulação
            // roda sobre a mesma rede cujos dados treinaram o modelo.
            RedeId = treino.RedeId,
            Status = SimulacaoStatus.Pendente,
            DataAgendamento = DateTimeOffset.UtcNow,
            TreinoJobId = req.TreinoJobId,
            JanelaDias = janela,
            LeadTimeDias = lt,
            CicloDias = ciclo,
            FatorServico = z,
        };
        db.SimulacoesCompra.Add(job);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Simulação {Id} enfileirada (treino={Treino} · janela={Janela}d · LT={LT} · ciclo={Ciclo} · z={Z}).",
            job.Id, req.TreinoJobId, janela, lt, ciclo, z);
        return Results.Accepted($"/api/purchasing/{job.Id}", ToView(job));
    }

    private static async Task<IResult> ListAsync(
        EngineDbContext db, CancellationToken ct, [FromQuery] int take = 50, [FromQuery] int redeId = 1)
    {
        var jobs = await db.SimulacoesCompra
            .AsNoTracking()
            .Where(j => j.RedeId == redeId)
            .OrderByDescending(j => j.DataAgendamento)
            .Take(Math.Clamp(take, 1, 200))
            .Select(j => new SimulacaoView(
                j.Id, j.Status.ToString(), j.DataAgendamento, j.DataInicioProcessamento,
                j.DataConclusao, j.TreinoJobId, j.JanelaDias, j.LeadTimeDias, j.CicloDias,
                j.FatorServico, j.SeriesSimuladas, j.MensagemErro, null))
            .ToListAsync(ct);
        return Results.Ok(jobs);
    }

    private static async Task<IResult> GetByIdAsync(Guid id, EngineDbContext db, CancellationToken ct)
    {
        var job = await db.SimulacoesCompra.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return job is null ? Results.NotFound() : Results.Ok(ToView(job));
    }

    private static SimulacaoView ToView(SimulacaoCompra j) => new(
        j.Id, j.Status.ToString(), j.DataAgendamento, j.DataInicioProcessamento,
        j.DataConclusao, j.TreinoJobId, j.JanelaDias, j.LeadTimeDias, j.CicloDias,
        j.FatorServico, j.SeriesSimuladas, j.MensagemErro, j.ResultadoJson);
}

internal sealed record EnqueueSimulationRequest(
    Guid TreinoJobId,
    int? JanelaDias,
    int? LeadTimeDias,
    int? CicloDias,
    double? FatorServico);

internal sealed record SimulacaoView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    Guid TreinoJobId,
    int JanelaDias,
    int LeadTimeDias,
    int CicloDias,
    double FatorServico,
    long? SeriesSimuladas,
    string? MensagemErro,
    string? ResultadoJson);
