using System.Linq.Expressions;
using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.Comparacoes;

/// <summary>
/// Fila de comparações contra o ERP (F13). Separado de
/// <see cref="ComparacoesEndpoints"/> (<c>/api/comparacoes</c>, sessões guiadas de
/// F14) porque são entidades e ciclos de vida distintos.
///
/// <para>
/// <b>Escopo por rede em todos os verbos.</b> O <c>GET</c> por id filtra por
/// <c>RedeId</c> junto do <c>Id</c> e devolve 404 quando não casa — não 403: informar
/// que a linha existe já vazaria que a outra rede rodou uma comparação. Mesmo formato
/// de <see cref="ComparacoesEndpoints"/>, coberto por teste de integração.
/// </para>
/// </summary>
internal static class ComparacaoPbsEndpoints
{
    public static IEndpointRouteBuilder MapComparisonEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/comparison").WithTags("Comparison");

        group.MapPost("/run", EnqueueAsync)
             .WithName("EnqueueComparison")
             .Produces<ComparacaoPbsView>(StatusCodes.Status202Accepted)
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListAsync)
             .WithName("ListComparisons")
             .Produces<IReadOnlyList<ComparacaoPbsView>>();

        group.MapGet("/{id:guid}", GetByIdAsync)
             .WithName("GetComparison")
             .Produces<ComparacaoPbsView>()
             .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> EnqueueAsync(
        [FromBody] EnqueueComparisonRequest? req,
        EngineDbContext db,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        if (req is null || req.TreinoJobId == Guid.Empty)
            return Results.BadRequest(new ValidationErrorResponse(["TreinoJobId é obrigatório."]));

        var erros = new List<string>();
        if (req.TipoCalculo is not (1 or 2))
            erros.Add("TipoCalculo deve ser 1 (Emax e Eseg) ou 2 (Dias de Reposição).");
        if (req.JanelaFim < req.JanelaInicio)
            erros.Add("JanelaFim não pode ser anterior a JanelaInicio.");
        if (erros.Count > 0) return Results.BadRequest(new ValidationErrorResponse(erros));

        // Escopado pela rede do request: um treino de outra rede é indistinguível de
        // inexistente daqui, para não confirmar que ele existe.
        var treino = await db.TreinoJobs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == req.TreinoJobId && t.RedeId == redeId, ct);
        if (treino is null)
            return Results.BadRequest(new ValidationErrorResponse([$"TreinoJob {req.TreinoJobId} não existe."]));
        if (treino.Status != TreinoStatus.Concluido)
            return Results.BadRequest(new ValidationErrorResponse(
                [$"TreinoJob {req.TreinoJobId} ainda não concluiu (Status={treino.Status})."]));

        // O corte de treino NÃO é validado aqui de propósito: quem decide se o modelo
        // pode disputar com o ERP é o ComparacaoProcessor, que é o único lado com acesso
        // à data das sugestões no Stage. Barrar metade da regra aqui e metade lá deixaria
        // as duas metades divergirem.
        var job = new ComparacaoPbs
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = ComparacaoPbsStatus.Pendente,
            DataAgendamento = DateTimeOffset.UtcNow,
            TreinoJobId = req.TreinoJobId,
            JanelaInicio = req.JanelaInicio,
            JanelaFim = req.JanelaFim,
            TipoCalculo = req.TipoCalculo,
        };
        db.ComparacoesPbs.Add(job);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Comparação {Id} enfileirada (rede={Rede} · treino={Treino} · tipo={Tipo} · {Inicio}→{Fim}).",
            job.Id, redeId, job.TreinoJobId, job.TipoCalculo, job.JanelaInicio, job.JanelaFim);

        return Results.Accepted($"/api/comparison/{job.Id}", ToView(job));
    }

    private static async Task<IResult> ListAsync(
        EngineDbContext db, CancellationToken ct, [FromQuery] int redeId = 1, [FromQuery] int take = 50)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var jobs = await db.ComparacoesPbs
            .AsNoTracking()
            .Where(c => c.RedeId == redeId)
            .OrderByDescending(c => c.DataAgendamento)
            .Take(Math.Clamp(take, 1, 200))
            // ResultadoJson pode ser grande — não traz na listagem.
            .Select(ProjectToView)
            .ToListAsync(ct);

        return Results.Ok(jobs);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id, EngineDbContext db, CancellationToken ct, [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var job = await db.ComparacoesPbs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.RedeId == redeId, ct);

        return job is null ? Results.NotFound() : Results.Ok(ToView(job));
    }

    private static readonly Expression<Func<ComparacaoPbs, ComparacaoPbsView>> ProjectToView =
        c => new ComparacaoPbsView(
            c.Id, c.Status.ToString(), c.DataAgendamento, c.DataInicioProcessamento, c.DataConclusao,
            c.TreinoJobId, c.JanelaInicio, c.JanelaFim, c.TipoCalculo, c.MensagemErro, null);

    private static ComparacaoPbsView ToView(ComparacaoPbs c) => new(
        c.Id, c.Status.ToString(), c.DataAgendamento, c.DataInicioProcessamento, c.DataConclusao,
        c.TreinoJobId, c.JanelaInicio, c.JanelaFim, c.TipoCalculo, c.MensagemErro, c.ResultadoJson);
}

/// <param name="JanelaInicio">Primeiro dia (inclusive) em que a sugestão do ERP pode ter sido calculada.</param>
/// <param name="JanelaFim">Último dia (inclusive) em que a sugestão do ERP pode ter sido calculada.</param>
/// <param name="TipoCalculo">
/// 1 = "Emax e Eseg", 2 = "Dias de Reposição". Uma execução mira em UM método: são
/// baselines distintos do ERP e média entre eles não significa nada.
/// </param>
internal sealed record EnqueueComparisonRequest(
    Guid TreinoJobId,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    byte TipoCalculo);

internal sealed record ComparacaoPbsView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    Guid TreinoJobId,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    byte TipoCalculo,
    string? MensagemErro,
    string? ResultadoJson);
