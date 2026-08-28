using System.Text.Json;
using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Engine.Mercado;
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

        group.MapDelete("/cobertura", ExcluirCoberturaAsync)
             .WithName("DeleteMercadoCobertura")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces<ValidationErrorResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/uploads/{id:guid}", ExcluirEnvioAsync)
             .WithName("DeleteMercadoUpload")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces<ValidationErrorResponse>(StatusCodes.Status409Conflict);

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
    /// <summary>
    /// Desfaz um envio: os recortes que ele declarou, os órfãos de catálogo/painel, o XLSX no
    /// MinIO e a própria linha do histórico.
    ///
    /// <para>
    /// <b>Existe por causa do import na rede errada.</b> O caso real que o motivou: o arquivo
    /// da IQVIA de uma rede foi enviado na outra. Excluir só os recortes deixaria, na rede
    /// errada, o painel de PDVs — que carrega os CNPJs das lojas da rede certa, dado
    /// identificável de outro inquilino — e a linha do envio visível na tela dela, com nome de
    /// arquivo e cobertura. Num sistema em que isolamento por rede é invariante (F10/F11),
    /// desfazer o engano tem de remover <b>tudo</b> que ele deixou, inclusive o rastro. É a
    /// exceção deliberada à regra de "histórico fica": o histórico dos imports do Stage
    /// descreve a própria rede; este descreveria a rede alheia.
    /// </para>
    ///
    /// <para>
    /// Os recortes removidos são o produto <c>Meses × Bricks</c> do resumo da carga — o
    /// arquivo real da IQVIA traz cada brick com todos os meses, então o produto é exato. Se
    /// um recorte já foi <b>recoberto</b> por envio mais novo, o que sai é o dado atual
    /// daquele recorte; o diálogo da tela avisa. Carga que falhou não tem resumo: sai só a
    /// linha e o blob.
    /// </para>
    /// </summary>
    private static async Task<IResult> ExcluirEnvioAsync(
        Guid id,
        EngineDbContext db,
        IMinioClient minio,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var carga = await db.MercadoCargas
            .FirstOrDefaultAsync(c => c.Id == id && c.RedeId == redeId, ct);
        if (carga is null) return Results.NotFound();

        if (carga.Status is MercadoCargaStatus.Pendente or MercadoCargaStatus.Processando)
        {
            return Results.Conflict(new ValidationErrorResponse([
                "Este envio ainda está na fila ou em processamento. Aguarde ele terminar " +
                "(ou falhar) para poder desfazê-lo."]));
        }

        var outroEmVoo = await db.MercadoCargas
            .AnyAsync(c => c.RedeId == redeId && c.Id != id
                && (c.Status == MercadoCargaStatus.Pendente || c.Status == MercadoCargaStatus.Processando), ct);
        if (outroEmVoo)
        {
            return Results.Conflict(new ValidationErrorResponse([
                "Há outro envio de dados de mercado em processamento nesta rede. Aguarde ele " +
                "terminar e tente de novo — excluir durante a gravação deixaria o mês pela metade."]));
        }

        var resumo = carga.ResumoJson is null
            ? null
            : JsonSerializer.Deserialize<MercadoCargaResumo>(carga.ResumoJson, JsonWeb);

        var observacoesRemovidas = 0;
        if (resumo is not null)
        {
            foreach (var mes in resumo.Meses)
            {
                foreach (var brick in resumo.Bricks)
                {
                    observacoesRemovidas += await db.MercadoObservacoes
                        .Where(o => o.RedeId == redeId && o.Mes == mes && o.Brick == brick)
                        .ExecuteDeleteAsync(ct);
                }
            }

            await VarrerCatalogoOrfaoAsync(db, redeId, ct);
        }

        // Blob antes da linha: se a remoção do MinIO falhar, a carga continua apontando para
        // ele e a operação pode ser repetida. A ordem inversa deixaria um blob órfão sem
        // nenhum registro de que existe.
        try
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs()
                .WithBucket(BucketName)
                .WithObject(carga.BlobKey), ct);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            // Já não existe — nada a desfazer desse lado.
        }
        catch (Minio.Exceptions.BucketNotFoundException)
        {
        }

        db.MercadoCargas.Remove(carga);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Mercado: envio {CargaId} desfeito na rede {RedeId} — {Obs} observação(ões), blob e histórico removidos.",
            id, redeId, observacoesRemovidas);
        return Results.NoContent();
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Remove do catálogo (<c>MercadoProdutos</c>) e do painel (<c>MercadoBrickPdvs</c>) o que
    /// nenhuma observação restante da rede referencia.
    ///
    /// <para>
    /// Roda depois de <b>toda</b> exclusão de observações, e é o que garante as duas pontas ao
    /// mesmo tempo: enquanto algum mês do brick existir, o painel e os EANs dele ficam (os
    /// meses restantes os usam); quando o último recorte sai, nada daquele arquivo sobra na
    /// rede — que é a exigência do caso "importei na rede errada".
    /// </para>
    /// </summary>
    private static async Task VarrerCatalogoOrfaoAsync(EngineDbContext db, int redeId, CancellationToken ct)
    {
        await db.MercadoProdutos
            .Where(p => p.RedeId == redeId
                && !db.MercadoObservacoes.Any(o => o.RedeId == redeId && o.Ean == p.Ean))
            .ExecuteDeleteAsync(ct);

        await db.MercadoBrickPdvs
            .Where(p => p.RedeId == redeId
                && !db.MercadoObservacoes.Any(o => o.RedeId == redeId && o.Brick == p.Brick))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Apaga as observações de mercado de <b>uma célula de cobertura</b> — (mês, brick) — da
    /// rede do chamador.
    ///
    /// <para>
    /// A célula é a única unidade de exclusão que existe: as observações não guardam de qual
    /// envio vieram (de propósito — a recarga substitui por (mês, brick), então "o que este
    /// envio carregou" deixa de ser rastreável no instante em que outro arquivo cobre o mesmo
    /// recorte). Excluir "por carga" prometeria uma granularidade que o modelo não tem.
    /// </para>
    ///
    /// <para>
    /// <b>Saem as observações e o que ficar órfão.</b> <c>MercadoProdutos</c> e
    /// <c>MercadoBrickPdvs</c> são compartilhados pelos meses do brick, então ficam enquanto
    /// alguma observação restante os referenciar — e são varridos quando a última sai
    /// (<see cref="VarrerCatalogoOrfaoAsync"/>). A <c>MercadoCarga</c> e o XLSX no MinIO
    /// ficam: para desfazer um envio inteiro, rastro incluído, use o DELETE de
    /// <c>/uploads/{id}</c>.
    /// </para>
    ///
    /// <para>
    /// <b>409 com carga em voo:</b> o <c>MercadoProcessor</c> escreve numa transação própria,
    /// e um DELETE concorrente ao bulk dele terminaria em célula meio-cheia ou em deadlock —
    /// com a mensagem culpando quem clicou. A recusa usa o mesmo desenho do bloqueio de sessão:
    /// espera-se a fila esvaziar, não se compete com ela.
    /// </para>
    ///
    /// <para>
    /// 404 quando a célula não existe <b>nesta</b> rede — o que cobre também a sonda com
    /// célula de outro inquilino, pela mesma regra dos demais endpoints (403 confirmaria a
    /// existência).
    /// </para>
    /// </summary>
    private static async Task<IResult> ExcluirCoberturaAsync(
        EngineDbContext db,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] DateOnly mes,
        [FromQuery] string brick = "",
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        if (string.IsNullOrWhiteSpace(brick))
        {
            return Results.BadRequest(new ValidationErrorResponse(["Informe o brick da célula a excluir."]));
        }

        var emVoo = await db.MercadoCargas
            .AnyAsync(c => c.RedeId == redeId
                && (c.Status == MercadoCargaStatus.Pendente || c.Status == MercadoCargaStatus.Processando), ct);
        if (emVoo)
        {
            return Results.Conflict(new ValidationErrorResponse([
                "Há um envio de dados de mercado em processamento nesta rede. Aguarde ele " +
                "terminar e tente de novo — excluir durante a gravação deixaria o mês pela metade."]));
        }

        var removidas = await db.MercadoObservacoes
            .Where(o => o.RedeId == redeId && o.Mes == mes && o.Brick == brick)
            .ExecuteDeleteAsync(ct);

        if (removidas == 0) return Results.NotFound();

        await VarrerCatalogoOrfaoAsync(db, redeId, ct);

        logger.LogInformation(
            "Mercado: {N} observação(ões) de {Mes:yyyy-MM} / brick '{Brick}' excluídas da rede {RedeId}.",
            removidas, mes, brick, redeId);
        return Results.NoContent();
    }

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
