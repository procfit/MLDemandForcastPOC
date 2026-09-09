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

        app.MapGet("/api/comparacoes/avaliacoes", TabulacaoAsync)
           .WithName("GetAvaliacoesTabuladas")
           .WithTags("Questionarios")
           .Produces<TabulacaoView>();

        return app;
    }

    /// <summary>
    /// Uma linha por execução avaliada, com a seção G e as respostas do questionário lado a
    /// lado — os dados brutos que quem conduz a pesquisa tabula no Excel.
    ///
    /// <para>
    /// <b>A chave é a execução, nunca o comprador</b> (exigência explícita do patrocinador). O
    /// mesmo comprador avalia várias execuções, e consolidar por ele apagaria justamente a
    /// variação que a pesquisa mede. Isso já é garantido pelo banco
    /// (<c>UQ_Questionarios_SessaoId</c>); esta consulta parte da sessão para que a garantia
    /// apareça também no formato.
    /// </para>
    ///
    /// <para>
    /// <b>Traz sessão sem avaliação e sem questionário também.</b> Filtrar só as respondidas
    /// esconderia o denominador: 12 avaliações não dizem nada sem as 30 execuções de onde
    /// saíram. Quem exporta decide o que descartar.
    /// </para>
    ///
    /// <para>
    /// <b><c>VersaoCatalogo</c> na linha não é enfeite.</b> O instrumento já foi renumerado duas
    /// vezes, e o código B7 designou afirmação diferente em cada versão. Sem esta coluna a
    /// planilha soma perguntas distintas na mesma coluna, sem erro nenhum e sem sintoma. É a
    /// única coisa que este endpoint acrescenta ao que foi pedido.
    /// </para>
    /// </summary>
    private static async Task<IResult> TabulacaoAsync(
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var sessoes = await db.ComparacaoSessoes
            .AsNoTracking()
            .Where(s => s.RedeId == redeId)
            .OrderByDescending(s => s.CriadoEm)
            .Select(s => new
            {
                s.Id,
                s.CriadoEm,
                s.Status,
                s.SugestaoId,
                s.SugestaoDescricao,
                s.AvaliacaoVeredito,
                s.AvaliacaoComentario,
                s.AvaliacaoEm,
                s.AvaliacaoUsuarioId,
            })
            .ToListAsync(ct);

        if (sessoes.Count == 0) return Results.Ok(new TabulacaoView(redeId, [], [], []));

        var ids = sessoes.Select(s => s.Id).ToList();

        // Cabeçalho e respostas em duas idas, e não numa junção só: a junção repetiria o
        // cabeçalho em cada resposta (onze linhas por execução) e o custo de montar passaria a
        // crescer com o produto em vez de com a soma.
        var questionarios = await db.Questionarios
            .AsNoTracking()
            .Where(q => ids.Contains(q.SessaoId))
            .Select(q => new { q.Id, q.SessaoId, q.UsuarioId, q.VersaoCatalogo, q.EnviadoEm })
            .ToListAsync(ct);

        var porSessao = questionarios.ToDictionary(q => q.SessaoId);
        var qids = questionarios.Select(q => q.Id).ToList();

        var respostas = (await db.QuestionarioRespostas
                .AsNoTracking()
                .Where(r => qids.Contains(r.QuestionarioId))
                .Select(r => new
                {
                    r.QuestionarioId,
                    r.PerguntaCodigo,
                    r.PerguntaTexto,
                    r.OpcaoTexto,
                    r.OpcaoValor,
                    r.TextoLivre,
                })
                .ToListAsync(ct))
            .GroupBy(r => r.QuestionarioId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Identidade resolvida em bloco. O e-mail é o que identifica a pessoa para quem tabula;
        // o Guid sozinho não serve a ninguém numa planilha.
        var usuarioIds = questionarios.Select(q => q.UsuarioId.ToString())
            .Concat(sessoes.Select(s => s.AvaliacaoUsuarioId).Where(x => x is not null)!)
            .Distinct()
            .ToList();

        var porUsuario = (await db.Users
                .AsNoTracking()
                .Where(u => usuarioIds.Contains(u.Id.ToString()))
                .Select(u => new { Id = u.Id.ToString(), u.Email, u.NomeCompleto })
                .ToListAsync(ct))
            .ToDictionary(u => u.Id, u => u.Email ?? u.NomeCompleto ?? u.Id);

        string? Quem(string? id) => id is null ? null : porUsuario.GetValueOrDefault(id, id);

        var linhas = sessoes.Select(s =>
        {
            porSessao.TryGetValue(s.Id, out var q);

            IReadOnlyList<RespostaTabuladaView> respostasDaLinha = q is null
                ? []
                : [.. respostas.GetValueOrDefault(q.Id, [])
                    .Select(r => new RespostaTabuladaView(
                        r.PerguntaCodigo, r.PerguntaTexto, r.OpcaoTexto, r.OpcaoValor,
                        r.TextoLivre))];

            return new AvaliacaoTabuladaView(
                s.Id,
                s.CriadoEm,
                s.Status.ToString(),
                s.SugestaoId,
                s.SugestaoDescricao,
                s.AvaliacaoVeredito,
                s.AvaliacaoComentario,
                s.AvaliacaoEm,
                Quem(s.AvaliacaoUsuarioId),
                q?.EnviadoEm,
                q?.VersaoCatalogo,
                q is null ? null : Quem(q.UsuarioId.ToString()),
                respostasDaLinha);
        }).ToList();

        // Colunas na ordem do catálogo ATUAL, e depois os códigos que só existem em respostas de
        // versões anteriores — sem isso uma pergunta que saiu do instrumento desapareceria da
        // planilha junto com as respostas que alguém de fato deu a ela.
        var doCatalogo = QuestionarioCatalogo.Perguntas.Select(p => p.Codigo).ToList();
        var extras = linhas
            .SelectMany(l => l.Respostas.Select(r => r.PerguntaCodigo))
            .Distinct()
            .Where(c => !doCatalogo.Contains(c))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Quais códigos a planilha exporta como texto. Sai do catálogo, e não de heurística
        // sobre o dado: ver PerguntaDef.TabularTexto.
        var comoTexto = QuestionarioCatalogo.Perguntas
            .Where(p => p.TabularTexto)
            .Select(p => p.Codigo)
            .ToList();

        return Results.Ok(new TabulacaoView(
            redeId, [.. doCatalogo, .. extras], comoTexto, linhas));
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

/// <param name="Codigos">
/// Ordem das colunas de resposta, para a tela e a planilha não decidirem cada uma a sua. Começa
/// na ordem do catálogo atual e termina nos códigos que só aparecem em respostas de versões
/// anteriores do instrumento.
/// </param>
/// <param name="CodigosDeTexto">
/// Códigos cuja célula traz o texto da opção em vez do número da escala. Vem do catálogo
/// (<c>PerguntaDef.TabularTexto</c>) para tela e planilha não divergirem — e para a decisão não
/// voltar a ser deduzida do formato do dado, que foi o que errou o A2.
/// </param>
internal sealed record TabulacaoView(
    int RedeId,
    IReadOnlyList<string> Codigos,
    IReadOnlyList<string> CodigosDeTexto,
    IReadOnlyList<AvaliacaoTabuladaView> Linhas);

/// <param name="VersaoCatalogo">
/// Versão do instrumento sob a qual esta linha foi respondida. <b>Sem ela a planilha soma
/// perguntas diferentes na mesma coluna</b>: o código B7 já designou três afirmações distintas
/// (V2, V5, V6). Nulo quando não há questionário.
/// </param>
internal sealed record AvaliacaoTabuladaView(
    Guid SessaoId,
    DateTimeOffset CriadoEm,
    string Status,
    long? SugestaoId,
    string? SugestaoDescricao,
    string? AvaliacaoVeredito,
    string? AvaliacaoComentario,
    DateTimeOffset? AvaliacaoEm,
    string? Avaliador,
    DateTimeOffset? QuestionarioEnviadoEm,
    int? VersaoCatalogo,
    string? Respondente,
    IReadOnlyList<RespostaTabuladaView> Respostas);

/// <param name="OpcaoValor">
/// Posição na escala (1 a 5) em pergunta ordinal. <b>Nulo significa "esta pergunta não é
/// ordinal"</b> — a de função e a de tempo de experiência são nominais, e é por isso que a
/// planilha exporta o texto nelas e o número nas afirmações da Parte B, exatamente como pedido.
/// </param>
internal sealed record RespostaTabuladaView(
    string PerguntaCodigo,
    /// <summary>
    /// Retrato do enunciado como foi exibido. Viaja junto porque e a UNICA forma de
    /// interpretar resposta de versao anterior do instrumento: o codigo B7 designou tres
    /// afirmacoes diferentes, e o catalogo atual so conhece a ultima.
    /// </summary>
    string PerguntaTexto,
    string OpcaoTexto,
    int? OpcaoValor,
    string? TextoLivre);
