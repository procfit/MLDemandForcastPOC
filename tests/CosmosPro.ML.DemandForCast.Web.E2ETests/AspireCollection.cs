namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// Coleção única para os cenários E2E.
/// <para>
/// O xUnit paraleliza classes, e cada <c>IClassFixture</c> subiria um AppHost próprio
/// sobre os mesmos containers persistentes do Aspire — dois orquestradores
/// simultâneos disputam DACPAC, migrations e portas, e os testes falham por
/// contenção em vez de por defeito. Aprendido na suíte de integração da F10.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class AspireCollection : ICollectionFixture<AppHostFixture>
{
    public const string Name = "aspire-e2e";
}
