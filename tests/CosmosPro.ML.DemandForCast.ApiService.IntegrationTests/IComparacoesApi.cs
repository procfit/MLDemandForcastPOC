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

    [Get("/api/comparacoes/{id}/itens")]
    Task<IApiResponse<SessaoItensPage>> ItensAsync(
        Guid id,
        [Query] int redeId,
        [Query] int skip = 0,
        [Query] int take = 25,
        [Query] string? orderBy = null,
        [Query] bool desc = true,
        CancellationToken ct = default);

    [Get("/api/comparacoes/{id}/analise")]
    Task<IApiResponse<SessaoAnaliseResposta>> AnaliseAsync(
        Guid id, [Query] int redeId, CancellationToken ct = default);
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
    string? ResultadoJson = null);

public sealed record SessaoItensPage(
    int Total,
    string OrderBy,
    bool Desc,
    List<SessaoItemResposta> Itens);

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
    bool JanelaAlemDoHistorico);

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
