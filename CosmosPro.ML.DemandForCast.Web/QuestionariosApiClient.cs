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
