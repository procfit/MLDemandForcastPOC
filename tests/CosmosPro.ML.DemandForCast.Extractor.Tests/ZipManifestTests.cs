using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class ZipManifestTests
{
    [Fact]
    public void Manifesto_roundtrip_preserva_a_sugestao_e_a_janela()
    {
        var m = new ZipManifest(21217, "MATTEL", new DateTime(2026, 3, 10, 10, 27, 0), 2,
                                new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "1.0.0", 3);

        var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

        volta.Should().BeEquivalentTo(m);
    }

    [Fact]
    public void Descricao_nula_e_preservada_no_roundtrip()
    {
        var m = new ZipManifest(1, null, new DateTime(2026, 1, 1), 1,
                                new DateOnly(2025, 1, 1), new DateOnly(2026, 2, 1), "1.0.0", 0);

        var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

        volta.Should().BeEquivalentTo(m);
    }

    [Fact]
    public void Serializacao_grava_a_data_da_janela_no_formato_ISO_e_o_campo_de_skus_sem_cadastro()
    {
        // Pino o texto literal do JSON (e não só o roundtrip) porque é isto que um
        // outro serviço (a sessão de comparação F14) vai parsear — um roundtrip
        // sozinho passaria mesmo se a serialização mudasse de casing ou formato
        // de data sem ninguém perceber.
        var m = new ZipManifest(21217, "MATTEL", new DateTime(2026, 3, 10, 10, 27, 0), 2,
                                new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "1.0.0", 3);

        var json = ZipManifest.Escrever(m);

        json.Should().Contain("\"JanelaInicio\": \"2025-03-10\"");
        json.Should().Contain("\"SkusSemCadastro\": 3");
    }

    [Fact]
    public void Ler_falha_se_faltar_o_SugestaoId()
    {
        // SugestaoId é a chave que amarra o ZIP à sessão de comparação (F14) — sem
        // ele o manifesto não serve para nada, e o erro precisa aparecer aqui, não
        // como um NullReferenceException silencioso lá na frente.
        const string semSugestaoId = """{"SugestaoDescricao":"X","VersaoExtractor":"1.0.0"}""";

        var acao = () => ZipManifest.Ler(semSugestaoId);

        acao.Should().Throw<InvalidOperationException>();
    }
}
