using CosmosPro.ML.DemandForCast.ApiService.Mercado;
using CosmosPro.ML.DemandForCast.Tests.Shared.Xlsx;

namespace CosmosPro.ML.DemandForCast.ApiService.Tests;

public sealed class MercadoUploadValidatorTests
{
    [Fact]
    public void Xlsx_com_aba_QUERY_passa()
    {
        using var xlsx = new IqviaXlsxBuilder()
            .WithColunas("Ean", "Produto Desc Longa",
                IqviaXlsxBuilder.Medida("B1", "CONCORRENTES", "202506", "Unidades"))
            .Build();

        var erros = MercadoUploadValidator.Validate(xlsx);

        erros.Should().BeEmpty();
    }

    [Fact]
    public void Arquivo_que_nao_e_zip_e_recusado()
    {
        using var lixo = new MemoryStream([1, 2, 3, 4, 5]);

        var erros = MercadoUploadValidator.Validate(lixo);

        erros.Should().ContainSingle().Which.Should().Contain("XLSX");
    }

    [Fact]
    public void Xlsx_sem_aba_QUERY_e_recusado_listando_as_abas()
    {
        using var xlsx = new IqviaXlsxBuilder(abaDados: "Relatorio")
            .WithColunas("Ean", "Produto Desc Longa")
            .Build();

        var erros = MercadoUploadValidator.Validate(xlsx);

        erros.Should().ContainSingle().Which.Should().Contain("QUERY").And.Contain("Relatorio");
    }

    [Fact]
    public void Zip_sem_workbook_e_recusado()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("qualquer.txt").Open());
            w.Write("x");
        }
        ms.Position = 0;

        var erros = MercadoUploadValidator.Validate(ms);

        erros.Should().ContainSingle().Which.Should().Contain("workbook");
    }
}
