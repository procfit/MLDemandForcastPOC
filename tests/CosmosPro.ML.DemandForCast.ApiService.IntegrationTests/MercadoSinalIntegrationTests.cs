using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Engine.Mercado;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// As sete colunas de mercado do item da sessão, contra banco real.
///
/// <para>
/// <b>Por que isto é teste de integração e não unitário:</b> o que se afirma aqui não é
/// lógica, é o que o SQL Server aceita e devolve. As precisões são o ponto — o default do
/// EF Core para <c>decimal</c> é <c>(18,2)</c>, que truncaria as três casas das unidades e
/// as quatro do índice <b>em silêncio</b>, sem falhar nenhum teste de unidade e sem nada na
/// tela denunciando. E a nulidade das sete é contrato: nulo significa "não foi possível
/// calcular", nunca zero, então um <c>DEFAULT 0</c> na migration transformaria toda linha
/// antiga numa afirmação falsa de que a IQVIA mediu zero.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class MercadoSinalIntegrationTests(AppHostFixture fixture)
{
    private const string Slug = "mercado-sinal";
    private static readonly DateOnly MesComparado = new(2025, 6, 1);
    private const string Brick = "528-RJ VOLTA REDONDA RETIRO";

    [Fact]
    public async Task As_sete_colunas_fazem_round_trip_sem_truncar()
    {
        var ct = TestContext.Current.CancellationToken;
        var (redeId, sessaoId) = await SemearSessaoAsync("round trip do sinal");

        await using (var db = await AbrirEngineAsync(ct))
        {
            db.ComparacaoSessaoItens.Add(new ComparacaoSessaoItem
            {
                SessaoId = sessaoId,
                LojaId = 12,
                Sku = "401882",
                CompraSugeridaPbs = 48m,
                VendidoNaJanela = 10m,
                DemandaDiaPbs = 1.5m,
                SobraPbsUnidades = 3m,
                MercadoMes = MesComparado,
                MercadoBrick = Brick,
                MercadoUnidadesRede = 1.234m,
                MercadoUnidadesConcorrentes = 5000.500m,
                MercadoIndiceDesempenho = 0.1234m,
                MercadoDiasSemEstoque = 3,
                MercadoAlerta = MercadoAlertas.Ruptura,
            });
            await db.SaveChangesAsync(ct);
        }

        await using var leitura = await AbrirEngineAsync(ct);
        var lido = await leitura.ComparacaoSessaoItens
            .SingleAsync(i => i.SessaoId == sessaoId && i.LojaId == 12 && i.Sku == "401882", ct);

        lido.MercadoMes.Should().Be(MesComparado);
        lido.MercadoBrick.Should().Be(Brick);

        // As três casas das unidades e as quatro do índice têm de voltar inteiras.
        lido.MercadoUnidadesRede.Should().Be(1.234m);
        lido.MercadoUnidadesConcorrentes.Should().Be(5000.500m);
        lido.MercadoIndiceDesempenho.Should().Be(0.1234m);

        lido.MercadoDiasSemEstoque.Should().Be(3);
        lido.MercadoAlerta.Should().Be(MercadoAlertas.Ruptura);
    }

    [Fact]
    public async Task Item_sem_dado_de_mercado_grava_nulo_e_nao_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, sessaoId) = await SemearSessaoAsync("item sem sinal");

        await using (var db = await AbrirEngineAsync(ct))
        {
            db.ComparacaoSessaoItens.Add(new ComparacaoSessaoItem
            {
                SessaoId = sessaoId,
                LojaId = 7,
                Sku = "118902",
                CompraSugeridaPbs = 0m,
                VendidoNaJanela = 0m,
                DemandaDiaPbs = 0m,
                SobraPbsUnidades = 0m,
                // Nenhuma coluna de mercado atribuída. Cinco causas levam aqui, todas
                // legítimas: loja sem CNPJ, CNPJ fora do painel, SKU sem EAN, EAN que a
                // IQVIA não reportou, e nenhum mês coberto antes do mês da sugestão.
            });
            await db.SaveChangesAsync(ct);
        }

        await using var leitura = await AbrirEngineAsync(ct);
        var lido = await leitura.ComparacaoSessaoItens
            .SingleAsync(i => i.SessaoId == sessaoId && i.LojaId == 7, ct);

        lido.MercadoMes.Should().BeNull();
        lido.MercadoBrick.Should().BeNull();
        lido.MercadoUnidadesRede.Should().BeNull();
        lido.MercadoUnidadesConcorrentes.Should().BeNull();
        lido.MercadoIndiceDesempenho.Should().BeNull();
        lido.MercadoDiasSemEstoque.Should().BeNull();
        lido.MercadoAlerta.Should().BeNull();
    }

    [Fact]
    public async Task Venda_nossa_zero_medida_e_diferente_de_ausencia_de_medida()
    {
        // A distinção que a tabela existe para preservar: zero é medição da IQVIA (o
        // bairro vende e a rede não vendeu nada), nulo é ausência de medição. As duas
        // linhas abaixo dizem coisas opostas ao comprador, e o banco tem de guardar as
        // duas sem colapsar uma na outra.
        var ct = TestContext.Current.CancellationToken;
        var (_, sessaoId) = await SemearSessaoAsync("zero medido vs ausente");

        await using (var db = await AbrirEngineAsync(ct))
        {
            db.ComparacaoSessaoItens.AddRange(
                new ComparacaoSessaoItem
                {
                    SessaoId = sessaoId, LojaId = 1, Sku = "MEDIDO-ZERO",
                    CompraSugeridaPbs = 0m, VendidoNaJanela = 0m,
                    DemandaDiaPbs = 0m, SobraPbsUnidades = 0m,
                    MercadoMes = MesComparado,
                    MercadoBrick = Brick,
                    MercadoUnidadesRede = 0m,
                    MercadoUnidadesConcorrentes = 500m,
                    MercadoIndiceDesempenho = 0m,
                    MercadoDiasSemEstoque = 0,
                    MercadoAlerta = MercadoAlertas.SemCausa,
                },
                new ComparacaoSessaoItem
                {
                    SessaoId = sessaoId, LojaId = 1, Sku = "SEM-MEDIDA",
                    CompraSugeridaPbs = 0m, VendidoNaJanela = 0m,
                    DemandaDiaPbs = 0m, SobraPbsUnidades = 0m,
                });
            await db.SaveChangesAsync(ct);
        }

        await using var leitura = await AbrirEngineAsync(ct);
        var medido = await leitura.ComparacaoSessaoItens
            .SingleAsync(i => i.SessaoId == sessaoId && i.Sku == "MEDIDO-ZERO", ct);
        var ausente = await leitura.ComparacaoSessaoItens
            .SingleAsync(i => i.SessaoId == sessaoId && i.Sku == "SEM-MEDIDA", ct);

        medido.MercadoUnidadesRede.Should().Be(0m);
        medido.MercadoIndiceDesempenho.Should().Be(0m);
        medido.MercadoAlerta.Should().Be(MercadoAlertas.SemCausa);

        ausente.MercadoUnidadesRede.Should().BeNull();
        ausente.MercadoIndiceDesempenho.Should().BeNull();
        ausente.MercadoAlerta.Should().BeNull();
    }

    [Fact]
    public async Task O_maior_valor_do_vocabulario_cabe_na_coluna()
    {
        // Se o tamanho declarado encolher abaixo do maior valor, quem descobre é o
        // SqlBulkCopy da materialização -- três fases depois de quem mudou o vocabulário.
        var ct = TestContext.Current.CancellationToken;
        var (_, sessaoId) = await SemearSessaoAsync("tamanho do alerta");

        var maior = new[]
        {
            MercadoAlertas.SemAlerta, MercadoAlertas.Ruptura,
            MercadoAlertas.SemCausa, MercadoAlertas.NaoApurado,
        }.MaxBy(v => v.Length)!;

        await using (var db = await AbrirEngineAsync(ct))
        {
            db.ComparacaoSessaoItens.Add(new ComparacaoSessaoItem
            {
                SessaoId = sessaoId, LojaId = 9, Sku = "ALERTA-MAIOR",
                CompraSugeridaPbs = 0m, VendidoNaJanela = 0m,
                DemandaDiaPbs = 0m, SobraPbsUnidades = 0m,
                MercadoAlerta = maior,
            });
            await db.SaveChangesAsync(ct);
        }

        await using var leitura = await AbrirEngineAsync(ct);
        var lido = await leitura.ComparacaoSessaoItens
            .SingleAsync(i => i.SessaoId == sessaoId && i.Sku == "ALERTA-MAIOR", ct);

        lido.MercadoAlerta.Should().Be(maior, "o texto não pode voltar truncado");
    }

    // --- semeadura ------------------------------------------------------------------

    private async Task<(int RedeId, Guid SessaoId)> SemearSessaoAsync(string nome)
    {
        var ct = TestContext.Current.CancellationToken;
        var redeId = await EnsureRedeAsync("Rede do sinal de mercado", Slug);

        var sessaoId = Guid.CreateVersion7();
        var agora = DateTimeOffset.UtcNow;

        await using var db = await AbrirEngineAsync(ct);
        db.ComparacaoSessoes.Add(new ComparacaoSessao
        {
            Id = sessaoId,
            RedeId = redeId,
            Nome = nome,
            Status = SessaoStatus.Comparando,
            CriadoEm = agora,
            AtualizadoEm = agora,
            SugestaoId = 125527,
            SugestaoDataHora = new DateTime(2026, 6, 10, 9, 0, 0),
            SugestaoTipoCalculo = 2,
        });
        await db.SaveChangesAsync(ct);

        return (redeId, sessaoId);
    }

    private async Task<EngineDbContext> AbrirEngineAsync(CancellationToken ct)
    {
        var connStr = await fixture.GetEngineConnectionStringAsync(ct);
        var options = new DbContextOptionsBuilder<EngineDbContext>().UseSqlServer(connStr).Options;
        return new EngineDbContext(options);
    }

    private async Task<int> EnsureRedeAsync(string nome, string slug)
    {
        var criacao = await fixture.RedesApi.CreateAsync(new CreateRedeRequest(nome, slug));
        if (criacao.IsSuccessStatusCode)
        {
            return criacao.Content!.Id;
        }

        criacao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "só conflito de slug é aceitável aqui; outro status é falha real");

        var lista = await fixture.RedesApi.ListAsync();
        lista.IsSuccessStatusCode.Should().BeTrue();
        var existente = lista.Content!.SingleOrDefault(r => r.Slug == slug);
        existente.Should().NotBeNull($"rede '{slug}' deu 409 mas não apareceu na listagem");
        return existente!.Id;
    }
}
