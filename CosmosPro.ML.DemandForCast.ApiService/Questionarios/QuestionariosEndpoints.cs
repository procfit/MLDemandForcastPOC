using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Engine.Questionarios;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.Questionarios;

/// <summary>
/// O questionário da última fase da sessão. Aninhado em <c>/api/comparacoes/{sessaoId}</c>
/// porque não existe fora dela — o catálogo é a exceção, já que é estático e igual para todos.
/// </summary>
internal static class QuestionariosEndpoints
{
    public static IEndpointRouteBuilder MapQuestionariosEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/questionarios/catalogo", Catalogo)
           .WithName("GetQuestionarioCatalogo")
           .WithTags("Questionarios")
           .Produces<CatalogoView>();

        var group = app.MapGroup("/api/comparacoes/{sessaoId:guid}/questionario")
                       .WithTags("Questionarios");

        group.MapGet("/", GetAsync)
             .WithName("GetQuestionario")
             .Produces<QuestionarioView>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/", SalvarAsync)
             .WithName("SalvarQuestionarioRascunho")
             .Produces<QuestionarioView>()
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .Produces<ValidationErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/enviar", EnviarAsync)
             .WithName("EnviarQuestionario")
             .Produces<QuestionarioView>()
             .Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .Produces<ValidationErrorResponse>(StatusCodes.Status409Conflict);

        return app;
    }

    private static IResult Catalogo() => Results.Ok(
        new CatalogoView(
            QuestionarioCatalogo.Versao,
            [.. QuestionarioCatalogo.Secoes.Select(s => new SecaoView(
                s.Titulo,
                s.Descricao,
                [.. s.Perguntas.Select(p => new PerguntaView(
                    p.Codigo,
                    p.Texto,
                    p.Obrigatoria,
                    [.. p.Opcoes.Select(o => new OpcaoView(
                        o.Codigo, o.Texto, o.Valor, o.PermiteTextoLivre))]))]))]));

    private static async Task<IResult> GetAsync(
        Guid sessaoId,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        if (await SessaoAsync(db, sessaoId, redeId, ct) is not { } sessao) return Results.NotFound();

        return Results.Ok(await MontarAsync(db, sessaoId, sessao.Status, ct));
    }

    // `usuarioId` é obrigatório, e NÃO pode ganhar `= default`. Um `Guid` opcional com valor
    // default explode o endpoint inteiro: o compilador não grava constante de Guid em
    // metadata, então a reflexão devolve `null` como default e o RequestDelegateFactory monta
    // `Expression.Constant(null, typeof(Guid))` — "Argument types do not match". Pior, a tabela
    // de rotas é construída inteira na primeira requisição, então um handler inválido derruba
    // *todos* os endpoints da app, inclusive `/health`; o efeito visível é o apiservice nunca
    // ficar saudável e o AppHost não subir. Se algum chamador legítimo puder não ter usuário,
    // use `Guid?` — nunca `= default`.
    private static async Task<IResult> SalvarAsync(
        Guid sessaoId,
        [FromBody] SalvarQuestionarioRequest req,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] Guid usuarioId,
        [FromQuery] int redeId = 1)
        => await GravarAsync(sessaoId, req.PassoAtual, req.Respostas, selar: false,
                             db, redeId, usuarioId, ct);

    /// <inheritdoc cref="SalvarAsync"/>
    private static async Task<IResult> EnviarAsync(
        Guid sessaoId,
        [FromBody] EnviarQuestionarioRequest req,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] Guid usuarioId,
        [FromQuery] int redeId = 1)
        => await GravarAsync(sessaoId, req.PassoAtual, req.Respostas, selar: true,
                             db, redeId, usuarioId, ct);

    /// <summary>
    /// Rascunho e envio compartilham quase tudo — as mesmas guardas, o mesmo <i>upsert</i> — e
    /// diferem em duas coisas: o envio exige completude e move a sessão para
    /// <see cref="SessaoStatus.Concluida"/>. Duplicar o caminho deixaria as guardas divergirem
    /// com o tempo, e é justamente a guarda que impede gravar sobre uma avaliação já selada.
    /// </summary>
    private static async Task<IResult> GravarAsync(
        Guid sessaoId,
        int passoAtual,
        IReadOnlyList<RespostaRequest>? respostas,
        bool selar,
        EngineDbContext db,
        int redeId,
        Guid usuarioId,
        CancellationToken ct)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        if (await SessaoAsync(db, sessaoId, redeId, ct) is not { } sessao) return Results.NotFound();

        if (sessao.Status != SessaoStatus.AguardandoQuestionario)
        {
            // Duas recusas com mensagens diferentes: "já foi" e "ainda não" mandam o chamador
            // para lados opostos, e um texto genérico faria o comprador tentar de novo no caso
            // em que nada vai mudar.
            return Results.Conflict(new ValidationErrorResponse(
                sessao.Status == SessaoStatus.Concluida
                    ? ["Esta comparação já foi avaliada. As respostas enviadas não podem ser alteradas."]
                    : [$"Esta comparação está em '{sessao.Status}' e ainda não pode ser avaliada: " +
                       "o questionário abre quando o resultado fica pronto."]));
        }

        var informadas = (respostas ?? [])
            .Select(r => new RespostaInformada(r.PerguntaCodigo, r.OpcaoCodigo, Limpar(r.TextoLivre)))
            .ToList();

        if (QuestionarioValidator.Conferir(informadas) is { Count: > 0 } erros)
            return Results.BadRequest(new ValidationErrorResponse(erros));

        if (selar && QuestionarioValidator.ObrigatoriasFaltando(informadas) is { Count: > 0 } faltando)
        {
            return Results.BadRequest(new ValidationErrorResponse(
                [.. faltando.Select(c => $"A pergunta '{c}' precisa ser respondida antes de enviar.")]));
        }

        var agora = DateTimeOffset.UtcNow;

        // A escrita inteira vai dentro da estratégia de execução, e isso NÃO é opcional:
        // `AddSqlServerDbContext` (Aspire) liga `EnableRetryOnFailure`, e sob uma estratégia de
        // retry o EF **recusa** transação iniciada pelo usuário — `BeginTransactionAsync` lança,
        // e o endpoint responde 500. As outras duas transações do repositório
        // (`CargaProcessor`, `SessaoResultadoMaterializador`) escapam disso por abrirem
        // `SqlConnection` própria, fora do contexto EF; aqui o trabalho é todo de entidade, então
        // o caminho é envolver a unidade retentável.
        //
        // `ChangeTracker.Clear()` na entrada é o que torna a retentativa segura: uma tentativa
        // que rolou atrás deixa as entidades dela rastreadas como Added, e a volta seguinte
        // reconsultaria o banco (sem vê-las), adicionaria um segundo `Questionario` e morreria no
        // índice único de `SessaoId` — transformando uma falha transitória em erro permanente.
        var estrategia = db.Database.CreateExecutionStrategy();

        var recusa = await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            return await EscreverAsync(db, sessaoId, redeId, usuarioId, passoAtual, informadas, selar, agora, ct);
        });

        if (recusa is not null) return recusa;

        var status = selar ? SessaoStatus.Concluida : SessaoStatus.AguardandoQuestionario;
        return Results.Ok(await MontarAsync(db, sessaoId, status, ct));
    }

    /// <summary>
    /// O corpo transacional: substitui as respostas e, no envio, sela e conclui a sessão.
    /// Devolve <c>null</c> em sucesso, ou o <c>IResult</c> da recusa. Roda dentro da estratégia
    /// de execução de <see cref="GravarAsync"/> — ver a nota de lá antes de mexer.
    /// </summary>
    private static async Task<IResult?> EscreverAsync(
        EngineDbContext db,
        Guid sessaoId,
        int redeId,
        Guid usuarioId,
        int passoAtual,
        List<RespostaInformada> informadas,
        bool selar,
        DateTimeOffset agora,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var questionario = await db.Questionarios.FirstOrDefaultAsync(q => q.SessaoId == sessaoId, ct);
        if (questionario is null)
        {
            questionario = new Questionario
            {
                Id = Guid.CreateVersion7(),
                RedeId = redeId,
                SessaoId = sessaoId,
                UsuarioId = usuarioId,
                VersaoCatalogo = QuestionarioCatalogo.Versao,
                CriadoEm = agora,
                AtualizadoEm = agora,
            };
            db.Questionarios.Add(questionario);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            questionario.AtualizadoEm = agora;
            // UsuarioId acompanha quem gravou por último de propósito: é auditoria de "quem
            // respondeu", e quem enviou é mais informativo que quem abriu o rascunho.
            questionario.UsuarioId = usuarioId;
        }

        questionario.PassoAtual = passoAtual;

        // Substitui o conjunto inteiro em vez de casar resposta por resposta: é o que torna o
        // PUT idempotente e o que permite desmarcar — a tela manda o estado completo do wizard,
        // então uma pergunta ausente do corpo significa "sem resposta", não "mantenha a antiga".
        await db.QuestionarioRespostas
            .Where(r => r.QuestionarioId == questionario.Id)
            .ExecuteDeleteAsync(ct);

        foreach (var r in informadas)
        {
            var pergunta = QuestionarioCatalogo.Pergunta(r.PerguntaCodigo)!;
            var opcao = pergunta.Opcao(r.OpcaoCodigo)!;

            db.QuestionarioRespostas.Add(new QuestionarioResposta
            {
                QuestionarioId = questionario.Id,
                PerguntaCodigo = pergunta.Codigo,
                PerguntaTexto = pergunta.Texto,
                OpcaoCodigo = opcao.Codigo,
                OpcaoTexto = opcao.Texto,
                OpcaoValor = opcao.Valor,
                TextoLivre = r.TextoLivre,
            });
        }

        if (selar)
        {
            questionario.EnviadoEm = agora;

            var (total, comMl) = await ContarItensAsync(db, sessaoId, ct);
            questionario.TotalDeItens = total;
            questionario.ItensComDecisaoMl = comMl;
        }

        await db.SaveChangesAsync(ct);

        if (selar)
        {
            // A transição por último e condicional, no mesmo padrão do
            // SessaoResultadoMaterializador: se dois envios chegarem juntos, só um encontra a
            // sessão em AguardandoQuestionario. O outro acha zero linha, a transação inteira
            // volta atrás e as respostas que ele gravou desaparecem com ela — em vez de dois
            // envios se sobrescreverem com a sessão concluída uma vez só.
            var linhas = await db.ComparacaoSessoes
                .Where(s => s.Id == sessaoId && s.Status == SessaoStatus.AguardandoQuestionario)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, SessaoStatus.Concluida)
                    .SetProperty(x => x.AtualizadoEm, agora), ct);

            if (linhas == 0)
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new ValidationErrorResponse(
                    ["Esta comparação já foi avaliada. As respostas enviadas não podem ser alteradas."]));
            }
        }

        await tx.CommitAsync(ct);
        return null;
    }

    /// <summary>
    /// Quantos itens a comparação avaliada tinha e em quantos o braço de ML decidiu — contados
    /// da tabela materializada, não do <c>ResultadoJson</c>, para não acoplar o selo ao formato
    /// do payload da manchete.
    ///
    /// <para>
    /// Sem item materializado os dois saem nulos, e não zero: zero afirmaria "a comparação não
    /// tinha item nenhum", quando o que houve foi não ter o que contar. Ver a nota de
    /// <see cref="Questionario.ItensComDecisaoMl"/>.
    /// </para>
    /// </summary>
    private static async Task<(int? Total, int? ComMl)> ContarItensAsync(
        EngineDbContext db, Guid sessaoId, CancellationToken ct)
    {
        var contagem = await db.ComparacaoSessaoItens
            .Where(i => i.SessaoId == sessaoId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                ComMl = g.Count(i => i.CompraSugeridaMl != null),
            })
            .FirstOrDefaultAsync(ct);

        return contagem is null ? (null, null) : (contagem.Total, contagem.ComMl);
    }

    /// <summary>
    /// A sessão, já filtrada pelo inquilino. <c>null</c> tanto para inexistente quanto para
    /// sessão de outra rede, e quem chama responde <b>404 nos dois casos</b>: um 403 confirmaria
    /// a quem sondasse que a sessão existe em outro inquilino.
    /// </summary>
    private static async Task<SessaoEscopo?> SessaoAsync(
        EngineDbContext db, Guid sessaoId, int redeId, CancellationToken ct)
        => await db.ComparacaoSessoes
            .AsNoTracking()
            .Where(s => s.Id == sessaoId && s.RedeId == redeId)
            .Select(s => new SessaoEscopo(s.Status))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// <c>QuestionarioRespostas</c> não tem <c>RedeId</c> — o escopo é transitivo pela FK. A
    /// junção com o pai vai no mesmo round-trip, e o pai já veio filtrado por rede em
    /// <see cref="SessaoAsync"/>: consultar por <c>SessaoId</c> solto entregaria a avaliação de
    /// um inquilino a quem acertasse um Guid.
    /// </summary>
    private static async Task<QuestionarioView> MontarAsync(
        EngineDbContext db, Guid sessaoId, SessaoStatus status, CancellationToken ct)
    {
        var cabecalho = await db.Questionarios
            .AsNoTracking()
            .Where(q => q.SessaoId == sessaoId)
            .Select(q => new { q.Id, q.PassoAtual, q.VersaoCatalogo, q.EnviadoEm })
            .FirstOrDefaultAsync(ct);

        if (cabecalho is null)
        {
            return new QuestionarioView(
                null, status.ToString(), null, 0, QuestionarioCatalogo.Versao, []);
        }

        var respostas = await db.QuestionarioRespostas
            .AsNoTracking()
            .Where(r => r.QuestionarioId == cabecalho.Id)
            .Select(r => new RespostaView(
                r.PerguntaCodigo, r.OpcaoCodigo, r.OpcaoValor, r.TextoLivre))
            .ToListAsync(ct);

        return new QuestionarioView(
            cabecalho.Id,
            status.ToString(),
            cabecalho.EnviadoEm,
            cabecalho.PassoAtual,
            cabecalho.VersaoCatalogo,
            respostas);
    }

    /// <summary>
    /// Texto livre em branco é ausência, não string vazia: sem isto o comprador que abre o campo
    /// e não escreve nada grava <c>""</c>, que na análise vira "respondeu e não disse nada" em
    /// vez de "não respondeu".
    /// </summary>
    private static string? Limpar(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private sealed record SessaoEscopo(SessaoStatus Status);
}

internal sealed record RespostaRequest(string PerguntaCodigo, string OpcaoCodigo, string? TextoLivre);

internal sealed record SalvarQuestionarioRequest(int PassoAtual, IReadOnlyList<RespostaRequest>? Respostas);

internal sealed record EnviarQuestionarioRequest(int PassoAtual, IReadOnlyList<RespostaRequest>? Respostas);

internal sealed record CatalogoView(int Versao, IReadOnlyList<SecaoView> Secoes);

internal sealed record SecaoView(string Titulo, string? Descricao, IReadOnlyList<PerguntaView> Perguntas);

internal sealed record PerguntaView(
    string Codigo, string Texto, bool Obrigatoria, IReadOnlyList<OpcaoView> Opcoes);

internal sealed record OpcaoView(string Codigo, string Texto, int? Valor, bool PermiteTextoLivre);

/// <param name="Id">
/// <c>null</c> quando ainda não há rascunho — a tela desenha o wizard vazio pelo mesmo caminho,
/// em vez de tratar "sem questionário" como erro.
/// </param>
/// <param name="SessaoStatus">
/// Viaja aqui, e não só no GET da sessão, para a tela decidir modo leitura sem depender de duas
/// respostas que podem discordar entre si.
/// </param>
internal sealed record QuestionarioView(
    Guid? Id,
    string SessaoStatus,
    DateTimeOffset? EnviadoEm,
    int PassoAtual,
    int VersaoCatalogo,
    IReadOnlyList<RespostaView> Respostas);

internal sealed record RespostaView(
    string PerguntaCodigo, string OpcaoCodigo, int? OpcaoValor, string? TextoLivre);
