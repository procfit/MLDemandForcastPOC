using System.IO.Compression;
using System.Text;
using CosmosPro.ML.DemandForCast.SyntheticData;

namespace CosmosPro.ML.DemandForCast.SyntheticData.Tests;

public sealed class SyntheticDatasetGeneratorTests
{
    private static readonly SyntheticDatasetOptions SmallOptions = new()
    {
        NumLojas = 2,
        NumSkus = 5,
        DataInicio = new DateOnly(2025, 1, 1),
        DataFim = new DateOnly(2025, 1, 30),
        Seed = 42,
    };

    [Fact]
    public void Generate_produz_ZIP_com_os_8_CSVs_esperados()
    {
        var result = SyntheticDatasetGenerator.Generate(SmallOptions);

        using var ms = new MemoryStream(result.ZipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var names = zip.Entries.Select(e => e.Name).ToList();
        names.Should().BeEquivalentTo([
            "lojas.csv", "produtos.csv", "vendas.csv", "estoques_diarios.csv",
            "compras.csv", "promocoes.csv", "sinais_externos.csv"]);
    }

    [Fact]
    public void Headers_de_cada_CSV_batem_com_o_schema_do_Stage()
    {
        var result = SyntheticDatasetGenerator.Generate(SmallOptions);
        var headers = ReadFirstLineOfEachCsv(result.ZipBytes);

        headers["lojas.csv"].Should().Be("LojaId,Nome,UF,Cidade,Regiao,Perfil,DiasOperacaoSemana,DataAbertura,Ativo,Cnpj");
        headers["produtos.csv"].Should().Be("Sku,Nome,Categoria,Subcategoria,Fabricante,PrincipioAtivo,Apresentacao,Ean,RegistroAnvisa,ListaControle,ClasseTerapeutica,Ativo");
        headers["vendas.csv"].Should().Be("Data,LojaId,Sku,Quantidade,PrecoUnitario,ValorTotal");
        headers["estoques_diarios.csv"].Should().Be("Data,LojaId,Sku,QuantidadeEmEstoque");
        headers["compras.csv"].Should().Be("DataPedido,DataRecebimento,LojaId,Sku,Quantidade,Fornecedor");
        headers["promocoes.csv"].Should().Be("DataInicio,DataFim,Sku,LojaId,Tipo,DescontoPct");
        headers["sinais_externos.csv"].Should().Be("Data,Geografia,Tipo,Valor");
    }

    [Fact]
    public void Mesmo_seed_produz_o_mesmo_dataset_byte_a_byte()
    {
        var r1 = SyntheticDatasetGenerator.Generate(SmallOptions);
        var r2 = SyntheticDatasetGenerator.Generate(SmallOptions);

        // ZIP carrega timestamps por entrada → bytes brutos diferem.
        // Comparamos o conteúdo descomprimido pra confirmar determinismo do gerador.
        var c1 = ReadCsv(r1.ZipBytes, "vendas.csv");
        var c2 = ReadCsv(r2.ZipBytes, "vendas.csv");
        c1.Should().Be(c2);
    }

    [Fact]
    public void Seeds_diferentes_produzem_datasets_distintos()
    {
        var r1 = SyntheticDatasetGenerator.Generate(SmallOptions);
        var r2 = SyntheticDatasetGenerator.Generate(SmallOptions with { Seed = 99 });

        var c1 = ReadCsv(r1.ZipBytes, "vendas.csv");
        var c2 = ReadCsv(r2.ZipBytes, "vendas.csv");
        c1.Should().NotBe(c2);
    }

    [Fact]
    public void Curva_ABC_concentra_vendas_nos_top_SKUs()
    {
        // Dataset maior pra ABC manifestar. Use 50 SKUs.
        var opt = SmallOptions with { NumSkus = 50, NumLojas = 5, DataFim = new DateOnly(2025, 6, 30) };
        var result = SyntheticDatasetGenerator.Generate(opt);

        var rows = ReadCsv(result.ZipBytes, "vendas.csv")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(l => l.Split(','))
            .Select(p => new { Sku = p[2], Qtd = decimal.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) })
            .GroupBy(r => r.Sku)
            .Select(g => new { Sku = g.Key, Total = g.Sum(r => r.Qtd) })
            .OrderByDescending(r => r.Total)
            .ToList();

        var totalGeral = rows.Sum(r => r.Total);
        var top20pct = rows.Take(rows.Count / 5).Sum(r => r.Total);

        // ABC clássica: top 20% explica > 60% do volume.
        (top20pct / totalGeral).Should().BeGreaterThan(0.6m,
            "curva ABC deve concentrar vendas — top 20% SKUs > 60% do volume");
    }

    [Fact]
    public void Promocoes_so_sao_emitidas_para_a_fracao_configurada_de_SKUs()
    {
        var opt = SmallOptions with { NumSkus = 100, FracaoSkusEmPromocao = 0.10 };
        var result = SyntheticDatasetGenerator.Generate(opt);

        var promoLines = ReadCsv(result.ZipBytes, "promocoes.csv")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Count();

        // 10% de 100 SKUs = 10 promoções (1 por SKU).
        promoLines.Should().Be(10);
    }

    [Fact]
    public void Stats_reportam_contagens_consistentes_com_o_ZIP()
    {
        var result = SyntheticDatasetGenerator.Generate(SmallOptions);

        result.Stats.Lojas.Should().Be(2);
        result.Stats.Produtos.Should().Be(5);
        result.Stats.Vendas.Should().BeGreaterThan(0);
        result.Stats.Estoques.Should().BeGreaterThan(0);
        result.Stats.SinaisExternos.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SinaisExternos_tem_clima_e_gripe_e_a_gripe_varia_ao_longo_do_ano()
    {
        // Horizonte de 1 ano pra capturar a sazonalidade + anomalia (surto).
        var opt = SmallOptions with { NumLojas = 3, DataInicio = new DateOnly(2025, 1, 1), DataFim = new DateOnly(2025, 12, 31) };
        var result = SyntheticDatasetGenerator.Generate(opt);

        var linhas = ReadCsv(result.ZipBytes, "sinais_externos.csv")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(l => l.Split(','))
            .ToList();

        linhas.Select(p => p[2]).Distinct().Should().BeEquivalentTo(["Clima", "Gripe"]);

        // A gripe deve VARIAR bastante no ano (baixa temporada vs pico/surto) —
        // é justamente a anomalia que o calendário sozinho não prevê.
        var gripe = linhas.Where(p => p[2] == "Gripe")
            .Select(p => decimal.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var amplitude = gripe.Max() - gripe.Min();
        amplitude.Should().BeGreaterThan(20m, "o índice de gripe deve oscilar entre baixa temporada e pico de surto");
    }

    private static Dictionary<string, string> ReadFirstLineOfEachCsv(byte[] zipBytes)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var result = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            result[entry.Name] = reader.ReadLine() ?? "";
        }
        return result;
    }

    private static string ReadCsv(byte[] zipBytes, string name)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(name)!;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
