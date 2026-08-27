using CosmosPro.ML.DemandForCast.ApiService.Imports;
using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Prova que o ZIP produzido pelo extrator é aceito pelo mesmo validador que a
/// UI de importação usa. Sem isto, o contrato só estaria conferido "no papel"
/// (nomes de coluna) e um detalhe de escrita — encoding, header ausente,
/// separador — só apareceria na hora de importar de verdade.
/// </summary>
public sealed class ImportCompatibilityTests
{
    private static MemoryStream GerarZip(bool comLinhas)
    {
        var buffer = new MemoryStream();
        using (var zip = new CsvZipWriter(buffer))
        {
            foreach (var arquivo in StageContract.WriteOrder)
            {
                using var entry = zip.CreateEntry(arquivo, StageContract.Headers[arquivo]);
                if (comLinhas)
                {
                    // Uma linha qualquer só para o arquivo não ficar apenas com header.
                    entry.WriteRow([.. StageContract.Headers[arquivo].Select(object? (_) => "1")]);
                }
            }
        }
        return new MemoryStream(buffer.ToArray());
    }

    [Fact]
    public void Zip_do_extrator_passa_no_validador_do_import()
    {
        using var zip = GerarZip(comLinhas: true);

        var resultado = ImportValidator.Validate(zip);

        resultado.Errors.Should().BeEmpty();
        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Zip_com_arquivos_apenas_header_tambem_e_aceito()
    {
        // Um recorte sem movimento (loja nova, período curto) produz CSVs só com header.
        using var zip = GerarZip(comLinhas: false);

        var resultado = ImportValidator.Validate(zip);

        resultado.Errors.Should().BeEmpty();
        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Zip_anterior_a_F16_com_mercado_iqvia_ainda_e_aceito()
    {
        // O arquivo saiu do contrato (o dado de mercado é importado à parte desde a
        // F16), mas todo ZIP gerado antes disso o traz — entrada desconhecida é
        // ignorada, não recusada, senão nenhum envio antigo poderia ser reexecutado.
        var buffer = new MemoryStream();
        using (var zip = new CsvZipWriter(buffer))
        {
            foreach (var arquivo in StageContract.WriteOrder)
            {
                using var entry = zip.CreateEntry(arquivo, StageContract.Headers[arquivo]);
            }
            using (zip.CreateEntry("mercado_iqvia.csv",
                ["Mes", "PrincipioAtivo", "UF", "DemandaMercadoUnidades", "MarketShareCategoria"])) { }
        }

        using var antigo = new MemoryStream(buffer.ToArray());
        var resultado = ImportValidator.Validate(antigo);

        resultado.Errors.Should().BeEmpty();
        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validador_reprova_se_faltar_arquivo_obrigatorio()
    {
        // Guarda contra o teste acima virar vacuamente verdadeiro caso o
        // validador pare de exigir os arquivos.
        var buffer = new MemoryStream();
        using (var zip = new CsvZipWriter(buffer))
        {
            using var entry = zip.CreateEntry(StageContract.Lojas, StageContract.Headers[StageContract.Lojas]);
        }

        using var incompleto = new MemoryStream(buffer.ToArray());
        var resultado = ImportValidator.Validate(incompleto);

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().NotBeEmpty();
    }
}
