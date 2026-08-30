using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Contrato Refit dos endpoints de comparação. Mesmo papel que <see cref="IImportsApi"/>:
/// camada tipada por cima do HttpClient do Aspire, usada no "Act" dos testes.
/// </summary>
public interface IComparacoesApi
{
    [Post("/api/comparacoes")]
    Task<IApiResponse<SessaoView>> CreateAsync(
        [Body] CreateSessaoRequest request, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/comparacoes/{id}")]
    Task<IApiResponse<SessaoView>> GetAsync(Guid id, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/comparacoes")]
    Task<IApiResponse<List<SessaoView>>> ListAsync(
        [Query] int redeId, [Query] int take = 50, CancellationToken ct = default);

    [Multipart]
    [Post("/api/comparacoes/{id}/dados")]
    Task<IApiResponse> UploadDadosAsync(
        Guid id,
        [AliasAs("file")] StreamPart file,
        [Query] int redeId,
        CancellationToken ct = default);

    /// <remarks>
    /// <see cref="HttpResponseMessage"/> cru, e não <c>IApiResponse&lt;byte[]&gt;</c>: o teste
    /// afirma o nome do arquivo, que vem no <c>Content-Disposition</c>, e precisa do 404 sem
    /// exceção.
    /// </remarks>
    [Get("/api/comparacoes/{id}/dados")]
    Task<HttpResponseMessage> DownloadDadosAsync(
        Guid id, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/comparacoes/{id}/itens")]
    Task<IApiResponse<SessaoItensPage>> ItensAsync(
        Guid id,
        [Query] int redeId,
        [Query] int skip = 0,
        [Query] int take = 25,
        [Query] string? orderBy = null,
        [Query] bool desc = true,
        [Query] int? lojaId = null,
        [Query] string? categoria = null,
        [Query] string? curva = null,
        [Query] bool? somenteComAlerta = null,
        CancellationToken ct = default);

    [Get("/api/comparacoes/{id}/itens/filtros")]
    Task<IApiResponse<FiltrosDisponiveis>> FiltrosDosItensAsync(
        Guid id, [Query] int redeId, CancellationToken ct = default);

    [Get("/api/comparacoes/{id}/itens/exportacao")]
    Task<IApiResponse<List<SessaoItemResposta>>> ExportarItensAsync(
        Guid id,
        [Query] int redeId,
        [Query] int? lojaId = null,
        [Query] string? categoria = null,
        [Query] string? curva = null,
        [Query] bool? somenteComAlerta = null,
        CancellationToken ct = default);

    [Get("/api/comparacoes/{id}/analise")]
    Task<IApiResponse<SessaoAnaliseResposta>> AnaliseAsync(
        Guid id, [Query] int redeId, CancellationToken ct = default);

    [Delete("/api/comparacoes/{id}")]
    Task<IApiResponse> ExcluirAsync(Guid id, [Query] int redeId, CancellationToken ct = default);
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
    bool DadosEnviados = false);

public sealed record SessaoItensPage(
    int Total,
    string OrderBy,
    bool Desc,
    List<SessaoItemResposta> Itens,
    int TotalSemFiltro = 0,
    TotaisDosItens? Totais = null);

public sealed record TotaisDosItens(
    int Itens,
    decimal CompraPbsUnidades,
    decimal? CompraMlUnidades,
    int ItensComCompraMl,
    decimal VendidoNaJanela,
    decimal SobraPbsUnidades,
    decimal? SobraMlUnidades,
    int ItensComSobraMl,
    decimal? SobraPbsValor,
    int ItensComValorPbs,
    decimal? SobraMlValor,
    int ItensComValorMl,
    int ItensComDadoDeMercado = 0);

public sealed record FiltrosDisponiveis(
    List<int> Lojas,
    List<string> Categorias,
    bool TemItemSemCategoria,
    List<string> Curvas,
    bool TemItemSemCurva);

public sealed record SessaoItemResposta(
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
    DateOnly? MercadoMes = null,
    string? MercadoBrick = null,
    decimal? MercadoUnidadesRede = null,
    decimal? MercadoUnidadesConcorrentes = null,
    decimal? MercadoIndiceDesempenho = null,
    int? MercadoDiasSemEstoque = null,
    string? MercadoAlerta = null);

public sealed record SessaoAnaliseResposta(
    int Itens,
    List<SessaoFatiaResposta> PorCurva,
    List<SessaoFatiaResposta> PorLoja,
    int ItensComDecisaoMl,
    int ItensComSobraMlMaior,
    decimal SobraExtraMlUnidades,
    decimal SobraExtraMlValor,
    List<ItemPiorResposta> PioresNaCompra,
    List<ItemPiorResposta> PioresNaPrevisao);

public sealed record SessaoFatiaResposta(
    string Chave,
    int Itens,
    int ItensComPrevisaoMl,
    decimal SomaDemandaRealDiaria,
    decimal SomaErroAbsPbs,
    decimal SomaErroAbsMl,
    int VitoriasMl,
    int VitoriasPbs);

public sealed record ItemPiorResposta(
    int LojaId,
    string Sku,
    string? NomeProduto,
    decimal? SobraPbsUnidades,
    decimal? SobraMlUnidades,
    decimal? ErroPbs,
    decimal? ErroMl,
    bool JanelaAlemDoHistorico);
