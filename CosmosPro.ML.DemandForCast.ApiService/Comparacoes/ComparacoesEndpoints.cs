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

        group.MapGet("/{id:guid}/itens", ListItensAsync)
             .WithName("ListComparacaoSessaoItens")
             .Produces<SessaoItensPage>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/analise", AnaliseAsync)
             .WithName("GetComparacaoSessaoAnalise")
             .Produces<SessaoAnaliseView>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", ExcluirAsync)
             .WithName("ExcluirComparacaoSessao")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces<ValidationErrorResponse>(StatusCodes.Status409Conflict);

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
            .Select(ProjectToDetailView)
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
    /// Exclui a sessão e, por cascata do banco, o detalhe por item.
    ///
    /// <para>
    /// <b>O que NÃO é excluído:</b> a <c>CargaStage</c>, o <c>TreinoJob</c> e a
    /// <c>ComparacaoPbs</c> que a sessão apontava, nem o ZIP no MinIO. Os três ponteiros são
    /// FKs lógicas justamente para o histórico do engine sobreviver à remoção de quem o
    /// referencia (ver <c>EngineDbContext</c>) — e o inverso vale aqui: apagar a sessão não
    /// apaga artefatos que existem por si. O dado importado no <c>Stage</c> também fica, e
    /// fica de propósito: ele é por rede, não por sessão, e o próximo import o substitui
    /// inteiro.
    /// </para>
    /// </summary>
    private static async Task<IResult> ExcluirAsync(
        Guid id,
        EngineDbContext db,
        ILogger<Program> logger,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        // DELETE condicional numa instrução, em vez de "ler, decidir, apagar": entre a
        // leitura e a remoção o Worker pode avançar a fase, e apagar uma sessão que acabou
        // de entrar em Treinando deixaria o job órfão terminando no vazio. O WHERE repete a
        // condição de ComparacaoSessao.PodeExcluir — a decisão acontece no banco, junto com
        // a escrita, no mesmo padrão do `UPDATE ... WHERE Status = <fase reclamada>` da
        // materialização.
        //
        // Os itens saem por ON DELETE CASCADE da FK: ExecuteDelete emite um DELETE cru e não
        // faz cascata do lado do cliente, então quem apaga o detalhe é o banco. Sem a
        // cascata configurada no EngineDbContext isto falharia por violação de FK — e é
        // melhor assim do que apagar a sessão e deixar o detalhe órfão.
        var afetadas = await db.ComparacaoSessoes
            .Where(s => s.Id == id && s.RedeId == redeId)
            .Where(s => s.Status != SessaoStatus.ProcessandoDados
                     && s.Status != SessaoStatus.Treinando
                     && s.Status != SessaoStatus.Comparando
                     // A segunda recusa de PodeExcluir, por motivo diferente: aqui não há job
                     // a proteger, há dado. Concluida significa que o comprador respondeu o
                     // questionário, e a resposta é dado de pesquisa. Um rascunho de
                     // questionário (sessão em AguardandoQuestionario) continua indo embora
                     // por cascade — rascunho abandonado não pode trancar a sessão.
                     && s.Status != SessaoStatus.Concluida)
            .ExecuteDeleteAsync(ct);

        if (afetadas > 0)
        {
            logger.LogInformation("Sessao {SessaoId} excluida (rede {RedeId})", id, redeId);
            return Results.NoContent();
        }

        // Zero linhas tem duas causas com respostas diferentes, e só aqui vale a segunda
        // consulta. Sessão de outra rede cai no `is null` e responde 404, não 403: um 403
        // confirmaria a quem sondasse que a sessão existe em outro inquilino.
        var status = await db.ComparacaoSessoes
            .AsNoTracking()
            .Where(s => s.Id == id && s.RedeId == redeId)
            .Select(s => (SessaoStatus?)s.Status)
            .FirstOrDefaultAsync(ct);

        if (status is null) return Results.NotFound();

        // 409 e não 400 nos dois casos: a requisição está perfeitamente bem formada. Mas as
        // duas recusas dizem coisas opostas a quem chama — uma é "espere", a outra é "nunca" —
        // e mandar esperar por algo que não vai mudar faria o comprador voltar a tentar para
        // sempre.
        return Results.Conflict(new ValidationErrorResponse(
            status == SessaoStatus.Concluida
                ? ["Esta comparação já foi avaliada e não pode mais ser excluída: as respostas do " +
                   "questionário são o registro da avaliação. Se precisar removê-la mesmo assim, " +
                   "procure o suporte."]
                : [$"A comparação está em '{status}' e não pode ser excluída enquanto o processamento corre. " +
                   "Espere a fase terminar — se ela falhar ou concluir, a exclusão passa a ser permitida."]));
    }

    /// <summary>
    /// Detalhe por item, paginado e ordenado no servidor. É a tabela que o comprador
    /// confere item a item contra a memória dele, e ela tem dezenas de milhares de linhas.
    /// </summary>
    private static async Task<IResult> ListItensAsync(
        Guid id,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        [FromQuery] string? orderBy = null,
        [FromQuery] bool desc = true)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        if (await TotalDeItensAsync(db, id, redeId, ct) is not { } total) return Results.NotFound();

        var coluna = OrdemItensSessao.Resolver(orderBy);
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        var itens = await OrdemItensSessao
            .Aplicar(ItensDaSessao(db, id, redeId), coluna, desc)
            .Skip(skip)
            .Take(take)
            .Select(ProjectItemToView)
            .ToListAsync(ct);

        return Results.Ok(new SessaoItensPage(total, coluna, desc, itens));
    }

    /// <summary>
    /// Agregados que a manchete materializada não carrega: previsão contra previsão
    /// (a partir das taxas gravadas item a item) e o recorte de onde o ML ficou pior.
    ///
    /// <para>
    /// Apurado no servidor, e não no navegador, pelo mesmo motivo da paginação: a
    /// população é a da sugestão do ERP inteira, e trazê-la para somar no circuito Blazor
    /// seria mandar dezenas de milhares de linhas por SignalR para exibir seis números.
    /// </para>
    /// </summary>
    private static async Task<IResult> AnaliseAsync(
        Guid id,
        EngineDbContext db,
        CancellationToken ct,
        [FromQuery] int redeId = 1)
    {
        if (await Redes.RedesEndpoints.ValidateRedeAsync(db, redeId, ct) is { } invalida) return invalida;

        if (await TotalDeItensAsync(db, id, redeId, ct) is not { } total) return Results.NotFound();

        var escopo = ItensDaSessao(db, id, redeId);

        var porCurva = await FatiasAsync(
            escopo, i => i.Curva, c => string.IsNullOrWhiteSpace(c) ? "sem curva" : c, ct);
        var porLoja = await FatiasAsync(escopo, i => i.LojaId, l => $"Loja {l}", ct);

        // Abertura por giro: a dimensão em que o resultado do ML de fato varia. Medido em
        // três execuções sobre extrações diferentes, o ML ganha do ERP onde a demanda real
        // é densa (~3 un./dia), empata em torno de 0,2 e desaba abaixo disso — que é onde
        // vive a maior parte de uma grade de farmácia. Sem esta abertura o número global
        // responde "o ML perdeu" para uma população em que ele nunca teve chance, e as
        // outras dimensões (curva do ERP, loja) não separam isso: a curva é do ERP e mistura
        // giro com critério dele, e loja é geografia.
        //
        // Os cortes (0,2 e 1,0 un./dia) saem dessas medições, não de convenção — e são o
        // mínimo honesto: dizem ONDE o ganho existe em vez de afirmar que existe em média.
        var porGiro = await FatiasAsync(
            escopo,
            i => i.DemandaDiaReal == null ? 0
               : i.DemandaDiaReal >= 1m ? 3
               : i.DemandaDiaReal >= 0.2m ? 2
               : i.DemandaDiaReal > 0m ? 1
               : 4,
            RotuloDeGiro,
            ct);

        var comDecisaoMl = await escopo.CountAsync(i => i.CompraSugeridaMl != null, ct);

        var sobrouMais = escopo.Where(i =>
            i.SobraMlUnidades != null && i.SobraMlUnidades > i.SobraPbsUnidades);

        var itensComSobraMlMaior = await sobrouMais.CountAsync(ct);
        var sobraExtraUnidades = await sobrouMais.SumAsync(
            i => i.SobraMlUnidades!.Value - i.SobraPbsUnidades, ct);
        var sobraExtraValor = await escopo
            .Where(i => i.SobraMlValor != null && i.SobraPbsValor != null
                     && i.SobraMlValor > i.SobraPbsValor)
            .SumAsync(i => i.SobraMlValor!.Value - i.SobraPbsValor!.Value, ct);

        var pioresNaCompra = await sobrouMais
            .OrderByDescending(i => i.SobraMlUnidades!.Value - i.SobraPbsUnidades)
            .ThenBy(i => i.LojaId).ThenBy(i => i.Sku)
            .Take(TetoDePiores)
            .Select(i => new ItemPiorView(
                i.LojaId, i.Sku, i.NomeProduto,
                i.SobraPbsUnidades, i.SobraMlUnidades,
                null, null, i.JanelaAlemDoHistorico))
            .ToListAsync(ct);

        var pioresNaPrevisao = await escopo
            .Where(i => i.DemandaDiaMl != null && i.DemandaDiaReal != null)
            .Select(i => new
            {
                Item = i,
                ErroPbs = Math.Abs(i.DemandaDiaPbs - i.DemandaDiaReal!.Value),
                ErroMl = Math.Abs(i.DemandaDiaMl!.Value - i.DemandaDiaReal!.Value),
            })
            .Where(x => x.ErroMl > x.ErroPbs)
            .OrderByDescending(x => x.ErroMl - x.ErroPbs)
            .ThenBy(x => x.Item.LojaId).ThenBy(x => x.Item.Sku)
            .Take(TetoDePiores)
            .Select(x => new ItemPiorView(
                x.Item.LojaId, x.Item.Sku, x.Item.NomeProduto,
                x.Item.SobraPbsUnidades, x.Item.SobraMlUnidades,
                x.ErroPbs, x.ErroMl, x.Item.JanelaAlemDoHistorico))
            .ToListAsync(ct);

        return Results.Ok(new SessaoAnaliseView(
            Itens: total,
            PorCurva: porCurva,
            PorLoja: porLoja,
            PorGiro: porGiro,
            ItensComDecisaoMl: comDecisaoMl,
            ItensComSobraMlMaior: itensComSobraMlMaior,
            SobraExtraMlUnidades: sobraExtraUnidades,
            SobraExtraMlValor: sobraExtraValor,
            PioresNaCompra: pioresNaCompra,
            PioresNaPrevisao: pioresNaPrevisao));
    }

    /// <summary>
    /// Quantos itens de "onde o ML foi pior" chegam à tela. Fixo e pequeno de propósito: o
    /// bloco existe para o comprador reconhecer o tipo de erro, não para virar uma segunda
    /// tabela paginada ao lado da primeira.
    /// </summary>
    private const int TetoDePiores = 10;

    /// <summary>
    /// Os itens de uma sessão, <b>escopados pela rede do pai no mesmo round-trip</b>.
    ///
    /// <para>
    /// <c>ComparacaoSessaoItens</c> não tem <c>RedeId</c> próprio — a PK é
    /// <c>(SessaoId, LojaId, Sku)</c> e o escopo é transitivo pela FK. Consultar por
    /// <c>SessaoId</c> sozinho entregaria o detalhe comercial de um inquilino a quem
    /// acertasse um Guid; conferir o pai numa consulta à parte deixaria a janela entre as
    /// duas. Por isso o <c>join</c> vive aqui, num único lugar, e todo endpoint de item
    /// passa por ele — página, contagem e agregado.
    /// </para>
    /// </summary>
    private static IQueryable<ComparacaoSessaoItem> ItensDaSessao(
        EngineDbContext db, Guid id, int redeId) =>
        from i in db.ComparacaoSessaoItens.AsNoTracking()
        join s in db.ComparacaoSessoes on i.SessaoId equals s.Id
        where i.SessaoId == id && s.RedeId == redeId
        select i;

    /// <summary>
    /// Total de itens da sessão, ou <c>null</c> quando ela não existe <b>nesta</b> rede.
    ///
    /// <para>
    /// A contagem sai correlacionada à linha da sessão já filtrada por rede, e é isso que
    /// mantém separados os dois desfechos que uma contagem solta confundiria: "não é sua"
    /// (nulo, 404) e "é sua e está vazia" (zero, página vazia). O 404 é deliberado — um 403
    /// confirmaria a quem sondasse que a sessão existe em outra rede.
    /// </para>
    /// </summary>
    private static Task<int?> TotalDeItensAsync(
        EngineDbContext db, Guid id, int redeId, CancellationToken ct) =>
        db.ComparacaoSessoes.AsNoTracking()
            .Where(s => s.Id == id && s.RedeId == redeId)
            .Select(s => (int?)db.ComparacaoSessaoItens.Count(i => i.SessaoId == s.Id))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Faixa de giro do item, pela demanda real medida na janela. "Sem previsão do ML" é faixa
    /// à parte de "sem venda no período": a primeira é ausência de medição, a segunda é medição
    /// que deu zero, e somá-las esconderia justamente a diferença entre não saber e saber que
    /// não vendeu.
    /// </summary>
    private static string RotuloDeGiro(int faixa) => faixa switch
    {
        3 => "1 un./dia ou mais",
        2 => "0,2 a 1 un./dia",
        1 => "até 0,2 un./dia",
        4 => "sem venda no período",
        _ => "sem previsão do ML",
    };

    /// <summary>
    /// Uma dimensão do drill-down. Somas cruas em vez de MAE/WAPE prontos: quem renderiza
    /// divide, e assim o numerador e o denominador de cada métrica ficam visíveis — é o que
    /// permite dizer "apurado sobre 12 de 4.000 itens" em vez de exibir um WAPE que parece
    /// falar da população inteira.
    /// </summary>
    private static async Task<List<SessaoFatiaView>> FatiasAsync<TKey>(
        IQueryable<ComparacaoSessaoItem> escopo,
        Expression<Func<ComparacaoSessaoItem, TKey>> chave,
        Func<TKey, string> rotulo,
        CancellationToken ct)
    {
        var brutas = await escopo
            .GroupBy(chave)
            .Select(g => new
            {
                Chave = g.Key,
                Itens = g.Count(),
                ComPrevisaoMl = g.Count(x => x.DemandaDiaMl != null && x.DemandaDiaReal != null),
                SomaDemandaRealDiaria = g.Sum(x =>
                    x.DemandaDiaMl != null && x.DemandaDiaReal != null ? x.DemandaDiaReal!.Value : 0m),
                SomaErroAbsPbs = g.Sum(x =>
                    x.DemandaDiaMl != null && x.DemandaDiaReal != null
                        ? Math.Abs(x.DemandaDiaPbs - x.DemandaDiaReal!.Value)
                        : 0m),
                SomaErroAbsMl = g.Sum(x =>
                    x.DemandaDiaMl != null && x.DemandaDiaReal != null
                        ? Math.Abs(x.DemandaDiaMl!.Value - x.DemandaDiaReal!.Value)
                        : 0m),
                VitoriasMl = g.Count(x =>
                    x.DemandaDiaMl != null && x.DemandaDiaReal != null
                    && Math.Abs(x.DemandaDiaMl!.Value - x.DemandaDiaReal!.Value)
                       < Math.Abs(x.DemandaDiaPbs - x.DemandaDiaReal!.Value)),
                VitoriasPbs = g.Count(x =>
                    x.DemandaDiaMl != null && x.DemandaDiaReal != null
                    && Math.Abs(x.DemandaDiaMl!.Value - x.DemandaDiaReal!.Value)
                       > Math.Abs(x.DemandaDiaPbs - x.DemandaDiaReal!.Value)),
            })
            .ToListAsync(ct);

        return [.. brutas
            .Select(f => new SessaoFatiaView(
                rotulo(f.Chave), f.Itens, f.ComPrevisaoMl,
                f.SomaDemandaRealDiaria, f.SomaErroAbsPbs, f.SomaErroAbsMl,
                f.VitoriasMl, f.VitoriasPbs))
            .OrderByDescending(f => f.Itens)
            .ThenBy(f => f.Chave, StringComparer.OrdinalIgnoreCase)];
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

    private static readonly Expression<Func<ComparacaoSessaoItem, SessaoItemView>> ProjectItemToView =
        i => new SessaoItemView(
            i.LojaId,
            i.Sku,
            i.NomeProduto,
            i.Curva,
            i.CompraSugeridaPbs,
            i.CompraSugeridaMl,
            i.VendidoNaJanela,
            i.SobraPbsUnidades,
            i.SobraMlUnidades,
            i.SobraPbsValor,
            i.JanelaAlemDoHistorico);

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
            s.MensagemErro,
            s.SkusSemCadastro,
            null);

    /// <summary>
    /// Projeção do detalhe — a única que traz o <c>ResultadoJson</c>. A listagem não o traz
    /// de propósito: são os agregados da manchete de cada sessão, e mandá-los em toda linha
    /// de uma lista de 50 sessões pagaria o payload inteiro para exibir um badge de status.
    /// </summary>
    private static readonly Expression<Func<ComparacaoSessao, SessaoView>> ProjectToDetailView =
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
            s.MensagemErro,
            s.SkusSemCadastro,
            s.ResultadoJson);

    private static SessaoView ToView(ComparacaoSessao s) => new(
        s.Id, s.Nome, s.Status.ToString(), s.CriadoEm,
        s.SugestaoId, s.SugestaoDescricao, s.SugestaoDataHora, s.SugestaoTipoCalculo,
        s.MotivoInviabilidade, s.MensagemErro, s.SkusSemCadastro, s.ResultadoJson);
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
    string? MensagemErro,
    int? SkusSemCadastro = null,
    string? ResultadoJson = null);

/// <param name="OrderBy">
/// Coluna <b>efetivamente</b> aplicada, depois da whitelist (<see cref="OrdemItensSessao"/>).
/// Viaja na resposta porque uma coluna recusada cai no padrão: sem este campo a tela
/// desenharia a seta de ordenação num cabeçalho que não ordenou nada.
/// </param>
internal sealed record SessaoItensPage(
    int Total,
    string OrderBy,
    bool Desc,
    IReadOnlyList<SessaoItemView> Itens);

/// <summary>
/// Uma linha do detalhe. Os anuláveis chegam anuláveis <b>de propósito</b>: nulo é "não foi
/// possível calcular" e zero é uma decisão de compra — ver <see cref="ComparacaoSessaoItem"/>.
/// Achatar um no outro aqui faria a tela afirmar ao comprador que o ML mandaria não comprar
/// nada, ou que a compra não deixou capital parado.
/// </summary>
/// <remarks>
/// As taxas de demanda/dia gravadas na linha (<c>DemandaDiaPbs</c>/<c>Ml</c>/<c>Real</c>) e a
/// sobra em R$ do braço de ML <b>não</b> viajam aqui: a tabela do comprador não tem coluna para
/// elas, e quem as consome é <c>GET /api/comparacoes/{id}/analise</c>, que as agrega no servidor
/// em WAPE/MAE por curva e por loja. Mandá-las em cada uma de dezenas de milhares de linhas
/// pagaria o payload para nada.
/// </remarks>
internal sealed record SessaoItemView(
    int LojaId,
    string Sku,
    string? NomeProduto,
    string? Curva,
    decimal CompraSugeridaPbs,
    decimal? CompraSugeridaMl,
    decimal VendidoNaJanela,
    decimal SobraPbsUnidades,
    decimal? SobraMlUnidades,
    decimal? SobraPbsValor,
    bool JanelaAlemDoHistorico);

/// <param name="Itens">População inteira da sessão — o denominador de todo o resto.</param>
/// <param name="SobraExtraMlUnidades">
/// Quanto o braço de ML teria deixado <b>a mais</b> na prateleira, somado só sobre os itens
/// em que ele sobrou mais. Não é o saldo líquido entre os dois braços: os itens em que o ML
/// sobrou menos não abatem daqui, justamente para que o número responda "quanto o ML piora
/// onde ele piora" em vez de ser cancelado pela média.
/// </param>
internal sealed record SessaoAnaliseView(
    int Itens,
    IReadOnlyList<SessaoFatiaView> PorCurva,
    IReadOnlyList<SessaoFatiaView> PorLoja,
    int ItensComDecisaoMl,
    int ItensComSobraMlMaior,
    decimal SobraExtraMlUnidades,
    decimal SobraExtraMlValor,
    IReadOnlyList<ItemPiorView> PioresNaCompra,
    IReadOnlyList<ItemPiorView> PioresNaPrevisao,
    IReadOnlyList<SessaoFatiaView>? PorGiro = null);

internal sealed record SessaoFatiaView(
    string Chave,
    int Itens,
    int ItensComPrevisaoMl,
    decimal SomaDemandaRealDiaria,
    decimal SomaErroAbsPbs,
    decimal SomaErroAbsMl,
    int VitoriasMl,
    int VitoriasPbs);

/// <summary>
/// Item em que o ML ficou pior. Os campos das duas armas convivem no mesmo tipo porque a
/// tela mostra as duas listas com o mesmo desenho: <see cref="ErroPbs"/>/<see cref="ErroMl"/>
/// só existem na lista de previsão, e <see cref="SobraMlUnidades"/> só na de compra.
///
/// <para>
/// Sem curva e sem R$: as duas listas têm dez linhas e mostram código, produto, loja, o par de
/// números da arma e a ressalva. O total em reais de "onde o ML foi pior" é agregado em
/// <see cref="SessaoAnaliseView.SobraExtraMlValor"/>, que é onde a tela o lê.
/// </para>
/// </summary>
internal sealed record ItemPiorView(
    int LojaId,
    string Sku,
    string? NomeProduto,
    decimal? SobraPbsUnidades,
    decimal? SobraMlUnidades,
    decimal? ErroPbs,
    decimal? ErroMl,
    bool JanelaAlemDoHistorico);
