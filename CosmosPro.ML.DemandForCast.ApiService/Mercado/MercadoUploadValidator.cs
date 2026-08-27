using System.IO.Compression;
using System.Xml;

namespace CosmosPro.ML.DemandForCast.ApiService.Mercado;

/// <summary>
/// Validação superficial do XLSX da IQVIA no upload: é um XLSX de verdade e tem a aba
/// de dados "QUERY". Não lê a planilha de dados (a real tem ~60 MB de XML) — o contrato
/// de colunas é responsabilidade do Worker, que falha com o cabeçalho ofensor na mensagem.
/// </summary>
internal static class MercadoUploadValidator
{
    public static IReadOnlyList<string> Validate(Stream xlsx)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(xlsx, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or EndOfStreamException or IOException)
        {
            return [$"Arquivo não é um XLSX válido: {ex.Message}"];
        }

        using (zip)
        {
            var workbook = zip.GetEntry("xl/workbook.xml");
            if (workbook is null)
            {
                return ["Arquivo não é um XLSX válido (xl/workbook.xml ausente). Envie o relatório da IQVIA sem converter o formato."];
            }

            var abas = new List<string>();
            try
            {
                using var stream = workbook.Open();
                using var reader = XmlReader.Create(stream);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" &&
                        reader.GetAttribute("name") is { } nome)
                    {
                        abas.Add(nome);
                    }
                }
            }
            catch (XmlException ex)
            {
                return [$"Arquivo não é um XLSX válido (workbook ilegível): {ex.Message}"];
            }

            if (!abas.Contains("QUERY", StringComparer.OrdinalIgnoreCase))
            {
                return [$"O arquivo não tem a aba 'QUERY' com os dados de mercado. Abas encontradas: {string.Join(", ", abas)}. " +
                        "Envie o relatório mensal da IQVIA sem renomear as abas."];
            }
        }

        return [];
    }
}
