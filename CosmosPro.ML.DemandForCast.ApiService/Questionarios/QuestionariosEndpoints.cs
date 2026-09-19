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

        // A ROTA NAO TEM MAIS SESSAO. O questionario passou a ser do comprador
        // (19/09/2026): um por pessoa, sobre a experiencia acumulada em varias execucoes.
        // Mante-la sob /api/comparacoes/{id} faria a URL afirmar um vinculo que nao existe
        // mais, e obrigaria a tela a escolher arbitrariamente uma execucao para citar.
        var group = app.MapGroup("/api/questionario").WithTags("Questionarios");

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
    /// <summary>
    /// Quantas execucoes este comprador ja avaliou, e se o questionario esta liberado.
    ///
    /// <para>
    /// Conta sessao com a <b>Secao G respondida por ele</b>, e nao sessao existente: o
    /// patrocinador exigiu as duas coisas -- realizar a execucao e avalia-la.
    /// </para>
    ///
    /// <para>
    /// <c>AvaliacaoUsuarioId</c> e <c>string</c> e o parametro e <c>Guid</c>: a coluna guarda
    /// o <c>ToString()</c> desde que nasceu. A conversao acontece aqui, uma vez, e nao dentro
    /// da consulta -- traduzir <c>Guid</c> para texto no SQL dependeria do formato que o
    /// provider escolhesse, e a contagem erraria <b>em silencio</b> se ele mudasse.
    /// </para>
    /// </summary>
    private static async Task<Portao> PortaoAsync(
        EngineDbContext db, Guid usuarioId, int redeId, CancellationToken ct)
    {
        var chave = usuarioId.ToString();

        var avaliadas = await db.ComparacaoSessoes
            .CountAsync(x => x.RedeId == redeId
                          && x.AvaliacaoUsuarioId == chave
                          && x.AvaliacaoVeredito != null, ct);

        var enviadoEm = await db.Questionarios
            .Where(q => q.UsuarioId == usuarioId)
            .Select(q => q.EnviadoEm)
            .FirstOrDefaultAsync(ct);

        return new Portao(avaliadas, QuestionarioCatalogo.MinimoDeExecucoes, enviadoEm);
    }

    /// <param name="ExecucoesAvaliadas">Execucoes com Secao G respondida por este comprador.</param>
    /// <param name="EnviadoEm">Nulo enquanto ele nao enviou. Carimbo, nao situacao.</param>
    private sealed record Portao(int ExecucoesAvaliadas, int MinimoExigido, DateTimeOffset? EnviadoEm)
    {
        public bool Liberado => ExecucoesAvaliadas >= MinimoExigido;
    }

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

        // QUESTIONARIO NAO E MAIS POR EXECUCAO, entao a busca e por REDE e nao por sessao.
        // Antes havia um `ids.Contains(q.SessaoId)` aqui; hoje isso nao existe, e e por isso
        // que a tabulacao devolve DOIS blocos em vez de uma linha por execucao carregando as
        // respostas: repeti-las em cada execucao do mesmo comprador faria a planilha parecer
        // ter N questionarios onde ha um.
        var questionarios = await db.Questionarios
            .AsNoTracking()
            .Where(q => q.RedeId == redeId)
            .Select(q => new { q.Id, q.UsuarioId, q.VersaoCatalogo, q.EnviadoEm })
            .ToListAsync(ct);

        if (sessoes.Count == 0 && questionarios.Count == 0)
            return Results.Ok(new TabulacaoView(redeId, [], [], 0, [], []));

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

        // PSEUDÔNIMO, e a identidade NUNCA SAI DO BANCO. Antes esta consulta lia
        // AspNetUsers e devolvia o e-mail; agora não há consulta a usuário nenhuma, e o que
        // viaja é P01, P02, P03… Isso é mais forte que anonimizar depois: o dado identificável
        // não passa pela API, pela tela, pela planilha nem por este processo.
        //
        // Foi a solução do patrocinador para o impasse do consentimento (07/09/2026): ele
        // precisa contar quantas pessoas responderam e quantas execuções cada uma avaliou, e
        // não pode identificar ninguém. Um código estável por participante entrega as duas
        // coisas — a contagem sai de valores distintos, e a repetição sai de agrupar o código.
        //
        // A ORDEM É A DA PRIMEIRA APARIÇÃO, com o id como desempate para ser determinística.
        // Consequência que vale saber: o código é estável porque as execuções são criadas
        // sempre "agora", então participante novo recebe o número seguinte e os existentes não
        // se mexem. Um dado retroativo — execução gravada com data anterior à de alguém que já
        // tem número — deslocaria a numeração dali para frente.
        //
        // A numeração é POR REDE, porque este endpoint é escopado por inquilino e não pode
        // enxergar outro. Duas redes têm cada uma o seu P01: o que identifica o participante na
        // análise é o par (Rede, código), e a coluna Rede vai na planilha justamente por isso.
        var primeiraAparicao = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var s in sessoes)
        {
            void Marcar(string? id)
            {
                if (id is null) return;
                if (!primeiraAparicao.TryGetValue(id, out var atual) || s.CriadoEm < atual)
                {
                    primeiraAparicao[id] = s.CriadoEm;
                }
            }

            Marcar(s.AvaliacaoUsuarioId);
        }

        // Quem respondeu o questionario mas nunca avaliou execucao nenhuma tambem precisa de
        // codigo. Entra depois das execucoes, entao recebe numero mais alto -- a ordem de
        // primeira aparicao continua sendo a das execucoes, que e a que o patrocinador le.
        foreach (var q in questionarios.OrderBy(q => q.EnviadoEm ?? DateTimeOffset.MaxValue))
        {
            var id = q.UsuarioId.ToString();
            if (!primeiraAparicao.ContainsKey(id))
            {
                primeiraAparicao[id] = q.EnviadoEm ?? DateTimeOffset.MaxValue;
            }
        }

        var pseudonimos = primeiraAparicao
            .OrderBy(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select((x, i) => (x.Key, Codigo: $"P{i + 1:D2}"))
            .ToDictionary(x => x.Key, x => x.Codigo, StringComparer.Ordinal);

        // Id sem pseudônimo devolve nulo, e nunca o próprio id: um Guid na célula seria
        // exatamente o identificador que este bloco existe para não deixar sair.
        string? Quem(string? id) =>
            id is not null && pseudonimos.TryGetValue(id, out var c) ? c : null;

        var execucoes = sessoes.Select(s => new ExecucaoAvaliadaView(
            s.Id,
            s.CriadoEm,
            s.Status.ToString(),
            s.SugestaoId,
            s.SugestaoDescricao,
            s.AvaliacaoVeredito,
            s.AvaliacaoComentario,
            s.AvaliacaoEm,
            Quem(s.AvaliacaoUsuarioId))).ToList();

        var respondidos = questionarios
            .OrderBy(q => q.EnviadoEm ?? DateTimeOffset.MaxValue)
            .Select(q => new QuestionarioDoCompradorView(
                Quem(q.UsuarioId.ToString()),
                q.EnviadoEm,
                q.VersaoCatalogo,
                [.. respostas.GetValueOrDefault(q.Id, [])
                    .Select(r => new RespostaTabuladaView(
                        r.PerguntaCodigo, r.PerguntaTexto, r.OpcaoTexto, r.OpcaoValor,
                        r.TextoLivre))]))
            .ToList();

        // Colunas na ordem do catálogo ATUAL, e depois os códigos que só existem em respostas de
        // versões anteriores — sem isso uma pergunta que saiu do instrumento desapareceria da
        // planilha junto com as respostas que alguém de fato deu a ela.
        var doCatalogo = QuestionarioCatalogo.Perguntas.Select(p => p.Codigo).ToList();
        var extras = respondidos
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
            redeId, [.. doCatalogo, .. extras], comoTexto, pseudonimos.Count,
            execucoes, respondidos));
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

    /// <summary>
    /// O questionario do comprador, com o estado do portao junto.
    ///
    /// <para>
    /// <b>Um endpoint so, e nao um <c>/situacao</c> separado.</b> A tela da Secao G precisa
    /// saber se o botao aparece e, quando nao aparece, quantas execucoes faltam; a tela do
    /// questionario precisa das respostas. Os dois numeros do portao custam duas contagens, e
    /// parti-los em dois endpoints criaria duas verdades que podem divergir entre a chamada de
    /// uma tela e a da outra.
    /// </para>
    ///
    /// <para>
    /// Portao fechado NAO e 404: devolve a view com <c>Liberado = false</c> e as contagens,
    /// porque a tela explica a regra em vez de esconder o botao.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetAsync(
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] Guid usuarioId,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        return Results.Ok(await MontarAsync(db, usuarioId, redeId, ct));
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
        [FromBody] SalvarQuestionarioRequest req,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] Guid usuarioId,
        [FromQuery] int redeId = 1)
        => await GravarAsync(req.PassoAtual, req.Respostas, selar: false,
                             db, redeId, usuarioId, ct);

    /// <inheritdoc cref="SalvarAsync"/>
    private static async Task<IResult> EnviarAsync(
        [FromBody] EnviarQuestionarioRequest req,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] Guid usuarioId,
        [FromQuery] int redeId = 1)
        => await GravarAsync(req.PassoAtual, req.Respostas, selar: true,
                             db, redeId, usuarioId, ct);

    /// <summary>
    /// Rascunho e envio compartilham quase tudo — as mesmas guardas, o mesmo <i>upsert</i> — e
    /// diferem em duas coisas: o envio exige completude e move a sessão para
    /// <see cref="SessaoStatus.Concluida"/>. Duplicar o caminho deixaria as guardas divergirem
    /// com o tempo, e é justamente a guarda que impede gravar sobre uma avaliação já selada.
    /// </summary>
    private static async Task<IResult> GravarAsync(
        int passoAtual,
        IReadOnlyList<RespostaRequest>? respostas,
        bool selar,
        EngineDbContext db,
        int redeId,
        Guid usuarioId,
        CancellationToken ct)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        var portao = await PortaoAsync(db, usuarioId, redeId, ct);

        // O PORTAO E DO SERVIDOR, e nao da tela. Esconder o botao nao impede um POST direto, e
        // uma resposta entrando fora do protocolo da pesquisa nao tem como ser identificada
        // depois. Vale para o rascunho tambem: aceita-lo antes da liberacao criaria a linha que
        // a propria checagem de "ja respondeu" passaria a encontrar.
        //
        // Duas recusas com mensagens diferentes: "ja foi" e "ainda nao" mandam o chamador para
        // lados opostos, e um texto generico faria o comprador tentar de novo no caso em que
        // nada vai mudar.
        if (portao.EnviadoEm is not null)
        {
            return Results.Conflict(new ValidationErrorResponse(
                ["Você já respondeu o questionário. Ele é respondido uma única vez, e as " +
                 "respostas enviadas não podem ser alteradas."]));
        }

        if (!portao.Liberado)
        {
            return Results.Conflict(new ValidationErrorResponse(
                [$"O questionário é liberado depois de {portao.MinimoExigido} execuções " +
                 $"avaliadas. Você tem {portao.ExecucoesAvaliadas}."]));
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
        // índice único de `UsuarioId` — transformando uma falha transitória em erro permanente.
        var estrategia = db.Database.CreateExecutionStrategy();

        var recusa = await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            return await EscreverAsync(db, redeId, usuarioId, passoAtual, informadas, selar, agora, ct);
        });

        if (recusa is not null) return recusa;

        return Results.Ok(await MontarAsync(db, usuarioId, redeId, ct));
    }

    /// <summary>
    /// O corpo transacional: substitui as respostas e, no envio, sela e conclui a sessão.
    /// Devolve <c>null</c> em sucesso, ou o <c>IResult</c> da recusa. Roda dentro da estratégia
    /// de execução de <see cref="GravarAsync"/> — ver a nota de lá antes de mexer.
    /// </summary>
    private static async Task<IResult?> EscreverAsync(
        EngineDbContext db,
        int redeId,
        Guid usuarioId,
        int passoAtual,
        List<RespostaInformada> informadas,
        bool selar,
        DateTimeOffset agora,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var questionario = await db.Questionarios.FirstOrDefaultAsync(q => q.UsuarioId == usuarioId, ct);
        if (questionario is null)
        {
            questionario = new Questionario
            {
                Id = Guid.CreateVersion7(),
                RedeId = redeId,
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

        // O SELO NAO MEXE MAIS EM SESSAO NENHUMA. Antes ele levava a sessao a Concluida, e um
        // UPDATE condicional era o que impedia dois envios simultaneos de se sobrescreverem.
        // Quem conclui a execucao passou a ser a Secao G; aqui a corrida e entre dois envios do
        // MESMO comprador, e quem a resolve e o indice unico UQ_Questionarios_UsuarioId -- o
        // segundo INSERT estoura, a transacao volta atras, e a tentativa seguinte recebe a
        // recusa de "ja respondeu" pela checagem do portao.
        if (selar)
        {
            questionario.EnviadoEm = agora;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    /// <summary>
    /// O questionário do comprador, com o estado do portão junto.
    ///
    /// <para>
    /// <b>O escopo aqui é o usuário, não a rede</b>, e não é exceção à regra de inquilino: o
    /// <c>usuarioId</c> vem do <c>IRedeContext</c>, então o chamador só consegue perguntar por
    /// si mesmo — não há registro de terceiro a proteger. A rede continua gravada na linha, e é
    /// por ela que a tabulação lê.
    /// </para>
    /// </summary>
    private static async Task<QuestionarioView> MontarAsync(
        EngineDbContext db, Guid usuarioId, int redeId, CancellationToken ct)
    {
        var portao = await PortaoAsync(db, usuarioId, redeId, ct);

        var cabecalho = await db.Questionarios
            .AsNoTracking()
            .Where(q => q.UsuarioId == usuarioId)
            .Select(q => new { q.Id, q.PassoAtual, q.VersaoCatalogo, q.EnviadoEm })
            .FirstOrDefaultAsync(ct);

        if (cabecalho is null)
        {
            return new QuestionarioView(
                null, null, 0, QuestionarioCatalogo.Versao,
                portao.ExecucoesAvaliadas, portao.MinimoExigido, portao.Liberado, []);
        }

        var respostas = await db.QuestionarioRespostas
            .AsNoTracking()
            .Where(r => r.QuestionarioId == cabecalho.Id)
            .Select(r => new RespostaView(
                r.PerguntaCodigo, r.OpcaoCodigo, r.OpcaoValor, r.TextoLivre))
            .ToListAsync(ct);

        return new QuestionarioView(
            cabecalho.Id,
            cabecalho.EnviadoEm,
            cabecalho.PassoAtual,
            cabecalho.VersaoCatalogo,
            portao.ExecucoesAvaliadas,
            portao.MinimoExigido,
            portao.Liberado,
            respostas);
    }

    /// <summary>
    /// Texto livre em branco é ausência, não string vazia: sem isto o comprador que abre o campo
    /// e não escreve nada grava <c>""</c>, que na análise vira "respondeu e não disse nada" em
    /// vez de "não respondeu".
    /// </summary>
    private static string? Limpar(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

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
/// <param name="EnviadoEm">
/// Carimbo do envio, e a <b>única</b> autoridade sobre "selado": nulo é rascunho, preenchido é
/// respondido. Não há coluna de situação, aqui nem na tabela.
/// </param>
/// <param name="ExecucoesAvaliadas">
/// Execuções com Seção G respondida por este comprador. Viaja mesmo com o portão fechado
/// porque a tela <b>explica</b> quanto falta em vez de esconder o botão — botão ausente lê-se
/// como defeito, e a regra escrita lê-se como regra.
/// </param>
/// <param name="Liberado">
/// <c>ExecucoesAvaliadas &gt;= MinimoExigido</c>, calculado no servidor. A tela usa para
/// decidir o que desenhar; quem <b>recusa</b> um envio fora do protocolo é o endpoint.
/// </param>
internal sealed record QuestionarioView(
    Guid? Id,
    DateTimeOffset? EnviadoEm,
    int PassoAtual,
    int VersaoCatalogo,
    int ExecucoesAvaliadas,
    int MinimoExigido,
    bool Liberado,
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
/// <param name="Participantes">
/// Pessoas distintas com alguma atividade nesta rede. Contagem que o patrocinador pediu junto
/// dos pseudônimos: quantas responderam, ao lado de quantas execuções elas avaliaram. Sai dos
/// códigos atribuídos, então não há uma segunda definição de "participante" em lugar nenhum.
/// </param>
/// <param name="Execucoes">
/// Uma linha por execução, com a Seção G. <b>Inclui execução não avaliada de propósito</b>: é
/// o denominador da taxa de resposta.
/// </param>
/// <param name="Questionarios">
/// Uma linha por <b>comprador</b>, e não por execução. Os dois blocos são separados desde
/// 19/09/2026, quando o questionário deixou de pertencer à sessão: repetir as respostas em
/// cada execução do mesmo comprador faria a planilha parecer ter N questionários onde há um,
/// e qualquer contagem feita sobre ela sairia multiplicada pelo número de execuções.
/// </param>
internal sealed record TabulacaoView(
    int RedeId,
    IReadOnlyList<string> Codigos,
    IReadOnlyList<string> CodigosDeTexto,
    int Participantes,
    IReadOnlyList<ExecucaoAvaliadaView> Execucoes,
    IReadOnlyList<QuestionarioDoCompradorView> Questionarios);

/// <param name="Avaliador">
/// <b>Pseudônimo</b> de quem registrou a Seção G — P01, P02, P03… —, nunca o nome nem o
/// e-mail. Ver a nota em <c>TabulacaoAsync</c>: a identidade não sai do banco. Nulo quando
/// ninguém avaliou esta execução.
/// </param>
internal sealed record ExecucaoAvaliadaView(
    Guid SessaoId,
    DateTimeOffset CriadoEm,
    string Status,
    long? SugestaoId,
    string? SugestaoDescricao,
    string? AvaliacaoVeredito,
    string? AvaliacaoComentario,
    DateTimeOffset? AvaliacaoEm,
    string? Avaliador);

/// <param name="Respondente">
/// <b>Pseudônimo</b> de quem respondeu. O mesmo código que aparece como <c>Avaliador</c> nas
/// execuções dele — é o que liga os dois blocos sem identificar ninguém.
/// </param>
/// <param name="EnviadoEm">Nulo enquanto for rascunho.</param>
internal sealed record QuestionarioDoCompradorView(
    string? Respondente,
    DateTimeOffset? EnviadoEm,
    int VersaoCatalogo,
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
