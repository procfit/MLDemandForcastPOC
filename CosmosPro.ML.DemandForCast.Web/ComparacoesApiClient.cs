using System.Globalization;
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

    /// <summary>
    /// Abre o download do ZIP que a sessão recebeu, para repetir a comparação sobre
    /// exatamente o mesmo envio.
    /// </summary>
    /// <remarks>
    /// Devolve a resposta crua com <see cref="HttpCompletionOption.ResponseHeadersRead"/>:
    /// o corpo é repassado por stream por quem chama, nunca materializado aqui. Quem chama
    /// é dono do <see cref="HttpResponseMessage"/> e precisa descartá-lo. Mesmo padrão de
    /// <c>ExtratorApiClient.AbrirDownloadAsync</c>.
    ///
    /// <para>
    /// Sem <see cref="LeituraTimeout"/>: este é o único GET deste client que não está atrás
    /// do poll de 3s, e o ZIP pode ser grande — vale o teto de 10 minutos do próprio client,
    /// o mesmo que o upload usa.
    /// </para>
    /// </remarks>
    public async Task<HttpResponseMessage> AbrirDownloadDadosAsync(Guid id, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        return await httpClient.GetAsync(
            $"/api/comparacoes/{id}/dados?redeId={redeId}", HttpCompletionOption.ResponseHeadersRead, ct);
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
        Guid id, int skip, int take, string? orderBy, bool desc,
        FiltroDeItens? filtro = null, CancellationToken ct = default)
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
        url += (filtro ?? FiltroDeItens.Nenhum).ParaQueryString();

        var page = await httpClient.GetFromJsonAsync<SessaoItensPage>(url, cts.Token);
        return page ?? new SessaoItensPage(0, "", desc, []);
    }

    /// <summary>
    /// Valores presentes nesta sessão para alimentar os filtros. Devolve <c>null</c> num 404 —
    /// sessão de outra rede, ou que não existe.
    /// </summary>
    public async Task<FiltrosDisponiveis?> GetFiltrosDeItensAsync(Guid id, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var resp = await httpClient.GetAsync($"/api/comparacoes/{id}/itens/filtros?redeId={redeId}", cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<FiltrosDisponiveis>(cancellationToken: cts.Token);
    }

    /// <summary>
    /// Todos os itens do recorte, sem paginar, para montar a planilha.
    /// </summary>
    /// <remarks>
    /// Sem <see cref="LeituraTimeout"/>: são até dezenas de milhares de linhas, e este é o
    /// único GET deste client que não está atrás do poll de 3s — vale o teto de 10 minutos do
    /// próprio client, o mesmo que o upload usa.
    /// </remarks>
    public async Task<IReadOnlyList<SessaoItem>> GetItensParaExportacaoAsync(
        Guid id, string? orderBy, bool desc, FiltroDeItens? filtro = null, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();

        var url = $"/api/comparacoes/{id}/itens/exportacao?redeId={redeId}" +
                  $"&desc={desc.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            url += $"&orderBy={Uri.EscapeDataString(orderBy)}";
        }
        url += (filtro ?? FiltroDeItens.Nenhum).ParaQueryString();

        return await httpClient.GetFromJsonAsync<List<SessaoItem>>(url, ct) ?? [];
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
    /// <summary>
    /// Registra o veredito rápido da execução. Reenviar substitui o anterior — o comprador pode
    /// mudar de ideia depois de olhar o detalhe, ao contrário do questionário, que é selado.
    /// </summary>
    public async Task<(bool Sucesso, string? Erro)> RegistrarAvaliacaoAsync(
        Guid id, string veredito, string? comentario, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var usuarioId = await redeContext.GetUsuarioIdAtualAsync();

        var resp = await httpClient.PostAsJsonAsync(
            $"/api/comparacoes/{id}/avaliacao?redeId={redeId}&usuarioId={usuarioId}",
            new { Veredito = veredito, Comentario = comentario }, ct);

        if (resp.IsSuccessStatusCode) return (true, null);

        // O texto do servidor vai para a tela: ele nomeia a causa (sem resultado, veredito
        // invalido), e uma mensagem generica faria o comprador tentar de novo sem saber o quê.
        var corpo = await resp.Content.ReadAsStringAsync(ct);
        return (false, string.IsNullOrWhiteSpace(corpo) ? $"Falha {(int)resp.StatusCode}." : corpo);
    }

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
    string? ResultadoJson = null,
    bool DadosEnviados = false,
    string? AvaliacaoVeredito = null,
    string? AvaliacaoComentario = null,
    DateTimeOffset? AvaliacaoEm = null)
{
    /// <summary>
    /// Se o comprador já registrou o veredito rápido desta execução. <b>Nulo em
    /// <c>AvaliacaoVeredito</c> é "ainda não avaliou"</b>, e nunca se confunde com
    /// <c>NaoValido</c> — a tela precisa distinguir quem não opinou de quem reprovou.
    /// </summary>
    public bool FoiAvaliada => !string.IsNullOrWhiteSpace(AvaliacaoVeredito);

    /// <summary>Veredito em português de comprador, ou <c>null</c> quando ainda não houve.</summary>
    public string? VereditoLegivel => AvaliacaoVeredito switch
    {
        "Valido" => "Válido",
        "ValidoComRessalvas" => "Válido com ressalvas",
        "NaoValido" => "Não válido",
        _ => null,
    };

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
    string? RessalvaTreinoServe,
    string? RessalvaExtrapolacao = null)
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
    decimal SobraValor,
    decimal SobraDaCompraUnidades = 0m,
    decimal SobraDaCompraValor = 0m);

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

/// <param name="Total">Itens do recorte filtrado — o denominador da paginação exibida.</param>
/// <param name="TotalSemFiltro">
/// População inteira da sessão, para a tela poder dizer "12 de 20.153". Sem isto um recorte
/// de 12 itens pareceria a sugestão completa.
/// </param>
public sealed record SessaoItensPage(
    int Total,
    string OrderBy,
    bool Desc,
    IReadOnlyList<SessaoItem> Itens,
    int TotalSemFiltro = 0,
    TotaisDosItens? Totais = null);

/// <summary>
/// Filtros combináveis da tela de itens. <c>null</c> em cada campo é "não filtrar por isto";
/// <see cref="Ausente"/> é o recorte "sem categoria"/"sem curva", que é coisa diferente.
/// </summary>
public sealed record FiltroDeItens(
    int? LojaId = null,
    string? Categoria = null,
    string? Curva = null,
    bool SomenteComAlerta = false,
    /// <summary>
    /// Só os itens em que o ML deixaria <b>mais</b> sobra que o ERP — o recorte que o bloco
    /// "Onde o ML foi pior" resume em dez linhas. Existe para o link daquele bloco abrir a
    /// tabela inteira já recortada, em vez de o comprador procurar item a item.
    /// </summary>
    bool SomenteMlPior = false,
    /// <summary>Fabricante do cadastro, ou <see cref="Ausente"/> para os sem fabricante.</summary>
    string? Fabricante = null,
    /// <summary>
    /// Um valor de <c>MercadoAlertas</c>, ou <see cref="Ausente"/> para os itens <b>sem dado de
    /// mercado</b> — que é diferente de <c>SemAlerta</c>: um não foi avaliado, o outro foi e
    /// está dentro do esperado.
    /// </summary>
    string? Alerta = null,
    /// <summary>Um estado de <c>Engine.Sessoes.AnaliseRapida</c>, ou <see cref="Ausente"/>.</summary>
    string? AnaliseRapida = null,
    /// <summary><c>ML</c>, <c>PBS</c>, <c>Empate</c> ou <see cref="Ausente"/> (sem braço de ML).</summary>
    string? MaisPerto = null,
    /// <summary>
    /// Só itens com índice vs bairro <b>abaixo</b> deste valor. Item sem índice não entra: nulo
    /// não é "abaixo de", é "não avaliado".
    /// </summary>
    decimal? IndiceAbaixoDe = null,
    /// <summary><c>RedeMenor</c> ou <c>IqviaMenor</c>, comparando os dois preços-índice.</summary>
    string? Preco = null)
{
    /// <summary>
    /// Sentinela que casa ausência do atributo. Precisa ser <b>o mesmo</b> string que a
    /// apiservice reconhece (<c>ComparacoesEndpoints.FiltroAusente</c>) — são dois processos,
    /// e um rótulo divergente aqui filtraria por uma categoria literalmente chamada "__sem__",
    /// devolvendo tela vazia sem erro nenhum.
    /// </summary>
    public const string Ausente = "__sem__";

    public static FiltroDeItens Nenhum { get; } = new();

    public bool Algum => LojaId is not null
        || !string.IsNullOrWhiteSpace(Categoria)
        || !string.IsNullOrWhiteSpace(Curva)
        || SomenteComAlerta
        || SomenteMlPior
        || !string.IsNullOrWhiteSpace(Fabricante)
        || !string.IsNullOrWhiteSpace(Alerta)
        || !string.IsNullOrWhiteSpace(AnaliseRapida)
        || !string.IsNullOrWhiteSpace(MaisPerto)
        || IndiceAbaixoDe is not null
        || !string.IsNullOrWhiteSpace(Preco);

    public string ParaQueryString()
    {
        var q = "";
        if (LojaId is { } loja) q += $"&lojaId={loja}";
        if (!string.IsNullOrWhiteSpace(Categoria)) q += $"&categoria={Uri.EscapeDataString(Categoria)}";
        if (!string.IsNullOrWhiteSpace(Curva)) q += $"&curva={Uri.EscapeDataString(Curva)}";
        if (SomenteComAlerta) q += "&somenteComAlerta=true";
        if (SomenteMlPior) q += "&somenteMlPior=true";
        if (!string.IsNullOrWhiteSpace(Fabricante)) q += $"&fabricante={Uri.EscapeDataString(Fabricante)}";
        if (!string.IsNullOrWhiteSpace(Alerta)) q += $"&alerta={Uri.EscapeDataString(Alerta)}";
        if (!string.IsNullOrWhiteSpace(AnaliseRapida)) q += $"&analiseRapida={Uri.EscapeDataString(AnaliseRapida)}";
        if (!string.IsNullOrWhiteSpace(MaisPerto)) q += $"&maisPerto={Uri.EscapeDataString(MaisPerto)}";
        if (IndiceAbaixoDe is { } ix) q += $"&indiceAbaixoDe={ix.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(Preco)) q += $"&preco={Uri.EscapeDataString(Preco)}";
        return q;
    }
}

/// <param name="ItensComCompraMl">
/// Sobre quantos itens do recorte <paramref name="CompraMlUnidades"/> foi apurada. A soma do
/// ML sem este número parece falar de todos os itens filtrados.
/// </param>
public sealed record TotaisDosItens(
    int Itens,
    decimal CompraPbsUnidades,
    decimal? CompraPbsComparavelUnidades,
    decimal? CompraMlUnidades,
    int ItensComCompraMl,
    /// <summary>
    /// Itens em que o ML mandou comprar <b>acima de zero</b>. Diferente de
    /// <see cref="ItensComCompraMl"/>, que conta onde ele decidiu — inclusive decidindo zero.
    /// Decidir comprar nada é uma decisão, não uma ausência, e sem este número a tela não
    /// distinguia "calculou" de "comprou".
    /// </summary>
    int ItensComCompraMlPositiva,
    decimal VendidoNaJanela,
    decimal SobraPbsUnidades,
    decimal? SobraPbsComparavelUnidades,
    decimal? SobraMlUnidades,
    int ItensComSobraMl,
    decimal? SobraPbsValor,
    int ItensComValorPbs = 0,
    decimal? SobraPbsComparavelValor = null,
    decimal? SobraMlValor = null,
    int ItensComValorMl = 0,
    // Itens do recorte com medição de mercado. Vem do servidor, e não de contagem na página
    // carregada: a página traz 25 linhas de um recorte que pode ter milhares, então contar
    // aqui diria "20 de 25" onde a resposta é "21 de 43".
    int ItensComDadoDeMercado = 0,
    int ItensComAlertaDeMercado = 0,
    int ItensComPrecoComparavel = 0,
    int ItensComPrecoRedeMenor = 0,
    int ItensComPrecoRedeMaior = 0)
{
    /// <summary>
    /// Itens com medição de mercado mas <b>sem preço comparável</b> — quase sempre porque a rede
    /// não vendeu nenhuma unidade do item no recorte, e sem venda não há preço.
    ///
    /// <para>
    /// Existe para a conta fechar. O patrocinador desenhou o placar com dois grupos somando o
    /// total; na prática há três, e omitir o terceiro faria a soma não bater com o número de
    /// itens medidos — a tela pareceria errada, ou pior, esses itens seriam empurrados para um
    /// dos lados e passariam a afirmar um resultado que ninguém apurou.
    /// </para>
    /// </summary>
    public int ItensSemPrecoComparavel => ItensComDadoDeMercado - ItensComPrecoComparavel;

    /// <summary>Preço idêntico nos dois lados. Raro, mas não é vitória de ninguém.</summary>
    public int ItensComPrecoEmpatado =>
        ItensComPrecoComparavel - ItensComPrecoRedeMenor - ItensComPrecoRedeMaior;

    /// <summary>
    /// Se há placar de preço a exibir. Falso quando não houve medição de mercado no recorte —
    /// e nesse caso a tela não pode mostrar um placar de zero a zero, que leria como "os preços
    /// estão iguais" em vez de "não há dado".
    /// </summary>
    public bool TemPlacarDePreco => ItensComPrecoComparavel > 0;

    /// <summary>
    /// Diferença de sobra entre os braços, ou <c>null</c> quando o ML não foi apurado em
    /// nenhum item do recorte. Zero aqui afirmaria "os dois métodos empataram", que é o
    /// contrário de "não há como comparar".
    ///
    /// <para>
    /// O lado do PBS é <see cref="SobraPbsComparavelUnidades"/>, e <b>nunca</b>
    /// <see cref="SobraPbsUnidades"/>. Já foi o total geral, e o resultado era um número que
    /// não media método nenhum: o PBS somava os 20.153 itens do recorte e o ML os 2.106 em
    /// que foi calculado, então a subtração media 18 mil itens a menos na conta e a tela a
    /// chamava de "diferença de sobra". Ver a nota de <c>TotalizarAsync</c>.
    /// </para>
    /// </summary>
    /// <summary>
    /// Diferenca de compra entre os bracos, sempre sobre o subconjunto comparavel. Nula sem
    /// braco de ML: zero afirmaria que os dois metodos mandaram comprar a mesma coisa.
    /// </summary>
    public decimal? DiferencaCompraUnidades =>
        CompraMlUnidades is { } ml && CompraPbsComparavelUnidades is { } pbs ? ml - pbs : null;

    public decimal? DiferencaSobraUnidades =>
        SobraMlUnidades is { } ml && SobraPbsComparavelUnidades is { } pbs ? ml - pbs : null;

    /// <summary>
    /// Idem em reais, sobre o subconjunto mais estreito ainda: item sem preço de compra fica
    /// fora dos dois lados.
    /// </summary>
    public decimal? DiferencaSobraValor =>
        SobraMlValor is { } ml && SobraPbsComparavelValor is { } pbs ? ml - pbs : null;
}

/// <param name="TemItemSemCategoria">
/// Se existe item sem categoria. A tela usa isto para oferecer o recorte "sem categoria" —
/// que numa sessão materializada antes da coluna existir é o recorte de <b>todos</b> os itens.
/// </param>
public sealed record FiltrosDisponiveis(
    IReadOnlyList<int> Lojas,
    IReadOnlyList<string> Categorias,
    bool TemItemSemCategoria,
    IReadOnlyList<string> Curvas,
    bool TemItemSemCurva,
    IReadOnlyList<string>? Fabricantes = null,
    bool TemItemSemFabricante = false);

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
    bool JanelaAlemDoHistorico,
    string? Categoria = null,
    decimal? SobraMlValor = null,
    // Sinal de mercado da IQVIA. Nulo e "sem dado de mercado para este item", nunca zero:
    // a tela mostra travessao e diz o porque, nao um numero.
    DateOnly? MercadoMes = null,
    string? MercadoBrick = null,
    decimal? MercadoUnidadesRede = null,
    decimal? MercadoUnidadesConcorrentes = null,
    decimal? MercadoIndiceDesempenho = null,
    int? MercadoDiasSemEstoque = null,
    string? MercadoAlerta = null,
    // Cadastro e estoque (itens 2, 3 e 4 do patrocinador). Nulo em qualquer uma e "nao ha
    // dado", e nas de estoque isso e diferente de zero: zero e prateleira vazia, que e
    // medicao. A tela mostra travessao para nulo.
    string? Fabricante = null,
    string? Ean = null,
    decimal? EstoqueNaSugestao = null,
    decimal? EstoqueNoFimDoPeriodo = null,
    decimal? VendaMediaDiaria = null,
    decimal? MercadoValorRede = null,
    decimal? MercadoValorConcorrentes = null)
{
    /// <summary>
    /// Preço médio das bandeiras da rede neste item, no bairro e mês comparados: o valor que a
    /// IQVIA atribuiu dividido pelas unidades que ela atribuiu.
    ///
    /// <para>
    /// <b>É preço-índice, não preço de balcão.</b> A IQVIA normaliza preços entre os
    /// participantes do painel, então este número não é o que passou no caixa. Ele serve para
    /// comparar com <see cref="PrecoMedioConcorrentes"/>, que sai do mesmo arquivo e da mesma
    /// normalização — nunca para comparar com o preço de compra do Stage nem com a base de
    /// vendas da rede, onde a diferença mediria metodologia e não posicionamento.
    /// </para>
    ///
    /// <para>
    /// Nulo quando <b>não há unidades</b> que sustentem um preço. Esse é o caso comum do lado
    /// da rede: o bairro vendeu e a rede não vendeu nada. Zero ali afirmaria que a rede vende
    /// de graça, e a tela ordenaria o item como o mais barato do mercado.
    /// </para>
    /// </summary>
    public decimal? PrecoMedioRede => Media(MercadoValorRede, MercadoUnidadesRede);

    /// <summary>Idem para o agregado de concorrentes, mesma ressalva.</summary>
    public decimal? PrecoMedioConcorrentes =>
        Media(MercadoValorConcorrentes, MercadoUnidadesConcorrentes);

    /// <summary>
    /// Se o preço-índice da rede está <b>acima</b> do dos concorrentes no mesmo recorte. Nulo
    /// quando falta um dos dois lados — sem os dois não há comparação, e falso significaria
    /// "está mais barata", que é uma afirmação diferente de "não se sabe".
    /// </summary>
    public bool? RedeMaisCaraQueOMercado =>
        PrecoMedioRede is { } rede && PrecoMedioConcorrentes is { } conc ? rede > conc : null;

    private static decimal? Media(decimal? valor, decimal? unidades) =>
        valor is { } v && unidades is { } u && u > 0m ? v / u : null;

    /// <summary>
    /// Quantos dias o estoque que sobrou duraria no ritmo de venda dos últimos 120 dias.
    ///
    /// <para>
    /// Usa o <b>estoque do fim do período</b>, e não o do dia da sugestão — decisão do
    /// patrocinador em 05/09/2026. A diferença não é de detalhe: com o estoque do fim, o
    /// vermelho acusa <i>uma compra que deixou capital parado</i>; com o da sugestão, acusaria
    /// <i>a decisão de comprar tendo muito</i>. O mesmo item pode ficar vermelho numa leitura e
    /// verde na outra, e a régua de 60 dias que ele deu vale para esta.
    /// </para>
    ///
    /// <para>
    /// Nula quando falta um dos dois lados, e também quando a venda média é zero — aí não há
    /// divisão, e o caso é tratado por <see cref="AnaliseRapida"/>, que o distingue de "sem
    /// dado". Estoque zero é cobertura zero, e não ausência: prateleira vazia é uma medição.
    /// </para>
    /// </summary>
    public decimal? Cobertura =>
        Engine.Sessoes.AnaliseRapida.Cobertura(EstoqueNoFimDoPeriodo, VendaMediaDiaria);

    /// <summary>
    /// O sinal de cor da coluna "Análise rápida", na régua que o patrocinador definiu em
    /// 05/09/2026: <b>vermelho</b> a partir de 60 dias de cobertura, <b>amarelo</b> de 30 a 59,
    /// <b>verde</b> abaixo de 30.
    ///
    /// <para>
    /// <b>Existe um quarto estado, e ele não é enfeite.</b> Item com estoque na prateleira e
    /// <i>nenhuma</i> venda em 120 dias não tem cobertura — a divisão não existe. Mas "não dá
    /// conta" está longe de "está tudo bem": é dinheiro parado que não gira, provavelmente o
    /// pior item da lista. Deixá-lo sem cor o esconderia justamente por ser ruim demais para a
    /// fórmula. Estado próprio, decidido pelo patrocinador (resposta 2B).
    /// </para>
    ///
    /// <para>
    /// Nulo é o quinto caso e significa <b>não avaliado</b> — falta estoque medido ou histórico
    /// para a média. Nunca confundir com verde: um diz "está bem", o outro diz "ninguém olhou".
    /// </para>
    /// </summary>
    public string? AnaliseRapida =>
        Engine.Sessoes.AnaliseRapida.Classificar(EstoqueNoFimDoPeriodo, VendaMediaDiaria);

    /// <summary>
    /// Rotulo do alerta em portugues de comprador. Devolve <c>null</c> quando nao ha alerta
    /// a mostrar -- item sem dado de mercado (<c>MercadoAlerta</c> nulo) ou avaliado e dentro
    /// do esperado. Os dois casos sao distinguidos por <see cref="TemDadoDeMercado"/>, e a
    /// tela precisa dizer coisas diferentes sobre eles.
    /// </summary>
    public string? AlertaDeMercadoLegivel => MercadoAlerta switch
    {
        "Ruptura" => "Possível perda por ruptura",
        "SemCausa" => "Abaixo do bairro, sem causa aparente",
        "NaoApurado" => "Abaixo do bairro, estoque não apurado",
        _ => null,
    };

    /// <summary>
    /// Houve medicao de mercado para este item. Falso significa que uma das pontes nao
    /// fechou (loja sem CNPJ, SKU sem EAN, EAN nao reportado pela IQVIA, ou nenhum mes
    /// coberto antes da sugestao) -- e nao que o item vai bem.
    /// </summary>
    public bool TemDadoDeMercado => MercadoAlerta is not null;

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
    IReadOnlyList<ItemPior>? PioresNaPrevisao,
    IReadOnlyList<SessaoFatia>? PorGiro = null,
    TotaisDosItens? Totais = null)
{
    /// <summary>
    /// Quantos grupos desta abertura tiveram <b>menor WAPE do ML</b>, e o total de grupos que
    /// chegaram a ser medidos. Grupo sem métrica apurada não entra em nenhum dos dois lados:
    /// contá-lo no denominador diria "o ML perdeu em N grupos" sobre grupos onde ninguém mediu.
    /// </summary>
    public static (int Ml, int Medidos) MenorWape(IReadOnlyList<SessaoFatia>? fatias)
    {
        var medidos = fatias?.Where(f => f.WapePbs is not null && f.WapeMl is not null).ToList() ?? [];
        return (medidos.Count(f => f.WapeMl < f.WapePbs), medidos.Count);
    }

    /// <summary>
    /// Registros que tiveram venda positiva na previsão medida. Sai das fatias de giro, e não de
    /// uma consulta nova: as faixas com venda são exatamente as que não são "sem venda no
    /// período" nem "sem previsão do ML".
    /// </summary>
    public int RegistrosComVendaPositiva =>
        PorGiro?.Where(f => f.SomaDemandaRealDiaria > 0m).Sum(f => f.ItensComPrevisaoMl) ?? 0;

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

    /// <summary>
    /// Fecha a explicação do WAPE com os números desta execução. Existe como texto visível, e
    /// não como tooltip, porque é numa apresentação e num print que o comprador lê o
    /// indicador — e tooltip não sai em nenhum dos dois.
    /// </summary>
    public string LeituraDoWape => Leitura(WapePbs, WapeMl, v => v.ToString("P1"), "menor erro global");

    /// <summary>Idem para o MAE, na unidade do indicador (unidades por dia).</summary>
    public string LeituraDoMae =>
        Leitura(MaePbs, MaeMl, v => $"{v:N2} un./dia", "menor erro médio");

    /// <summary>
    /// Os dois desfechos que não são "alguém ganhou" existem porque são afirmações
    /// diferentes entre si e diferentes de vitória: <b>não apurado</b> é ausência de medida
    /// (WAPE sem venda real no denominador, MAE sem item medido) e <b>empate</b> é medida
    /// igual. Eleger vencedor em qualquer um dos dois faria a tela afirmar, com a autoridade
    /// de um número, o que ninguém calculou.
    /// </summary>
    private static string Leitura(
        double? pbs, double? ml, Func<double, string> formatar, string qualificacao)
    {
        if (pbs is not { } p || ml is not { } m)
        {
            return "Nesta execução este indicador não foi apurado para os dois métodos, " +
                   "então não há comparação a fazer aqui.";
        }

        var numeros = $"Nesta execução: PBS {formatar(p)} e ML {formatar(m)}";

        // Igualdade exata, sem tolerância, pelo mesmo critério de MlPerde: inventar um
        // epsilon aqui criaria um empate que a coluna "ML perde aqui?" não reconhece, e as
        // duas leituras da mesma tela passariam a discordar.
        if (p == m) return $"{numeros} — empate, nenhum dos dois apresentou {qualificacao}.";

        return p < m
            ? $"{numeros}; portanto, o seu ERP apresentou {qualificacao}."
            : $"{numeros}; portanto, o ML apresentou {qualificacao}.";
    }
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
