using CosmosPro.ML.DemandForCast.Worker.Sessoes;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// O ZIP declara, na própria raiz, qual sugestão do PBS ele traz — é assim que a sessão
/// de comparação se vincula ao upload sem o comprador digitar nada. Estes testes cobrem
/// as duas metades da leitura: o retrato que segue para a sessão, e a recusa quando o
/// arquivo enviado não pode sustentar comparação nenhuma.
///
/// <para>
/// A recusa é <b>resposta</b>, não erro: o motivo é lido por um comprador de farmácia, e
/// por isso cada caso afirma também que o texto diz o que fazer sem citar nome de
/// arquivo, extensão ou campo técnico.
/// </para>
/// </summary>
public sealed class ManifestoLeitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"manifesto-teste-{Guid.NewGuid():N}");

    public ManifestoLeitorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* diretório temporário; sujeira não invalida o teste */ }
    }

    [Fact]
    public void Manifesto_presente_devolve_a_sugestao_e_o_retrato_dela()
    {
        Escrever("""
            {
              "SugestaoId": 21217,
              "SugestaoDescricao": "MATTEL",
              "SugestaoDataHora": "2026-03-10T10:27:00",
              "SugestaoTipoCalculo": 2,
              "JanelaInicio": "2025-03-10",
              "JanelaFim": "2026-04-09",
              "VersaoExtractor": "1.0.0",
              "SkusSemCadastro": 3
            }
            """);

        var leitura = ManifestoLeitor.Ler(_dir);

        leitura.MotivoInviabilidade.Should().BeNull();
        leitura.Manifesto.Should().NotBeNull();
        leitura.Manifesto!.SugestaoId.Should().Be(21217);
        leitura.Manifesto.SugestaoDescricao.Should().Be("MATTEL");
        leitura.Manifesto.SugestaoDataHora.Should().Be(new DateTime(2026, 3, 10, 10, 27, 0),
            "o corte anti-vazamento do treino da sessão é derivado desta data");
        leitura.Manifesto.SugestaoTipoCalculo.Should().Be(2,
            "o ERP tem dois métodos de cálculo e a comparação precisa saber contra qual disputa");
        leitura.Manifesto.JanelaInicio.Should().Be(new DateOnly(2025, 3, 10));
        leitura.Manifesto.JanelaFim.Should().Be(new DateOnly(2026, 4, 9));
        leitura.Manifesto.VersaoExtractor.Should().Be("1.0.0");
        leitura.Manifesto.SkusSemCadastro.Should().Be(3);
    }

    [Fact]
    public void Envio_sem_declaracao_de_sugestao_e_inviavel()
    {
        var leitura = ManifestoLeitor.Ler(_dir);

        leitura.Manifesto.Should().BeNull();
        MotivoAcionavelPorComprador(leitura.MotivoInviabilidade);
        leitura.MotivoInviabilidade.Should().Contain("sugestão de compra");
    }

    [Fact]
    public void Declaracao_danificada_e_inviavel_e_nao_estoura_excecao()
    {
        Escrever("{ isto nao e conteudo valido");

        var leitura = ManifestoLeitor.Ler(_dir);

        leitura.Manifesto.Should().BeNull();
        MotivoAcionavelPorComprador(leitura.MotivoInviabilidade);
        leitura.MotivoInviabilidade.Should().Contain("danificad",
            "arquivo ilegível é envio inutilizável, não defeito nosso — e o remédio é outro: gerar de novo");
    }

    [Fact]
    public void Declaracao_sem_identificacao_da_sugestao_e_inviavel()
    {
        // Manifesto bem formado, mas sem SugestaoId: nada amarra o envio a uma sugestão.
        Escrever("""
            {
              "SugestaoDescricao": "MATTEL",
              "SugestaoDataHora": "2026-03-10T10:27:00",
              "SugestaoTipoCalculo": 2,
              "JanelaInicio": "2025-03-10",
              "JanelaFim": "2026-04-09",
              "VersaoExtractor": "1.0.0",
              "SkusSemCadastro": 0
            }
            """);

        var leitura = ManifestoLeitor.Ler(_dir);

        leitura.Manifesto.Should().BeNull();
        MotivoAcionavelPorComprador(leitura.MotivoInviabilidade);
    }

    [Fact]
    public void Periodo_que_termina_antes_do_dia_da_sugestao_e_inviavel()
    {
        // Sem nenhum dia a partir da sugestão não existe gabarito: não há venda posterior
        // à decisão de compra contra a qual medir os dois métodos.
        Escrever(Manifesto(janelaInicio: "2025-03-10", janelaFim: "2026-03-09"));

        var leitura = ManifestoLeitor.Ler(_dir);

        leitura.Manifesto.Should().BeNull();
        MotivoAcionavelPorComprador(leitura.MotivoInviabilidade);
        leitura.MotivoInviabilidade.Should().Contain("10/03/2026",
            "o comprador precisa ver a data da sugestão que ele escolheu, não um id interno");
    }

    [Fact]
    public void Periodo_que_comeca_no_dia_da_sugestao_e_inviavel()
    {
        // Espelho do caso anterior: sem nenhum dia ANTERIOR à sugestão não há histórico
        // para o modelo aprender o padrão de venda.
        Escrever(Manifesto(janelaInicio: "2026-03-10", janelaFim: "2026-04-09"));

        var leitura = ManifestoLeitor.Ler(_dir);

        leitura.Manifesto.Should().BeNull();
        MotivoAcionavelPorComprador(leitura.MotivoInviabilidade);
        leitura.MotivoInviabilidade.Should().Contain("histórico");
    }

    private void Escrever(string json) =>
        File.WriteAllText(Path.Combine(_dir, ManifestoLeitor.NomeArquivo), json);

    private static string Manifesto(string janelaInicio, string janelaFim) => $$"""
        {
          "SugestaoId": 21217,
          "SugestaoDescricao": "MATTEL",
          "SugestaoDataHora": "2026-03-10T10:27:00",
          "SugestaoTipoCalculo": 2,
          "JanelaInicio": "{{janelaInicio}}",
          "JanelaFim": "{{janelaFim}}",
          "VersaoExtractor": "1.0.0",
          "SkusSemCadastro": 0
        }
        """;

    /// <summary>
    /// Quem lê o motivo é comprador de farmácia. Precisa dizer o que fazer (o extrator é
    /// a próxima ação em todos os casos) e não pode citar formato de arquivo nem campo
    /// interno — o comprador não tem como agir sobre nada disso.
    /// </summary>
    private static void MotivoAcionavelPorComprador(string? motivo)
    {
        motivo.Should().NotBeNullOrWhiteSpace();
        motivo.Should().Contain("extrator", "o motivo tem de terminar numa próxima ação");

        foreach (var jargao in new[] { "json", "manifesto", "csv", "zip", "SugestaoId", "campo" })
        {
            motivo.Should().NotContainEquivalentOf(jargao,
                $"'{jargao}' é vocabulário de programador, e quem lê é comprador");
        }
    }
}
