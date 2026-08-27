using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.ApiService.Mercado;

/// <summary>
/// Ingestão e consulta dos dados de mercado da IQVIA (F16). O dado é por rede e
/// sobrevive aos imports do Stage — vive no banco engine, não no Stage.
/// </summary>
internal static class MercadoEndpoints
{
    public const string BucketName = "mercado";
    private const long MaxUploadBytes = 100L * 1024 * 1024; // 100 MB — o XLSX real tem ~10 MB

    public static IEndpointRouteBuilder MapMercadoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mercado").WithTags("Mercado");

        group.MapPost("/uploads", UploadAsync)
             .DisableAntiforgery()
             .WithName("UploadMercado")
             .Accepts<IFormFile>("multipart/form-data")
             .Produces<MercadoUploadResponse>(StatusCodes.Status202Accepted)
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/uploads", ListAsync)
             .WithName("ListMercadoUploads")
             .Produces<IReadOnlyList<MercadoCargaView>>();

        group.MapGet("/uploads/{id:guid}", GetByIdAsync)
             .WithName("GetMercadoUpload")
             .Produces<MercadoCargaView>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/cobertura", CoberturaAsync)
             .WithName("GetMercadoCobertura")
             .Produces<IReadOnlyList<MercadoCoberturaView>>();

        return app;
    }

    private static async Task<IResult> UploadAsync(
        IFormFile file,
        EngineDbContext db,
        IMinioClient minio,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1,
        [FromQuery] string? usuarioId = null)
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

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ValidationErrorResponse(
                ["O upload deve ser o relatório mensal da IQVIA em formato .xlsx."]));
        }

        // Validação superficial: é XLSX e tem a aba de dados. O contrato de colunas é
        // conferido pelo Worker, que é quem lê a planilha inteira — erro profundo chega
        // à tela pela MensagemErro da carga, com o cabeçalho ofensor no texto.
        await using (var validateStream = file.OpenReadStream())
        {
            var erros = MercadoUploadValidator.Validate(validateStream);
            if (erros.Count > 0)
            {
                return Results.BadRequest(new ValidationErrorResponse(erros));
            }
        }

        var carga = new MercadoCarga
        {
            Id = Guid.CreateVersion7(),
            RedeId = redeId,
            Status = MercadoCargaStatus.Pendente,
            DataAgendamento = DateTimeOffset.UtcNow,
            NomeArquivoOriginal = file.FileName,
            BlobKey = string.Empty,
            UsuarioId = usuarioId,
        };
        carga.BlobKey = $"{carga.Id}.xlsx";

        await ImportsEndpoints.EnsureBucketExistsAsync(minio, BucketName, ct);

        await using (var uploadStream = file.OpenReadStream())
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(BucketName)
                .WithObject(carga.BlobKey)
                .WithStreamData(uploadStream)
                .WithObjectSize(file.Length)
                .WithContentType("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
                ct);
        }

        db.MercadoCargas.Add(carga);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Carga de mercado {Id} enfileirada: arquivo={Arquivo} bytes={Bytes} rede={RedeId}",
            carga.Id, file.FileName, file.Length, redeId);

        return Results.Accepted(
            uri: $"/api/mercado/uploads/{carga.Id}",
            value: new MercadoUploadResponse(carga.Id, carga.Status.ToString(), carga.DataAgendamento));
    }

    private static async Task<IResult> ListAsync(
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1,
        [FromQuery] int take = 50)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var cargas = await db.MercadoCargas
            .AsNoTracking()
            .Where(c => c.RedeId == redeId)
            .OrderByDescending(c => c.DataAgendamento)
            .Take(Math.Clamp(take, 1, 200))
            .Select(ProjectToView)
            .ToListAsync(ct);

        return Results.Ok(cargas);
    }

    /// <summary>404 cobre inexistente e de outra rede — 403 confirmaria a existência a quem sonda.</summary>
    private static async Task<IResult> GetByIdAsync(
        Guid id,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var carga = await db.MercadoCargas
            .AsNoTracking()
            .Where(c => c.Id == id && c.RedeId == redeId)
            .Select(ProjectToView)
            .FirstOrDefaultAsync(ct);

        return carga is null ? Results.NotFound() : Results.Ok(carga);
    }

    /// <summary>
    /// O que está carregado, agregado do dado real (não do resumo das cargas): uma
    /// linha por (mês, brick) com contagem de observações. É a resposta que separa
    /// "zero" de "não coberto" para quem olha a tela.
    /// </summary>
    private static async Task<IResult> CoberturaAsync(
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var agregado = await CoberturaQuery(db, redeId).ToListAsync(ct);

        var cobertura = agregado
            .Select(l => new MercadoCoberturaView(l.Mes, l.Brick, l.Observacoes, l.Unidades))
            .ToList();

        return Results.Ok(cobertura);
    }

    /// <summary>
    /// A parte de <see cref="CoberturaAsync"/> que roda no banco. Separada para ser
    /// testável: <c>ToQueryString()</c> força a tradução sem abrir conexão, e é o único
    /// jeito barato de provar que ela é traduzível — o provider InMemory usado nos testes
    /// de modelo avalia tudo client-side e aceita consulta que o SQL Server recusa.
    ///
    /// <para>
    /// <b>A projeção usa inicializador de objeto, não construtor.</b> O EF Core não traduz
    /// <c>GroupBy(...).Select(g =&gt; new Record(a, b))</c> — a chamada de construtor num
    /// agrupamento compila e só estoura em runtime com "could not be translated". Foi
    /// exatamente esse o defeito: a tela ficava vazia com 175 mil linhas no banco. O record
    /// da resposta é montado depois, sobre o resultado já materializado.
    /// </para>
    /// </summary>
    internal static IQueryable<MercadoCoberturaLinha> CoberturaQuery(EngineDbContext db, int redeId) =>
        db.MercadoObservacoes
            .AsNoTracking()
            .Where(o => o.RedeId == redeId)
            .GroupBy(o => new { o.Mes, o.Brick })
            .Select(g => new MercadoCoberturaLinha
            {
                Mes = g.Key.Mes,
                Brick = g.Key.Brick,
                Observacoes = g.Count(),
                Unidades = g.Sum(o => o.Unidades),
            })
            .OrderByDescending(l => l.Mes).ThenBy(l => l.Brick);

    private static readonly System.Linq.Expressions.Expression<Func<MercadoCarga, MercadoCargaView>> ProjectToView =
        c => new MercadoCargaView(
            c.Id,
            c.Status.ToString(),
            c.DataAgendamento,
            c.DataInicioProcessamento,
            c.DataConclusao,
            c.NomeArquivoOriginal,
            c.MensagemErro,
            c.LinhasImportadas,
            c.ResumoJson);
}

internal sealed record MercadoUploadResponse(Guid Id, string Status, DateTimeOffset DataAgendamento);

internal sealed record MercadoCargaView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    string NomeArquivoOriginal,
    string? MensagemErro,
    long? LinhasImportadas,
    string? ResumoJson);

internal sealed record MercadoCoberturaView(
    DateOnly Mes,
    string Brick,
    int Observacoes,
    decimal Unidades);

/// <summary>
/// Linha da agregação como o SQL a devolve. Classe com propriedades atribuíveis, e não
/// record posicional, porque é essa a forma de projeção que o EF Core traduz dentro de um
/// <c>GroupBy</c> — ver <see cref="MercadoEndpoints.CoberturaQuery"/>.
/// </summary>
internal sealed class MercadoCoberturaLinha
{
    public DateOnly Mes { get; set; }
    public string Brick { get; set; } = "";
    public int Observacoes { get; set; }
    public decimal Unidades { get; set; }
}
