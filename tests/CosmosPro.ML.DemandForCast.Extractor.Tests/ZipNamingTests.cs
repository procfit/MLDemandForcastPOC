using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// <see cref="ZipNaming"/> é a fonte única do nome do ZIP: <c>ExtractionService.Run</c> grava
/// nele, e <c>MainForm.ExtrairAsync</c> precisa do mesmo nome para checar se o arquivo já
/// existe antes de perguntar (ver CHANGE 3). Duplicar a regra deixaria os dois discordarem
/// sobre qual arquivo está em jogo.
/// </summary>
public sealed class ZipNamingTests
{
    [Fact]
    public void Mesmo_minuto_produz_o_mesmo_caminho()
    {
        var t1 = new DateTime(2026, 8, 10, 14, 32, 0);
        var t2 = new DateTime(2026, 8, 10, 14, 32, 59);

        ZipNaming.BuildPath(@"C:\saida", t1).Should().Be(ZipNaming.BuildPath(@"C:\saida", t2));
    }

    [Fact]
    public void Minutos_diferentes_produzem_caminhos_diferentes()
    {
        var t1 = new DateTime(2026, 8, 10, 14, 32, 59);
        var t2 = new DateTime(2026, 8, 10, 14, 33, 0);

        ZipNaming.BuildPath(@"C:\saida", t1).Should().NotBe(ZipNaming.BuildPath(@"C:\saida", t2));
    }

    [Fact]
    public void Nome_segue_o_padrao_extracao_pbs_com_data_e_hora()
    {
        var caminho = ZipNaming.BuildPath(@"C:\saida", new DateTime(2026, 8, 10, 14, 32, 0));

        caminho.Should().Be(Path.Combine(@"C:\saida", "extracao-pbs_20260810-1432.zip"));
    }

    [Fact]
    public void Pasta_de_saida_diferente_produz_caminho_diferente_mesmo_no_mesmo_minuto()
    {
        var quando = new DateTime(2026, 8, 10, 14, 32, 0);

        ZipNaming.BuildPath(@"C:\saida-a", quando).Should().NotBe(ZipNaming.BuildPath(@"C:\saida-b", quando));
    }

    /// <summary>
    /// Não prova que <c>ExtractionService.Run</c> deixou de recomputar a regra por conta
    /// própria -- isso este teste não alcança sem banco disponível. O que ele pina é só a
    /// regra em si: <c>BuildPath</c> é função pura de pasta e instante, sem estado
    /// escondido. Quem garante que <c>Run</c> chama exatamente esta função, com o instante
    /// que veio de <c>ExtractionRequest.Instante</c> em vez de recalcular o seu, é a leitura
    /// de ExtractionService.cs (ver <see cref="ExtractionService.ResolverInstante"/>) --
    /// não este teste.
    /// </summary>
    [Fact]
    public void Regra_e_pura_funcao_de_pasta_e_instante()
    {
        var quando = new DateTime(2026, 1, 1, 0, 0, 0);

        ZipNaming.BuildPath(@"C:\x", quando).Should().Be(@"C:\x\extracao-pbs_20260101-0000.zip");
    }
}
