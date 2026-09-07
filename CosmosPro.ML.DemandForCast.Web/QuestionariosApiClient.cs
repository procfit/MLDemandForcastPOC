using System.Net;
using System.Net.Http.Json;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// O questionário da última fase da sessão. Como nos demais clients, <c>redeId</c> e
/// <c>usuarioId</c> vêm do <see cref="IRedeContext"/> — nunca de parâmetro de página.
/// </summary>
public class QuestionariosApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    private static readonly TimeSpan LeituraTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// O instrumento. Estático e igual para todas as redes, então sem escopo de inquilino.
    /// </summary>
    public async Task<CatalogoView?> GetCatalogoAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);
        return await httpClient.GetFromJsonAsync<CatalogoView>("/api/questionarios/catalogo", cts.Token);
    }

    /// <summary>
    /// O questionário da sessão. Devolve <c>null</c> num 404 — sessão inexistente ou de outra
    /// rede. <b>Não</b> confundir com <c>QuestionarioView.Id == null</c>, que é a resposta
    /// legítima "esta sessão ainda não tem rascunho".
    /// </summary>
    public async Task<QuestionarioView?> GetAsync(Guid sessaoId, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var resp = await httpClient.GetAsync(
            $"/api/comparacoes/{sessaoId}/questionario?redeId={redeId}", cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<QuestionarioView>(cancellationToken: cts.Token);
    }

    /// <summary>
    /// A tabulação da rede: uma linha por execução, com a seção G e as respostas do
    /// questionário. É a fonte da tela de tabulação e da planilha.
    /// </summary>
    public async Task<TabulacaoView> GetTabulacaoAsync(CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var resp = await httpClient.GetFromJsonAsync<TabulacaoView>(
            $"/api/comparacoes/avaliacoes?redeId={redeId}", cts.Token);

        return resp ?? new TabulacaoView(redeId, [], []);
    }

    /// <summary>Grava o rascunho. Idempotente: manda o estado completo do wizard.</summary>
    public Task<QuestionarioResult> SalvarAsync(
        Guid sessaoId, int passoAtual, IReadOnlyList<RespostaEnviada> respostas, CancellationToken ct = default)
        => EnviarCorpoAsync(sessaoId, passoAtual, respostas, selar: false, ct);

    /// <summary>
    /// Sela a avaliação e conclui a sessão. Falha com 400 quando falta pergunta obrigatória e
    /// com 409 quando a sessão já foi avaliada.
    /// </summary>
    public Task<QuestionarioResult> EnviarAsync(
        Guid sessaoId, int passoAtual, IReadOnlyList<RespostaEnviada> respostas, CancellationToken ct = default)
        => EnviarCorpoAsync(sessaoId, passoAtual, respostas, selar: true, ct);

    /// <summary>
    /// Rascunho e envio diferem só na rota; a tradução do erro é a mesma, e duplicá-la faria
    /// as duas pontas divergirem na mensagem que o comprador lê.
    /// </summary>
    private async Task<QuestionarioResult> EnviarCorpoAsync(
        Guid sessaoId, int passoAtual, IReadOnlyList<RespostaEnviada> respostas, bool selar, CancellationToken ct)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var usuarioId = await redeContext.GetUsuarioIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var rota = selar
            ? $"/api/comparacoes/{sessaoId}/questionario/enviar?redeId={redeId}&usuarioId={usuarioId}"
            : $"/api/comparacoes/{sessaoId}/questionario?redeId={redeId}&usuarioId={usuarioId}";
        var corpo = new SalvarQuestionarioRequest(passoAtual, respostas);

        var resp = selar
            ? await httpClient.PostAsJsonAsync(rota, corpo, cts.Token)
            : await httpClient.PutAsJsonAsync(rota, corpo, cts.Token);

        if (resp.IsSuccessStatusCode)
        {
            var view = await resp.Content.ReadFromJsonAsync<QuestionarioView>(cancellationToken: cts.Token);
            return new QuestionarioResult(true, view, null);
        }

        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: cts.Token);
            return new QuestionarioResult(false, null, err?.Errors ?? ["Não foi possível gravar suas respostas."]);
        }

        var texto = await resp.Content.ReadAsStringAsync(cts.Token);
        return new QuestionarioResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {texto}"]);
    }
}

public sealed record SalvarQuestionarioRequest(int PassoAtual, IReadOnlyList<RespostaEnviada> Respostas);

public sealed record RespostaEnviada(string PerguntaCodigo, string OpcaoCodigo, string? TextoLivre);

public sealed record QuestionarioResult(
    bool Success, QuestionarioView? Questionario, IReadOnlyList<string>? Errors);

// --- Espelho dos DTOs de QuestionariosEndpoints ---------------------------------------

public sealed record CatalogoView(int Versao, IReadOnlyList<SecaoView> Secoes);

public sealed record SecaoView(string Titulo, string? Descricao, IReadOnlyList<PerguntaView> Perguntas);

public sealed record PerguntaView(
    string Codigo, string Texto, bool Obrigatoria, IReadOnlyList<OpcaoView> Opcoes);

public sealed record OpcaoView(string Codigo, string Texto, int? Valor, bool PermiteTextoLivre);

/// <param name="Id"><c>null</c> = ainda não há rascunho; a tela desenha o wizard vazio.</param>
public sealed record QuestionarioView(
    Guid? Id,
    string SessaoStatus,
    DateTimeOffset? EnviadoEm,
    int PassoAtual,
    int VersaoCatalogo,
    IReadOnlyList<RespostaView> Respostas)
{
    /// <summary>
    /// Se a avaliação já foi selada — a tela entra em modo leitura. Vem do status da sessão, que
    /// é a autoridade; <see cref="EnviadoEm"/> é só o carimbo que a tela mostra.
    /// </summary>
    public bool Selado => SessaoStatus == "Concluida";
}

public sealed record RespostaView(
    string PerguntaCodigo, string OpcaoCodigo, int? OpcaoValor, string? TextoLivre);

// --- Tabulação das avaliações ---------------------------------------------------------

public sealed record TabulacaoView(
    int RedeId,
    IReadOnlyList<string> Codigos,
    IReadOnlyList<AvaliacaoTabulada> Linhas)
{
    public int ComAvaliacao => Linhas.Count(l => l.AvaliacaoVeredito is not null);

    public int ComQuestionario => Linhas.Count(l => l.QuestionarioEnviadoEm is not null);

    /// <summary>
    /// Quantas versões distintas do instrumento aparecem nas respostas. <b>Mais de uma é
    /// aviso</b>, não curiosidade: o mesmo código designa afirmação diferente entre versões, e
    /// somar a coluna inteira misturaria perguntas. A tela declara isso em vez de deixar quem
    /// tabula descobrir depois.
    /// </summary>
    public IReadOnlyList<int> VersoesPresentes =>
        [.. Linhas.Select(l => l.VersaoCatalogo).OfType<int>().Distinct().Order()];
}

public sealed record AvaliacaoTabulada(
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
    IReadOnlyList<RespostaTabulada> Respostas)
{
    /// <summary>
    /// O que exportar na coluna de um código: o <b>número</b> da escala quando a pergunta é
    /// ordinal e o <b>texto</b> quando é nominal — exatamente o formato pedido. Devolve string
    /// vazia quando não há resposta, e nunca "0": zero seria uma posição na escala.
    /// </summary>
    public string Valor(string codigo)
    {
        var r = Respostas.FirstOrDefault(x => x.PerguntaCodigo == codigo);
        if (r is null) return "";
        return r.OpcaoValor is { } v ? v.ToString() : r.OpcaoTexto;
    }

    /// <summary>Complemento de "Outro:", quando existe. Vai numa coluna própria na planilha.</summary>
    public string? TextoLivre(string codigo) =>
        Respostas.FirstOrDefault(x => x.PerguntaCodigo == codigo)?.TextoLivre;

    public bool Respondido => QuestionarioEnviadoEm is not null;
}

public sealed record RespostaTabulada(
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
