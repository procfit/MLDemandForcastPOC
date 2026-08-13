using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CosmosPro.ML.DemandForCast.Web.Services;
using Radzen;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// O <c>redeId</c> vem sempre do <see cref="IRedeContext"/>, como nos demais clients
/// (ver <see cref="ImportsApiClient"/>) — nunca de parâmetro de página.
/// </summary>
public class ComparacoesApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    /// <summary>
    /// Teto próprio para as chamadas de leitura por trás do poll de 3s (Comparacoes.razor,
    /// Sessao.razor). O <see cref="HttpClient.Timeout"/> deste client é 10 minutos porque
    /// <see cref="UploadDadosAsync"/> sobe o ZIP e legitimamente precisa desse teto — mas
    /// esse mesmo default, herdado por list/get, faria cada tick do poll ficar pendurado
    /// por até 10 minutos se a apiservice travar, e o guard <c>_loading</c> das páginas
    /// nunca mais liberaria um refresh novo. CancelAfter dá um teto curto só para essas
    /// chamadas, sem mexer no Timeout do client.
    /// </summary>
    private static readonly TimeSpan LeituraTimeout = TimeSpan.FromSeconds(30);

    public async Task<SessaoView> CreateAsync(string? nome, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);
        var resp = await httpClient.PostAsJsonAsync(
            $"/api/comparacoes?redeId={redeId}", new CreateSessaoRequest(nome), cts.Token);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessaoView>(cancellationToken: cts.Token))!;
    }

    public async Task<IReadOnlyList<SessaoView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);
        var result = await httpClient.GetFromJsonAsync<List<SessaoView>>(
            $"/api/comparacoes?take={take}&redeId={redeId}", cts.Token);
        return result ?? [];
    }

    public async Task<SessaoView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);
        var resp = await httpClient.GetAsync($"/api/comparacoes/{id}?redeId={redeId}", cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessaoView>(cancellationToken: cts.Token);
    }

    /// <summary>
    /// Uma página do detalhe por item. Paginação e ordenação são <b>do servidor</b>: a
    /// população é a da sugestão inteira do ERP, e trazê-la para o circuito Blazor a fim de
    /// ordenar em memória mandaria dezenas de milhares de linhas por SignalR a cada clique
    /// no cabeçalho.
    /// </summary>
    public async Task<SessaoItensPage> GetItensAsync(
        Guid id, int skip, int take, string? orderBy, bool desc, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var url = $"/api/comparacoes/{id}/itens?redeId={redeId}&skip={skip}&take={take}" +
                  $"&desc={desc.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            url += $"&orderBy={Uri.EscapeDataString(orderBy)}";
        }

        var page = await httpClient.GetFromJsonAsync<SessaoItensPage>(url, cts.Token);
        return page ?? new SessaoItensPage(0, "", desc, []);
    }

    /// <summary>
    /// Agregados que a manchete materializada não carrega: previsão contra previsão e o
    /// recorte de onde o ML ficou pior. Devolve <c>null</c> num 404 — sessão de outra rede,
    /// ou que não existe.
    /// </summary>
    public async Task<SessaoAnalise?> GetAnaliseAsync(Guid id, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var resp = await httpClient.GetAsync($"/api/comparacoes/{id}/analise?redeId={redeId}", cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessaoAnalise>(cancellationToken: cts.Token);
    }

    /// <summary>
    /// Desserializa os agregados da manchete gravados pelo Worker. Devolve <c>null</c>
    /// quando ausente ou ilegível — a tela trata isso como "sem resultado legível", nunca
    /// como zero (mesmo contrato de <c>ComparisonApiClient.ParseResultado</c>).
    /// </summary>
    public static SessaoResultadoView? ParseResultado(string? resultadoJson)
    {
        if (string.IsNullOrWhiteSpace(resultadoJson)) return null;
        // Catch largo de propósito: isto roda dentro do render, e uma exceção que escape
        // aqui vira 500 na página inteira em vez de um aviso num card.
        try { return JsonSerializer.Deserialize<SessaoResultadoView>(resultadoJson, Json); }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<UploadDadosResult> UploadDadosAsync(
        Guid id, Stream content, string fileName, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var usuarioId = await redeContext.GetUsuarioIdAtualAsync();

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new("application/zip");
        form.Add(fileContent, "file", fileName);

        // Sem timeout curto aqui de propósito: este é o upload do ZIP, que usa o
        // Timeout de 10 minutos do HttpClient (ver LeituraTimeout acima).
        var resp = await httpClient.PostAsync(
            $"/api/comparacoes/{id}/dados?redeId={redeId}&usuarioId={usuarioId}", form, ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            return new UploadDadosResult(true, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new UploadDadosResult(false, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        return new UploadDadosResult(false, [$"Erro HTTP {(int)resp.StatusCode}: {text}"]);
    }

    /// <summary>
    /// Exclui a sessão. O 404 é tratado como sucesso: o efeito pretendido — "esta comparação
    /// não deve mais existir" — já está satisfeito, e mostrar erro para quem clicou duas
    /// vezes, ou para uma linha que o polling de 3s ainda não atualizou, seria ruído.
    /// </summary>
    public async Task<ExcluirSessaoResult> ExcluirAsync(Guid id, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();

        var resp = await httpClient.DeleteAsync($"/api/comparacoes/{id}?redeId={redeId}", ct);

        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new ExcluirSessaoResult(true, null);
        }

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new ExcluirSessaoResult(false, err?.Errors ?? ["A comparação não pode ser excluída agora."]);
        }

        var texto = await resp.Content.ReadAsStringAsync(ct);
        return new ExcluirSessaoResult(false, [$"Erro HTTP {(int)resp.StatusCode}: {texto}"]);
    }
}

public sealed record CreateSessaoRequest(string? Nome);

public sealed record SessaoView(
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
    string? ResultadoJson = null)
{
    /// <summary>
    /// Se não há mais nada a esperar do servidor por conta própria, e o poll de 3s pode parar.
    ///
    /// <para>
    /// <b>Não é o mesmo que "fluxo concluído"</b>, e desde o questionário os dois conceitos
    /// deixaram de coincidir: <c>AguardandoQuestionario</c> está no meio do fluxo e entra aqui,
    /// porque quem a move é um clique do próprio usuário nesta tela — não um worker. Sem isso a
    /// tela ficaria fazendo poll para sempre esperando um humano que já está olhando para ela.
    /// Quem quer saber se o fluxo terminou olha o status, não este predicado.
    /// </para>
    /// </summary>
    public bool SemPollNecessario =>
        Status is "Concluida" or "Inviavel" or "Falha" or "AguardandoQuestionario";

    /// <summary>Se a comparação já tem resultado para mostrar (materializado).</summary>
    public bool TemResultado => Status is "AguardandoQuestionario" or "Concluida";

    /// <summary>
    /// Espelha <c>ComparacaoSessao.PodeExcluir</c>, com as duas recusas: as fases em andamento
    /// têm job de outra fila trabalhando pela sessão, e excluir ali deixaria o job terminando
    /// no vazio; <c>Concluida</c> significa que o questionário foi respondido, e resposta de
    /// pesquisa não evapora por clique. Aqui o status é string porque atravessa JSON. A
    /// autoridade é o endpoint, que repete a condição no <c>WHERE</c> do <c>DELETE</c> — botão
    /// desabilitado é cosmético.
    /// </summary>
    public bool PodeExcluir =>
        Status is not ("ProcessandoDados" or "Treinando" or "Comparando" or "Concluida");

    public static string EstadoLabel(string status) => status switch
    {
        "AguardandoDados" => "Aguardando dados",
        "ProcessandoDados" => "Processando dados",
        "Treinando" => "Treinando",
        "Comparando" => "Comparando",
        "AguardandoQuestionario" => "Aguardando avaliação",
        "Concluida" => "Concluída",
        "Inviavel" => "Inviável",
        "Falha" => "Falha",
        _ => status,
    };

    public static BadgeStyle BadgeFor(string status) => status switch
    {
        "AguardandoDados" => BadgeStyle.Secondary,
        "ProcessandoDados" or "Treinando" or "Comparando" => BadgeStyle.Info,
        // Primary, não Success: a comparação deu certo, mas o fluxo pede uma ação do
        // comprador — verde de "pronto" o faria ignorar a pendência.
        "AguardandoQuestionario" => BadgeStyle.Primary,
        "Concluida" => BadgeStyle.Success,
        "Inviavel" => BadgeStyle.Warning,
        "Falha" => BadgeStyle.Danger,
        _ => BadgeStyle.Light,
    };
}

public sealed record UploadDadosResult(bool Success, IReadOnlyList<string>? Errors);

public sealed record ExcluirSessaoResult(bool Success, IReadOnlyList<string>? Errors);

// --- Espelho de SessaoResultado (Worker/Sessoes/SessaoResultadoMontador.cs) -----------
//
// Tudo anulável de propósito, inclusive o que o Worker sempre preenche: um payload
// truncado ou de outra versão precisa render "não consigo ler isto" num card, não uma
// NullReferenceException que apaga a página inteira.
//
// Campo que o payload gravado tem e este espelho não é simplesmente ignorado na
// desserialização (o JsonSerializerOptions abaixo não recusa membro desconhecido), então
// sessões materializadas por uma versão anterior continuam abrindo normalmente.

/// <summary>
/// Agregados da manchete de uma sessão concluída.
///
/// <para>
/// <b>Cheque <see cref="TemColunaMl"/> antes de exibir qualquer número do braço de ML.</b>
/// Com a cobertura de 15 a 30 dias do ERP contra o horizonte de 7 dias do pipeline, a
/// ausência é o desfecho esperado hoje — e o que vai na tela nesse caso é
/// <see cref="ExplicacaoSemColunaMl"/>, nunca um traço, um zero ou uma célula vazia.
/// </para>
/// </summary>
public sealed record SessaoResultadoView(
    DateTimeOffset GeradoEm,
    Guid ComparacaoPbsId,
    DateTime? SugestaoDataHora,
    byte TipoCalculo,
    int ItensAvaliados,
    decimal VendidoNaJanelaUnidades,
    BracoDaSessaoView? Pbs,
    ConfrontoDaSessaoView? Confronto,
    string? MotivoMlIndisponivel,
    int ItensComDecisaoMl,
    int ItensComPrevisaoMl,
    string? UtilidadeDecisaoMl,
    RupturaObservadaView? Ruptura,
    int ItensComJanelaAlemDoHistorico,
    int ItensSemPrecoCompra,
    int? SkusSemCadastro,
    string? RessalvaTreinoServe)
{
    /// <summary>
    /// Se existe braço de ML a colocar ao lado do do ERP. Falso é o estado normal de hoje,
    /// não um defeito.
    /// </summary>
    public bool TemColunaMl => Confronto is { Itens: > 0 };

    /// <summary>
    /// Por que não há coluna de ML, em português de comprador. O texto vem do próprio
    /// resultado (o Worker o escreve a partir do motivo real); a alternativa local existe
    /// só para payload antigo, e diz o que sabe sem inventar a causa.
    /// </summary>
    public string ExplicacaoSemColunaMl =>
        string.IsNullOrWhiteSpace(MotivoMlIndisponivel)
            ? "Este resultado não registrou o motivo, e por isso não é possível dizer aqui por que a conta do " +
              "método de ML não saiu. O que está do lado do seu ERP continua sendo o que de fato aconteceu com " +
              "esta compra."
            : MotivoMlIndisponivel;

    /// <summary>
    /// Se as figuras em R$ desta manchete estão subestimadas por itens sem preço de compra
    /// cadastrado. Eles entram nos totais com zero em reais — as unidades sobraram, só não
    /// se sabe quanto capital elas representam.
    /// </summary>
    public bool ValoresSubestimados => ItensSemPrecoCompra > 0;

    /// <summary>
    /// Fração da cobertura com snapshot de estoque. Sem ela, "nenhum dia zerado" pode
    /// significar "não faltou" ou "não sabemos" — e as duas leituras levam a decisões opostas.
    /// </summary>
    public double? CoberturaDoSnapshot =>
        Ruptura is { DiasNaJanela: > 0 } r ? (double)r.DiasComSnapshot / r.DiasNaJanela : null;
}

public sealed record BracoDaSessaoView(
    decimal CompraUnidades,
    decimal SobraUnidades,
    decimal SobraValor);

/// <param name="Itens">
/// Itens em que <b>os dois</b> braços existem. É o denominador honesto do confronto: somar
/// o ERP sobre a população inteira e o ML sobre o punhado que ele decidiu faria o ML parecer
/// dezenas de vezes melhor por ter sido medido em menos itens.
/// </param>
public sealed record ConfrontoDaSessaoView(int Itens, BracoDaSessaoView? Pbs, BracoDaSessaoView? Ml);

public sealed record RupturaObservadaView(
    int ItensComDiaSemEstoque,
    int DiasSemEstoque,
    int DiasComSnapshot,
    int DiasNaJanela);

// --- Detalhe por item e análise (endpoints da apiservice) -----------------------------

public sealed record SessaoItensPage(
    int Total,
    string OrderBy,
    bool Desc,
    IReadOnlyList<SessaoItem> Itens);

public sealed record SessaoItem(
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
    bool JanelaAlemDoHistorico)
{
    /// <summary>
    /// Quem chegou mais perto do que a loja realmente vendeu, nesta linha: menor sobra.
    ///
    /// <para>
    /// Comparação direta, sem módulo, porque <c>SobraCalculator</c> nunca produz sobra
    /// negativa — vender mais do que havia é ruptura, medida em outro lugar. Menor sobra é,
    /// aqui, inequivocamente melhor.
    /// </para>
    ///
    /// <para>
    /// Nulo quando não há braço de ML, e nulo <b>tem</b> de virar texto na tela: "empate" e
    /// "não calculado" são afirmações opostas.
    /// </para>
    /// </summary>
    public bool? MlFicouMaisPerto => SobraMlUnidades is { } ml ? ml < SobraPbsUnidades : null;

    public bool Empate => SobraMlUnidades == SobraPbsUnidades;
}

public sealed record SessaoAnalise(
    int Itens,
    IReadOnlyList<SessaoFatia>? PorCurva,
    IReadOnlyList<SessaoFatia>? PorLoja,
    int ItensComDecisaoMl,
    int ItensComSobraMlMaior,
    decimal SobraExtraMlUnidades,
    decimal SobraExtraMlValor,
    IReadOnlyList<ItemPior>? PioresNaCompra,
    IReadOnlyList<ItemPior>? PioresNaPrevisao)
{
    public int ItensComPrevisaoMl => PorCurva?.Sum(f => f.ItensComPrevisaoMl) ?? 0;

    public decimal SomaDemandaRealDiaria => PorCurva?.Sum(f => f.SomaDemandaRealDiaria) ?? 0m;

    public decimal SomaErroAbsPbs => PorCurva?.Sum(f => f.SomaErroAbsPbs) ?? 0m;

    public decimal SomaErroAbsMl => PorCurva?.Sum(f => f.SomaErroAbsMl) ?? 0m;

    public int VitoriasMl => PorCurva?.Sum(f => f.VitoriasMl) ?? 0;

    public int VitoriasPbs => PorCurva?.Sum(f => f.VitoriasPbs) ?? 0;

    public int Empates => ItensComPrevisaoMl - VitoriasMl - VitoriasPbs;

    /// <summary>
    /// Global apurado somando as fatias por curva, e não numa consulta à parte: cada item
    /// cai em exatamente uma curva, então a soma é o total exato — e duas consultas para o
    /// mesmo número seriam duas versões dele.
    /// </summary>
    public SessaoFatia Global => new(
        "total", Itens, ItensComPrevisaoMl, SomaDemandaRealDiaria,
        SomaErroAbsPbs, SomaErroAbsMl, VitoriasMl, VitoriasPbs);
}

/// <summary>
/// Uma fatia do drill-down. MAE e WAPE são derivados aqui, das somas cruas, e nunca
/// gravados: com o numerador e o denominador à mão, a tela pode dizer sobre quantos itens a
/// métrica foi apurada em vez de exibir um percentual que parece falar de toda a população.
/// </summary>
public sealed record SessaoFatia(
    string? Chave,
    int Itens,
    int ItensComPrevisaoMl,
    decimal SomaDemandaRealDiaria,
    decimal SomaErroAbsPbs,
    decimal SomaErroAbsMl,
    int VitoriasMl,
    int VitoriasPbs)
{
    public double? MaePbs => ItensComPrevisaoMl == 0 ? null : (double)SomaErroAbsPbs / ItensComPrevisaoMl;

    public double? MaeMl => ItensComPrevisaoMl == 0 ? null : (double)SomaErroAbsMl / ItensComPrevisaoMl;

    public double? WapePbs =>
        SomaDemandaRealDiaria == 0m ? null : (double)(SomaErroAbsPbs / SomaDemandaRealDiaria);

    public double? WapeMl =>
        SomaDemandaRealDiaria == 0m ? null : (double)(SomaErroAbsMl / SomaDemandaRealDiaria);

    /// <summary>
    /// O ML erra mais que o ERP nesta fatia. Média global esconde regressão local
    /// (CLAUDE.md §6) — é justamente esta marca que um número único apagaria.
    /// </summary>
    public bool MlPerde => WapePbs is { } pbs && WapeMl is { } ml && ml > pbs;
}

public sealed record ItemPior(
    int LojaId,
    string Sku,
    string? NomeProduto,
    decimal? SobraPbsUnidades,
    decimal? SobraMlUnidades,
    decimal? ErroPbs,
    decimal? ErroMl,
    bool JanelaAlemDoHistorico);
