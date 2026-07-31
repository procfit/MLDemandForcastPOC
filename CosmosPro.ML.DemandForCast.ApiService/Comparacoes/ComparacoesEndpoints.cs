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
        [FromQuery] int redeId = 1,
        [FromQuery] string? usuarioId = null)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var sessao = await db.ComparacaoSessoes
            .Where(s => s.Id == id && s.RedeId == redeId)
            .FirstOrDefaultAsync(ct);
        if (sessao is null) return Results.NotFound();

        if (await SessaoConcorrenteAsync(db, redeId, id, ct) is { } emAndamento)
        {
            return Results.BadRequest(new ValidationErrorResponse([SessaoConcorrenteMensagem(emAndamento)]));
        }

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

        // A esta altura sessao.Status é sempre AguardandoDados (era de origem, ou
        // acabou de ser normalizado acima), e AguardandoDados -> ProcessandoDados é
        // sempre permitida (ComparacaoSessao.Permitidas) — sem segunda checagem a fazer.

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
            UsuarioId = usuarioId,
        };
        carga.BlobKey = $"{carga.Id}.zip";

        await ImportsEndpoints.EnsureBucketExistsAsync(minio, ImportsEndpoints.BucketName, ct);

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

    /// <summary>
    /// Outra sessão da <b>mesma rede</b> ainda viva numa das três fases, ou <c>null</c>.
    ///
    /// <para>
    /// <b>Por que só pode haver uma por vez:</b> o Stage é por rede e cada import o
    /// <b>substitui inteiro</b> (<c>CargaProcessor</c> apaga as tabelas da rede antes de
    /// carregar). Duas sessões em voo na mesma rede não competem por recurso — elas se
    /// destroem. Na melhor hipótese o segundo envio apaga a sugestão que a primeira ia
    /// comparar e a primeira morre culpando o ZIP dela, que estava certo. Na pior, a sugestão
    /// nova cai no mesmo dia e método da anterior e a primeira pontua <b>a sugestão da
    /// segunda</b> contra o próprio modelo, produzindo um número plausível a partir de dados
    /// que não combinam. Este é o motivo de a recusa existir: a corrupção silenciosa, não o
    /// desperdício de CPU.
    /// </para>
    ///
    /// <para>
    /// <b>Por que no envio e não na criação:</b> criar sessão não escreve nada no Stage — quem
    /// destrói é o import, e ele começa aqui. Bloquear a criação recusaria um ato inofensivo
    /// (dar nome a uma comparação e ler as instruções do extrator) e, pior, deixaria o botão
    /// "Nova comparação" quebrado por causa de uma sessão anterior travada. Este é também o
    /// único caminho por onde uma segunda sessão entra em voo, inclusive no reenvio a partir de
    /// <c>Inviavel</c>/<c>Falha</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Viva</b>, não apenas não terminal: o <c>SessaoWorker</c> toca <c>AtualizadoEm</c> a
    /// cada volta do claim, então uma sessão sem toque recente é uma cujo worker morreu. Sem
    /// esse recorte, um crash trancaria a rede para sempre e a mensagem mandaria o comprador
    /// esperar por algo que nunca termina. O relógio é o mesmo que encerra a fase abandonada
    /// (<see cref="ComparacaoSessao.LimiteDeFaseSemProgresso"/>), de propósito: quem solta o
    /// bloqueio e quem mata a sessão presa não podem discordar.
    /// </para>
    /// </summary>
    private static async Task<SessaoEmVoo?> SessaoConcorrenteAsync(
        EngineDbContext db, int redeId, Guid exceto, CancellationToken ct)
    {
        var vivaDesde = DateTimeOffset.UtcNow - ComparacaoSessao.LimiteDeFaseSemProgresso;

        // Comparações explícitas em vez de Contains numa coleção: Status tem value converter
        // para string, e a tradução de IN sobre propriedade convertida é onde o EF costuma
        // escorregar. Aqui o SQL gerado é o óbvio.
        return await db.ComparacaoSessoes
            .AsNoTracking()
            .Where(s => s.RedeId == redeId
                     && s.Id != exceto
                     && (s.Status == SessaoStatus.ProcessandoDados
                         || s.Status == SessaoStatus.Treinando
                         || s.Status == SessaoStatus.Comparando)
                     && s.AtualizadoEm >= vivaDesde)
            .OrderBy(s => s.CriadoEm)
            .Select(s => new SessaoEmVoo(s.Id, s.Nome, s.Status))
            .FirstOrDefaultAsync(ct);
    }

    private sealed record SessaoEmVoo(Guid Id, string? Nome, SessaoStatus Status);

    private static string SessaoConcorrenteMensagem(SessaoEmVoo emAndamento)
    {
        var nome = string.IsNullOrWhiteSpace(emAndamento.Nome)
            ? $"sem nome ({emAndamento.Id.ToString()[..8]})"
            : emAndamento.Nome;

        return $"A comparação \"{nome}\" ainda está em andamento — ela está {FaseLegivel(emAndamento.Status)} agora. " +
               "Cada envio substitui todos os dados importados desta rede, então começar outra agora apagaria os " +
               "dados que ela está usando e as duas terminariam erradas. Espere ela terminar e envie os dados desta " +
               "comparação depois.";
    }

    /// <summary>Fase em linguagem de comprador. O nome do enum não descreve nada para quem lê.</summary>
    private static string FaseLegivel(SessaoStatus status) => status switch
    {
        SessaoStatus.ProcessandoDados => "importando os dados enviados",
        SessaoStatus.Treinando => "aprendendo o padrão de venda das suas lojas",
        SessaoStatus.Comparando => "comparando os dois métodos",
        _ => "em andamento",
    };

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
