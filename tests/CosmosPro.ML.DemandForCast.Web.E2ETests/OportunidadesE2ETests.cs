using Aspire.Hosting.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Playwright;

namespace CosmosPro.ML.DemandForCast.Web.E2ETests;

/// <summary>
/// A tela de oportunidades de sortimento (F16 parte C, grupo A, regra A3).
///
/// <para>
/// <b>Os dois estados que importam são opostos, e o segundo é o perigoso.</b> Com catálogo,
/// a tela lista o que o bairro vende e a rede não tem. <b>Sem</b> catálogo, todo produto do
/// mercado parece ausente do cadastro — e uma tela que listasse nesse estado estaria errada
/// em 100% das linhas. O que se afirma aqui é que ela explica em vez de listar.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class OportunidadesE2ETests(AppHostFixture fixture)
{
    private const string Rota = "/mercado/oportunidades";
    private const string Brick = "528-RJ VOLTA REDONDA RETIRO";
    private static readonly DateOnly Mes = new(2026, 6, 1);

    [Fact]
    public async Task Com_catalogo_a_tela_lista_e_declara_sobre_o_que_apurou()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearAsync(comCatalogo: true, ct);

        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            await IrParaAsync(page);

            var linhas = page.Locator("[data-test='linha-oportunidade']");
            await linhas.First.WaitForAsync(new() { Timeout = 60_000 });
            (await linhas.CountAsync()).Should().BeGreaterThan(0);

            // O painel declara mês, tamanho do catálogo e quantas oportunidades sobraram.
            // Sem isso, "3 oportunidades" pode ser a rede inteira ou um recorte, e o
            // comprador não tem como saber qual.
            var painel = page.Locator("[data-test='painel-oportunidades']");
            (await painel.CountAsync()).Should().Be(1);
            var texto = await painel.InnerTextAsync();
            texto.Should().Contain("06/2026", "o mês do mercado usado");

            // O tamanho do catálogo é a base da comparação: sem ele o comprador não sabe se
            // "2 oportunidades" saiu de um cadastro completo ou de um pedaço. Asserção pelo
            // data-test do campo, e não pela palavra "códigos" -- ela flexiona no singular, e
            // foi assim que este teste quebrou depois de eu corrigir o plural da tela.
            var cadastro = await page.Locator("[data-test='tamanho-do-catalogo']").InnerTextAsync();
            cadastro.Should().Contain("1 código", "a semeadura põe um EAN no catálogo");

            var oportunidades = await page.Locator("[data-test='total-de-oportunidades']").InnerTextAsync();
            oportunidades.Should().Contain("2", "três EANs no mercado, um deles já cadastrado");

            // A ressalva do código de barras é sempre visível: produto cadastrado sem código
            // aparece aqui como se não existisse no mix, e o comprador precisa saber disso
            // antes de incluir.
            var ressalva = page.Locator("[data-test='ressalva-codigo-de-barras']");
            (await ressalva.CountAsync()).Should().Be(1);
            (await ressalva.InnerTextAsync()).Should().Contain("sem");
        }
        finally { await page.CloseAsync(); }
    }

    [Fact]
    public async Task O_item_que_esta_no_catalogo_nao_aparece_como_oportunidade()
    {
        // A afirmação central da regra A1. A semeadura põe três EANs no mercado e um deles
        // no catálogo; a tela tem de mostrar dois.
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearAsync(comCatalogo: true, ct);

        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            await IrParaAsync(page);
            await page.Locator("[data-test='linha-oportunidade']").First
                      .WaitForAsync(new() { Timeout = 60_000 });

            var corpo = await page.Locator("[data-test='tabela-oportunidades']").InnerTextAsync();

            corpo.Should().Contain("7896714231143", "está no mercado e fora do cadastro");
            corpo.Should().Contain("7896523200576", "idem");
            corpo.Should().NotContain("7891721201806",
                "este está no catálogo da rede, então não é oportunidade de sortimento");
        }
        finally { await page.CloseAsync(); }
    }

    [Fact]
    public async Task Sem_catalogo_a_tela_explica_em_vez_de_listar()
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await SemearAsync(comCatalogo: false, ct);

        var page = await fixture.NovaPaginaLogadaAsync();
        try
        {
            await IrParaAsync(page);

            var aviso = page.Locator("[data-test='aviso-sem-catalogo']");
            await aviso.First.WaitForAsync(new() { Timeout = 60_000 });

            var texto = await aviso.InnerTextAsync();
            texto.Should().Contain("extrator", "o comprador precisa saber de onde o catálogo vem");
            texto.Should().Contain("0.18.0", "e qual versão traz");

            (await page.Locator("[data-test='linha-oportunidade']").CountAsync()).Should().Be(0,
                "listar sem catálogo seria errar em 100% das linhas: todo produto do mercado "
                + "pareceria ausente do cadastro, inclusive os que a rede vende");

            // E o painel de contexto não aparece — não há sobre o que declarar.
            (await page.Locator("[data-test='painel-oportunidades']").CountAsync()).Should().Be(0);
        }
        finally { await page.CloseAsync(); }
    }

    // --- apoio -----------------------------------------------------------------------

    private async Task IrParaAsync(IPage page)
    {
        await page.GotoAsync($"{fixture.WebfrontendUrl.TrimEnd('/')}{Rota}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Semeia mercado e (opcionalmente) catálogo na rede que a Web resolve para o usuário
    /// logado. Direto no banco: o caminho do XLSX e do ZIP já tem testes próprios, e o que
    /// se afirma aqui é o que a tela faz com o dado.
    /// </summary>
    private async Task<int> SemearAsync(bool comCatalogo, CancellationToken ct)
    {
        var connectionString = await fixture.App.GetConnectionStringAsync("engine", ct)
            ?? throw new InvalidOperationException("Recurso 'engine' sem connection string.");

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // A PRIMEIRA rede ativa por id, e não uma rede própria do teste. O E2E entra como
        // PowerUser, que não tem rede vinculada e cai justamente nesta -- semear numa rede
        // criada aqui deixaria a tela olhando outro inquilino, e ela renderiza o aviso de
        // "sem catálogo" com toda razão. Foi assim que este arquivo quebrou na primeira volta.
        int redeId;
        await using (var qual = conn.CreateCommand())
        {
            qual.CommandText = "SELECT TOP 1 Id FROM dbo.Redes WHERE Ativo = 1 ORDER BY Id;";
            redeId = (int?)await qual.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Nenhuma rede ativa no banco engine.");
        }

        // Zera o mercado e o catálogo desta rede: o volume do SQL é persistente e a rede é
        // reaproveitada entre execuções, então asserção de conteúdo passaria na primeira
        // execução e falharia na terceira.
        await ExecutarAsync(conn, """
            DELETE FROM dbo.MercadoObservacoes WHERE RedeId = @redeId;
            DELETE FROM dbo.MercadoProdutos   WHERE RedeId = @redeId;
            DELETE FROM dbo.RedeCatalogoEans  WHERE RedeId = @redeId;
            """, redeId, ct);

        (string Ean, string Nome, decimal Unidades)[] mercado =
        [
            ("7891721201806", "GLIFAGE XR COMPRIMIDO 500 MG X 30", 900m),
            ("7896714231143", "NEOSORO AD SOLUCAO NASAL 30 ML", 800m),
            ("7896523200576", "CIMEGRIPE CAPSULAS 400 MG X 20", 700m),
        ];

        foreach (var (ean, nome, unidades) in mercado)
        {
            await ExecutarAsync(conn, """
                INSERT INTO dbo.MercadoObservacoes
                    (RedeId, Mes, Brick, Bandeira, Ean, Unidades, ValorCpp)
                VALUES (@redeId, @mes, @brick, 'CONCORRENTES', @ean, @unidades, @valor);

                INSERT INTO dbo.MercadoProdutos
                    (RedeId, Ean, DescricaoLonga, Laboratorio, Molecula, AreaFarmacia, Nec1, Forma3, Classe4)
                VALUES (@redeId, @ean, @nome, 'LABORATORIO E2E', NULL, 'MIP', NULL, NULL, NULL);
                """, redeId, ct, p =>
            {
                p.AddWithValue("@mes", Mes.ToDateTime(TimeOnly.MinValue));
                p.AddWithValue("@brick", Brick);
                p.AddWithValue("@ean", ean);
                p.AddWithValue("@nome", nome);
                p.AddWithValue("@unidades", unidades);
                p.AddWithValue("@valor", unidades * 10m);
            });
        }

        if (comCatalogo)
        {
            // Um dos três EANs entra no catálogo: é ele que NÃO pode aparecer na tela.
            await ExecutarAsync(conn, """
                INSERT INTO dbo.RedeCatalogoEans (RedeId, Ean, Sku, Nome)
                VALUES (@redeId, '7891721201806', 'SKU-E2E-CAT', 'GLIFAGE JA CADASTRADO');
                """, redeId, ct);
        }

        return redeId;
    }

    private static async Task ExecutarAsync(
        SqlConnection conn, string sql, int redeId, CancellationToken ct,
        Action<SqlParameterCollection>? parametros = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@redeId", redeId);
        parametros?.Invoke(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
