using System.Net;
using System.Net.Http.Json;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// Dados de mercado da IQVIA (F16). O <c>redeId</c> vem sempre do
/// <see cref="IRedeContext"/>, como nos demais clients — nenhuma página informa rede.
/// </summary>
public class MercadoApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    public async Task<MercadoUploadResult> UploadAsync(Stream content, string fileName, long length, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(fileContent, "file", fileName);

        var resp = await httpClient.PostAsync($"/api/mercado/uploads?redeId={redeId}", form, ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            var body = await resp.Content.ReadFromJsonAsync<MercadoUploadResponse>(cancellationToken: ct);
            return new MercadoUploadResult(true, body, null);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return new MercadoUploadResult(false, null, err?.Errors ?? ["Erro de validação desconhecido."]);
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        return new MercadoUploadResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {text}"]);
    }

    public async Task<IReadOnlyList<MercadoCargaView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var result = await httpClient.GetFromJsonAsync<List<MercadoCargaView>>(
            $"/api/mercado/uploads?take={take}&redeId={redeId}", ct);
        return result ?? [];
    }

    /// <summary>
    /// Oportunidades de sortimento: o que o mercado vende nos bairros da rede e não existe no
    /// cadastro dela (regras A1 e A2).
    /// </summary>
    /// <param name="corteMinimo">
    /// Nulo deixa o servidor aplicar o padrão calibrado. Passar zero é "sem filtro de
    /// relevância", que devolve dezenas de milhares de linhas — só para inspeção.
    /// </param>
    public async Task<OportunidadesPagina> OportunidadesAsync(
        decimal? corteMinimo = null,
        string? brick = null,
        string? areaFarmacia = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();

        var q = $"/api/mercado/oportunidades?redeId={redeId}&skip={skip}&take={take}";
        if (corteMinimo is { } corte) q += $"&corteMinimo={corte.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(brick)) q += $"&brick={Uri.EscapeDataString(brick)}";
        if (!string.IsNullOrWhiteSpace(areaFarmacia)) q += $"&areaFarmacia={Uri.EscapeDataString(areaFarmacia)}";

        var pagina = await httpClient.GetFromJsonAsync<OportunidadesPagina>(q, ct);
        return pagina ?? new OportunidadesPagina([], 0, null, 0);
    }

    public async Task<IReadOnlyList<MercadoCoberturaView>> CoberturaAsync(CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var result = await httpClient.GetFromJsonAsync<List<MercadoCoberturaView>>(
            $"/api/mercado/cobertura?redeId={redeId}", ct);
        return result ?? [];
    }

    /// <summary>
    /// Exclui as observações de uma célula de cobertura (mês × brick). Devolve o erro em
    /// texto, ou <c>null</c> no sucesso — o 409 de "envio em processamento" precisa chegar à
    /// tela com a explicação do servidor, não como exceção genérica.
    /// </summary>
    public async Task<string?> ExcluirCoberturaAsync(DateOnly mes, string brick, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var resp = await httpClient.DeleteAsync(
            $"/api/mercado/cobertura?redeId={redeId}&mes={mes:yyyy-MM-dd}&brick={Uri.EscapeDataString(brick)}", ct);

        if (resp.IsSuccessStatusCode) return null;

        if (resp.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
        {
            var body = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return body?.Errors is { Count: > 0 } erros ? string.Join(" ", erros) : "O servidor recusou a exclusão.";
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return "Este recorte não existe mais — a tabela pode estar desatualizada. Atualize a página.";
        }

        return $"Falha ao excluir (HTTP {(int)resp.StatusCode}). Tente novamente.";
    }

    /// <summary>
    /// Desfaz um envio inteiro: recortes declarados, órfãos de catálogo/painel, o arquivo
    /// guardado e a linha do histórico. Existe para o import feito na rede errada — o caso em
    /// que até o rastro é contaminação. Mesmo contrato de erro do
    /// <see cref="ExcluirCoberturaAsync"/>.
    /// </summary>
    public async Task<string?> ExcluirEnvioAsync(Guid cargaId, CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var resp = await httpClient.DeleteAsync($"/api/mercado/uploads/{cargaId}?redeId={redeId}", ct);

        if (resp.IsSuccessStatusCode) return null;

        if (resp.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
        {
            var body = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: ct);
            return body?.Errors is { Count: > 0 } erros ? string.Join(" ", erros) : "O servidor recusou a exclusão.";
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return "Este envio não existe mais — a tabela pode estar desatualizada. Atualize a página.";
        }

        return $"Falha ao excluir (HTTP {(int)resp.StatusCode}). Tente novamente.";
    }
}

public sealed record MercadoUploadResult(bool Success, MercadoUploadResponse? Body, IReadOnlyList<string>? Errors);

public sealed record MercadoUploadResponse(Guid Id, string Status, DateTimeOffset DataAgendamento);

public sealed record MercadoCargaView(
    Guid Id,
    string Status,
    DateTimeOffset DataAgendamento,
    DateTimeOffset? DataInicioProcessamento,
    DateTimeOffset? DataConclusao,
    string NomeArquivoOriginal,
    string? MensagemErro,
    long? LinhasImportadas,
    string? ResumoJson);

public sealed record MercadoCoberturaView(
    DateOnly Mes,
    string Brick,
    int Observacoes,
    decimal Unidades);

/// <param name="EansNoCatalogo">
/// Tamanho do catálogo de códigos de barras da rede. <b>Zero significa "o catálogo não foi
/// enviado", nunca "a rede não tem produto nenhum"</b> — e aí a lista vem vazia de propósito,
/// porque sem catálogo não há como afirmar ausência. A tela explica em vez de listar.
/// </param>
/// <param name="Mes">
/// Mês da IQVIA usado: o mais recente carregado. Nulo quando não há mês nenhum, ou quando o
/// catálogo está vazio — nos dois casos não há o que declarar.
/// </param>
public sealed record OportunidadesPagina(
    IReadOnlyList<OportunidadeDeSortimento> Itens,
    int Total,
    DateOnly? Mes,
    int EansNoCatalogo);

/// <param name="Descricao">
/// Nome no catálogo da <b>IQVIA</b>, e não no da rede — por definição a rede não tem cadastro
/// deste produto. Nulo quando o relatório trouxe medida sem a linha de dimensão; aí a tela
/// mostra o próprio código, nunca célula vazia.
/// </param>
public sealed record OportunidadeDeSortimento(
    string Ean,
    string Brick,
    string? Descricao,
    string? Laboratorio,
    string? AreaFarmacia,
    string? Classe4,
    decimal UnidadesConcorrentes,
    decimal ValorCpp);
