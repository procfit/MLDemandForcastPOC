using System.Net;
using System.Net.Http.Json;
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
    string? MensagemErro)
{
    /// <summary>Estados terminais: a sessão não muda mais sozinha, então nada de poll.</summary>
    public bool EstadoTerminal => Status is "Concluida" or "Inviavel" or "Falha";

    public static string EstadoLabel(string status) => status switch
    {
        "AguardandoDados" => "Aguardando dados",
        "ProcessandoDados" => "Processando dados",
        "Treinando" => "Treinando",
        "Comparando" => "Comparando",
        "Concluida" => "Concluída",
        "Inviavel" => "Inviável",
        "Falha" => "Falha",
        _ => status,
    };

    public static BadgeStyle BadgeFor(string status) => status switch
    {
        "AguardandoDados" => BadgeStyle.Secondary,
        "ProcessandoDados" or "Treinando" or "Comparando" => BadgeStyle.Info,
        "Concluida" => BadgeStyle.Success,
        "Inviavel" => BadgeStyle.Warning,
        "Falha" => BadgeStyle.Danger,
        _ => BadgeStyle.Light,
    };
}

public sealed record UploadDadosResult(bool Success, IReadOnlyList<string>? Errors);
