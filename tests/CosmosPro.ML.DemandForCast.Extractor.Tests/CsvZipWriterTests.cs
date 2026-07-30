using System.Globalization;
using System.IO.Compression;
using System.Text;
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class CsvZipWriterTests
{
    private static string[] EscreverEler(string entryName, string[] header, Action<CsvEntryWriter> escrita)
    {
        var buffer = new MemoryStream();
        using (var zip = new CsvZipWriter(buffer))
        using (var entry = zip.CreateEntry(entryName, header))
        {
            escrita(entry);
        }

        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(entryName)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Escreve_header_na_ordem_recebida()
    {
        var linhas = EscreverEler("t.csv", ["A", "B", "C"], _ => { });

        linhas.Should().ContainSingle();
        linhas[0].Should().Be("A,B,C");
    }

    [Fact]
    public void Formata_valores_com_cultura_invariante()
    {
        var anterior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
        try
        {
            var linhas = EscreverEler("t.csv", ["Data", "Valor", "Flag"], entry =>
                entry.WriteRow(new DateOnly(2026, 3, 9), 1234.5678m, true));

            // Sem isto o locale pt-BR sairia com vírgula decimal e data dd/MM/yyyy,
            // e o reader do Worker (InvariantCulture) quebraria.
            linhas[1].Should().Be("2026-03-09,1234.5678,1");
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }

    [Fact]
    public void Null_vira_campo_vazio()
    {
        var linhas = EscreverEler("t.csv", ["A", "B", "C"], entry => entry.WriteRow("x", null, 3));

        linhas[1].Should().Be("x,,3");
    }

    [Fact]
    public void Escapa_virgula_aspas_e_quebra_de_linha()
    {
        var linhas = EscreverEler("t.csv", ["A", "B"], entry =>
            entry.WriteRow("tem,virgula", "tem\"aspas"));

        linhas[1].Should().Be("\"tem,virgula\",\"tem\"\"aspas\"");
    }

    [Fact]
    public void Bool_falso_vira_zero()
    {
        var linhas = EscreverEler("t.csv", ["Ativo"], entry => entry.WriteRow(false));

        linhas[1].Should().Be("0");
    }

    [Fact]
    public void Conta_as_linhas_escritas()
    {
        var buffer = new MemoryStream();
        using var zip = new CsvZipWriter(buffer);
        using var entry = zip.CreateEntry("t.csv", ["A"]);

        entry.WriteRow(1);
        entry.WriteRow(2);

        entry.RowCount.Should().Be(2);
    }

    [Fact]
    public void Datetime_sai_no_formato_iso_sem_hora()
    {
        var linhas = EscreverEler("t.csv", ["Data"], entry =>
            entry.WriteRow(new DateTime(2026, 12, 31, 23, 45, 0, DateTimeKind.Unspecified)));

        linhas[1].Should().Be("2026-12-31");
    }

    [Fact]
    public void WriteText_grava_conteudo_solto_sem_formatacao_de_csv()
    {
        var buffer = new MemoryStream();
        using (var zip = new CsvZipWriter(buffer))
        {
            zip.WriteText("manifesto.json", """{"a":1}""");
        }

        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("manifesto.json")!.Open(), Encoding.UTF8);

        reader.ReadToEnd().Should().Be("""{"a":1}""");
    }
}
