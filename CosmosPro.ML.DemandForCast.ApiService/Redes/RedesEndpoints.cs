using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.Redes;

/// <summary>
/// Cadastro de inquilinos. Em F10 o endpoint é aberto — o <c>redeId</c> ainda chega
/// pelo request e é falsificável. F11 amarra tudo ao claim do usuário e restringe
/// este grupo ao papel PowerUser.
/// </summary>
internal static class RedesEndpoints
{
    public static IEndpointRouteBuilder MapRedesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/redes").WithTags("Redes");

        group.MapGet("/", ListAsync)
             .WithName("ListRedes")
             .Produces<IReadOnlyList<RedeView>>();

        group.MapPost("/", CreateAsync)
             .WithName("CreateRede")
             .Produces<RedeView>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> ListAsync(EngineDbContext db, CancellationToken ct)
    {
        var redes = await db.Redes
            .AsNoTracking()
            .OrderBy(r => r.Nome)
            .Select(r => new RedeView(r.Id, r.Nome, r.Slug, r.Ativo))
            .ToListAsync(ct);
        return Results.Ok(redes);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateRedeRequest req,
        EngineDbContext db,
        CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(req.Nome)) errors.Add("Nome é obrigatório.");
        if (req.Nome?.Length > 120) errors.Add("Nome excede 120 caracteres.");
        if (string.IsNullOrWhiteSpace(req.Slug)) errors.Add("Slug é obrigatório.");
        if (req.Slug is { Length: > 40 }) errors.Add("Slug excede 40 caracteres.");
        if (req.Slug is not null && !SlugIsValid(req.Slug))
            errors.Add("Slug aceita apenas letras minúsculas, dígitos e hífen.");
        if (req.CnpjRaiz is { Length: > 14 }) errors.Add("CnpjRaiz excede 14 caracteres.");
        if (errors.Count > 0) return Results.BadRequest(new { errors });

        if (await db.Redes.AnyAsync(r => r.Slug == req.Slug, ct))
        {
            return Results.Conflict(new { error = $"Já existe rede com slug '{req.Slug}'." });
        }

        var rede = new Rede
        {
            Nome = req.Nome!.Trim(),
            Slug = req.Slug!.Trim(),
            CnpjRaiz = string.IsNullOrWhiteSpace(req.CnpjRaiz) ? null : req.CnpjRaiz.Trim(),
            Ativo = true,
            CriadoEm = DateTimeOffset.UtcNow,
        };

        db.Redes.Add(rede);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/redes/{rede.Id}",
            new RedeView(rede.Id, rede.Nome, rede.Slug, rede.Ativo));
    }

    private static bool SlugIsValid(string slug) =>
        slug.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');

    /// <summary>
    /// Guarda de escopo usada por todos os endpoints que recebem <c>redeId</c>.
    /// Transforma erro de wiring em 400 explícito em vez de deixar a consulta rodar
    /// contra uma rede inexistente e devolver vazio como se fosse resposta legítima.
    /// </summary>
    public static async Task<IResult?> ValidateRedeAsync(
        EngineDbContext db, int redeId, CancellationToken ct)
    {
        var rede = await db.Redes.AsNoTracking()
            .Where(r => r.Id == redeId)
            .Select(r => new { r.Id, r.Ativo })
            .FirstOrDefaultAsync(ct);

        if (rede is null)
            return Results.BadRequest(new { error = $"Rede {redeId} não existe." });
        if (!rede.Ativo)
            return Results.BadRequest(new { error = $"Rede {redeId} está inativa." });

        return null;
    }
}

internal sealed record CreateRedeRequest(string? Nome, string? Slug, string? CnpjRaiz);

internal sealed record RedeView(int Id, string Nome, string Slug, bool Ativo);
