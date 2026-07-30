using System.Linq.Expressions;
using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.ApiService.Comparacoes;

internal static class ComparacoesEndpoints
{
    public static IEndpointRouteBuilder MapComparacoesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/comparacoes").WithTags("Comparacoes");

        group.MapPost("/", CreateAsync)
             .WithName("CreateComparacaoSessao")
             .Produces<SessaoView>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListAsync)
             .WithName("ListComparacaoSessoes")
             .Produces<IReadOnlyList<SessaoView>>();

        group.MapGet("/{id:guid}", GetByIdAsync)
             .WithName("GetComparacaoSessao")
             .Produces<SessaoView>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/dados", UploadDadosAsync)
             .DisableAntiforgery()
             .WithName("UploadComparacaoSessaoDados")
             .Accepts<IFormFile>("multipart/form-data")
             .Produces(StatusCodes.Status202Accepted)
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateSessaoRequest req,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var agora = DateTimeOffset.UtcNow;
        var sessao = new ComparacaoSessao
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Nome = string.IsNullOrWhiteSpace(req.Nome) ? null : req.Nome.Trim(),
            Status = SessaoStatus.AguardandoDados,
            CriadoEm = agora,
            AtualizadoEm = agora,
        };

        db.ComparacaoSessoes.Add(sessao);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/comparacoes/{sessao.Id}", ToView(sessao));
    }

    private static async Task<IResult> ListAsync(
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1,
        [FromQuery] int take = 50)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var sessoes = await db.ComparacaoSessoes
            .AsNoTracking()
            .Where(s => s.RedeId == redeId)
            .OrderByDescending(s => s.CriadoEm)
            .Take(Math.Clamp(take, 1, 200))
            .Select(ProjectToView)
            .ToListAsync(ct);

        return Results.Ok(sessoes);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var sessao = await db.ComparacaoSessoes
            .AsNoTracking()
            .Where(s => s.Id == id && s.RedeId == redeId)
            .Select(ProjectToView)
            .FirstOrDefaultAsync(ct);

        return sessao is null ? Results.NotFound() : Results.Ok(sessao);
    }

    private static async Task<IResult> UploadDadosAsync(
        Guid id,
        IFormFile file,
        EngineDbContext db,
        IMinioClient minio,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var sessao = await db.ComparacaoSessoes
            .Where(s => s.Id == id && s.RedeId == redeId)
            .FirstOrDefaultAsync(ct);
        if (sessao is null) return Results.NotFound();

        // Reenvio a partir de Inviavel/Falha reseta a sessão antes de avançar — os dois
        // saltos passam pela máquina de estados (ComparacaoSessao.PodeTransicionar), então
        // um estado em andamento (ProcessandoDados/Treinando/Comparando) ou terminal
        // (Concluida) rejeita upload em vez de correr por baixo dele.
        var origem = sessao.Status;
        if (origem != SessaoStatus.AguardandoDados)
        {
            if (!ComparacaoSessao.PodeTransicionar(origem, SessaoStatus.AguardandoDados))
            {
                return Results.BadRequest(new ValidationErrorResponse(
                    [$"Sessão em '{origem}' não aceita novo envio de dados."]));
            }
            sessao.Status = SessaoStatus.AguardandoDados;
            sessao.MotivoInviabilidade = null;
            sessao.MensagemErro = null;
        }

        if (!ComparacaoSessao.PodeTransicionar(sessao.Status, SessaoStatus.ProcessandoDados))
        {
            return Results.BadRequest(new ValidationErrorResponse(
                [$"Sessão em '{origem}' não aceita novo envio de dados."]));
        }

        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new ValidationErrorResponse(["Arquivo vazio."]));
        }

        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ValidationErrorResponse(["O upload deve ser um arquivo .zip."]));
        }

        // Mesma validação superficial do upload avulso (Imports). O manifesto.json com
        // a sugestão do PBS é lido pelo Worker (Task 8 da F14) — aqui só garantimos que
        // o ZIP tem a forma esperada dos 7 CSVs do Stage.
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
            BlobKey = string.Empty,
            UsuarioId = "web-comparacao",
        };
        carga.BlobKey = $"{carga.Id}.zip";

        await EnsureBucketExistsAsync(minio, ImportsEndpoints.BucketName, ct);

        await using (var uploadStream = file.OpenReadStream())
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(ImportsEndpoints.BucketName)
                .WithObject(carga.BlobKey)
                .WithStreamData(uploadStream)
                .WithObjectSize(file.Length)
                .WithContentType("application/zip"),
                ct);
        }

        db.CargasStage.Add(carga);
        sessao.CargaStageId = carga.Id;
        sessao.Status = SessaoStatus.ProcessandoDados;
        sessao.AtualizadoEm = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Sessao {SessaoId}: carga {CargaId} enfileirada", sessao.Id, carga.Id);

        return Results.Accepted($"/api/comparacoes/{sessao.Id}");
    }

    private static readonly Expression<Func<ComparacaoSessao, SessaoView>> ProjectToView =
        s => new SessaoView(
            s.Id,
            s.Nome,
            s.Status.ToString(),
            s.CriadoEm,
            s.SugestaoId,
            s.SugestaoDescricao,
            s.SugestaoDataHora,
            s.SugestaoTipoCalculo,
            s.MotivoInviabilidade,
            s.MensagemErro);

    private static SessaoView ToView(ComparacaoSessao s) => new(
        s.Id, s.Nome, s.Status.ToString(), s.CriadoEm,
        s.SugestaoId, s.SugestaoDescricao, s.SugestaoDataHora, s.SugestaoTipoCalculo,
        s.MotivoInviabilidade, s.MensagemErro);

    private static async Task EnsureBucketExistsAsync(IMinioClient minio, string bucket, CancellationToken ct)
    {
        var exists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
        if (!exists)
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);
        }
    }
}

internal sealed record CreateSessaoRequest(string? Nome);

internal sealed record SessaoView(
    Guid Id,
    string? Nome,
    string Status,
    DateTimeOffset CriadoEm,
    long? SugestaoId,
    string? SugestaoDescricao,
    DateTime? SugestaoDataHora,
    byte? SugestaoTipoCalculo,
    string? MotivoInviabilidade,
    string? MensagemErro);
