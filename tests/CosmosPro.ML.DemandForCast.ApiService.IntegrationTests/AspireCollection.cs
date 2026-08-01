namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// Coleção única para todas as classes de teste de integração.
/// <para>
/// Motivo: o xUnit paraleliza classes de teste, e cada <c>IClassFixture</c> subiria
/// um AppHost próprio. Como os containers do Aspire são <b>persistentes e
/// compartilhados</b> (mesmo SQL Server e MinIO), dois AppHosts
/// simultâneos disputam publicação de DACPAC, migrations e portas — e os testes
/// falham por contenção, não por defeito no código.
/// </para>
/// <para>
/// Com <c>ICollectionFixture</c> as classes compartilham <b>um</b> AppHost e rodam
/// em sequência. Custa serialização, mas é o único arranjo determinístico.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class AspireCollection : ICollectionFixture<AppHostFixture>
{
    public const string Name = "aspire";
}
