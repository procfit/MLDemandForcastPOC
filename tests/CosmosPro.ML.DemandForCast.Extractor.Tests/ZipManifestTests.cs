using CosmosPro.ML.DemandForCast.Extractor;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

public sealed class ZipManifestTests
{
    [Fact]
    public void Manifesto_roundtrip_preserva_a_sugestao_e_a_janela()
    {
        var m = new ZipManifest(21217, "MATTEL", new DateTime(2026, 3, 10, 10, 27, 0), 2,
                                new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "1.0.0", 3, [11, 34], 40);

        var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

        volta.Should().BeEquivalentTo(m);
    }

    [Fact]
    public void Descricao_nula_e_preservada_no_roundtrip()
    {
        var m = new ZipManifest(1, null, new DateTime(2026, 1, 1), 1,
                                new DateOnly(2025, 1, 1), new DateOnly(2026, 2, 1), "1.0.0", 0, [5], 5);

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
                                new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "1.0.0", 3, [11, 34], 40);

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

    [Fact]
    public void Manifesto_declara_as_lojas_exportadas_e_o_total_da_sugestao()
    {
        var m = new ZipManifest(21217, "MATTEL", new DateTime(2026, 3, 10, 10, 27, 0), 2,
            new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "0.15.0", 0, [10, 20], 98);

        var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

        volta.LojasExportadas.Should().Equal(10, 20);
        volta.LojasNaSugestao.Should().Be(98);
    }

    [Fact]
    public void Manifesto_sem_recorte_declara_todas_as_lojas_da_sugestao()
    {
        // Extracao sem --stores: as duas listas coincidem, e o consumidor consegue
        // distinguir "exportou tudo" de "exportou parte" sem heuristica.
        var m = new ZipManifest(1, null, new DateTime(2026, 1, 1), 1,
            new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 8), "0.15.0", 0, [7], 1);

        var volta = ZipManifest.Ler(ZipManifest.Escrever(m));

        volta.LojasExportadas.Should().Equal(7);
        volta.LojasNaSugestao.Should().Be(1);
    }
}

/// <summary>
/// O extrator roda na máquina do cliente e o Worker não pode depender dele (mesma razão
/// pela qual o contrato de colunas do Stage é duplicado — ver <see cref="StageContractTests"/>).
/// Por isso o Worker tem leitor próprio para o mesmo JSON, e estes testes são o que
/// impede as duas formas de divergirem em silêncio: quem escreve o manifesto está aqui,
/// quem o lê está no Worker, e nenhum compilador liga os dois.
/// </summary>
public sealed class ManifestoContratoTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"manifesto-contrato-{Guid.NewGuid():N}");

    public ManifestoContratoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* diretório temporário */ }
    }

    [Fact]
    public void Forma_do_manifesto_bate_campo_a_campo_entre_extrator_e_Worker()
    {
        var noExtrator = Campos(typeof(ZipManifest));
        var noWorker = Campos(typeof(ManifestoDaSugestao));

        noWorker.Should().Equal(noExtrator,
            "o Worker desserializa o JSON que o extrator escreve; nome, tipo ou ordem diferente " +
            "quebra a leitura em produção sem quebrar nenhuma compilação");
    }

    [Fact]
    public void Nome_do_arquivo_na_raiz_do_envio_e_o_mesmo_nos_dois_lados()
    {
        ManifestoLeitor.NomeArquivo.Should().Be(ZipManifest.EntryName);
    }

    /// <summary>
    /// Prova de ponta a ponta do contrato: escreve com o serializador REAL do extrator e
    /// lê com o leitor REAL do Worker. A comparação de forma acima não pegaria uma
    /// divergência de casing, de política de nomes ou de formato de data — só isto pega.
    /// </summary>
    [Fact]
    public void Leitor_do_Worker_le_o_que_o_extrator_de_fato_escreve()
    {
        var escrito = new ZipManifest(21217, "MATTEL", new DateTime(2026, 3, 10, 10, 27, 0), 2,
                                      new DateOnly(2025, 3, 10), new DateOnly(2026, 4, 9), "1.2.3", 3, [11, 34], 40);
        File.WriteAllText(Path.Combine(_dir, ZipManifest.EntryName), ZipManifest.Escrever(escrito));

        var leitura = ManifestoLeitor.Ler(_dir);

        leitura.MotivoInviabilidade.Should().BeNull();
        leitura.Manifesto.Should().BeEquivalentTo(escrito, o => o.ComparingRecordsByMembers());
    }

    [Fact]
    public void Versao_atual_nao_e_vazia()
        => ZipManifest.VersaoAtual().Should().NotBeNullOrWhiteSpace();

    /// <summary>
    /// A data de geração sai da data de escrita do executável, **não** do timestamp do
    /// cabeçalho PE. O SDK do .NET compila de forma determinística por default, e nesse modo
    /// aquele campo do PE é um hash do conteúdo interpretado como data — costuma render
    /// algo como 1976 ou 2103. Este teste falha se alguém "melhorar" a implementação para
    /// lê-lo, porque a tela passaria a exibir uma data absurda com toda a confiança.
    /// </summary>
    [Fact]
    public void Data_de_geracao_e_plausivel_ou_ausente_nunca_absurda()
    {
        var geradoEm = ZipManifest.GeradoEm();

        if (geradoEm is null) return; // host sem executável próprio — ausência é resposta válida

        geradoEm.Value.Should().BeAfter(new DateTime(2024, 1, 1),
            "data anterior a isto denuncia leitura do timestamp do PE, que em build determinístico é um hash");
        geradoEm.Value.Should().BeBefore(DateTime.Now.AddDays(2),
            "data no futuro também denuncia hash lido como data");
    }

    /// <summary>
    /// Parâmetros do construtor primário na ordem declarada. Records posicionais também
    /// expõem um construtor de cópia, então o critério é "o de mais parâmetros".
    /// </summary>
    private static (string Nome, string Tipo)[] Campos(Type tipo) =>
        [.. tipo.GetConstructors()
              .OrderByDescending(c => c.GetParameters().Length)
              .First()
              .GetParameters()
              .Select(p => (p.Name!, p.ParameterType.Name))];
}
