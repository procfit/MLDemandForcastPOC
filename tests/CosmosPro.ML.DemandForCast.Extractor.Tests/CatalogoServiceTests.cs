using System.Data;
using System.Reflection;
using CosmosPro.ML.DemandForCast.Extractor;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// As peças puras da leitura do catálogo. As queries em si dependem de um SQL Server
/// vivo e são exercitadas pelo CLI contra o PBS.
/// <para>
/// A leitura de linha é testada com <see cref="DataTableReader"/>: as duas queries de
/// cabeçalho (catalogo_sugestoes.sql e sugestao_por_id.sql) devolvem a mesma forma, e
/// um erro de ordinal aqui só apareceria como conversão de tipo na frente do cliente.
/// </para>
/// </summary>
public sealed class CatalogoServiceTests
{
    private static DataTableReader LeitorDeCabecalho(
        long id = 18172, string? descricao = "ACHE RX", byte tipoCalculo = 1, object? diasCobertura = null)
    {
        var tabela = new DataTable();
        tabela.Columns.Add("SugestaoId", typeof(long));
        tabela.Columns.Add("Descricao", typeof(string));
        tabela.Columns.Add("DataHora", typeof(DateTime));
        tabela.Columns.Add("TipoCalculo", typeof(byte));
        tabela.Columns.Add("DiasCoberturaMax", typeof(int));
        tabela.Rows.Add(
            id,
            descricao ?? (object)DBNull.Value,
            new DateTime(2026, 6, 9, 14, 30, 0),
            tipoCalculo,
            diasCobertura ?? DBNull.Value);
        return tabela.CreateDataReader();
    }

    private static DataTableReader LeitorDeContagem(params (long Id, int Linhas, int Lojas)[] linhas)
    {
        var tabela = new DataTable();
        tabela.Columns.Add("SugestaoId", typeof(long));
        tabela.Columns.Add("QtdLinhas", typeof(int));
        tabela.Columns.Add("QtdLojas", typeof(int));
        foreach (var (id, qtdLinhas, qtdLojas) in linhas) tabela.Rows.Add(id, qtdLinhas, qtdLojas);
        return tabela.CreateDataReader();
    }

    private static SugestaoCatalogoCabecalho Cabecalho(long id, string? descricao) =>
        new(id, descricao, new DateTime(2026, 3, 1, 8, 0, 0), 1, 30);

    [Fact]
    public void Cabecalho_e_lido_na_ordem_dos_ordinais_da_query()
    {
        using var reader = LeitorDeCabecalho(diasCobertura: 5);
        reader.Read();

        var cabecalho = CatalogoService.LerCabecalho(reader);

        cabecalho.SugestaoId.Should().Be(18172);
        cabecalho.Descricao.Should().Be("ACHE RX");
        cabecalho.DataHora.Should().Be(new DateTime(2026, 6, 9, 14, 30, 0));
        cabecalho.TipoCalculo.Should().Be(1);
        cabecalho.DiasCoberturaMax.Should().Be(5);
    }

    [Fact]
    public void Descricao_nula_no_pbs_vira_nulo_e_nao_quebra()
    {
        using var reader = LeitorDeCabecalho(descricao: null, diasCobertura: 5);
        reader.Read();

        CatalogoService.LerCabecalho(reader).Descricao.Should().BeNull();
    }

    [Fact]
    public void Cobertura_nula_vira_zero_para_a_janela_ficar_degenerada_em_vez_de_o_catalogo_travar()
    {
        // Os cinco DIAS_CURVA_* podem ser todos NULL. Zero faz ExtractionWindow.Derive
        // devolver uma janela sem cobertura futura, que o comprador vê e descarta.
        using var reader = LeitorDeCabecalho(diasCobertura: null);
        reader.Read();

        CatalogoService.LerCabecalho(reader).DiasCoberturaMax.Should().Be(0);
    }

    [Fact]
    public void Contagem_da_sugestao_e_lida_da_linha_que_veio()
    {
        using var reader = LeitorDeContagem((18172, 365, 1));

        var contagem = CatalogoService.LerContagem(18172, reader);

        contagem.SugestaoId.Should().Be(18172);
        contagem.QtdLinhas.Should().Be(365);
        contagem.QtdLojas.Should().Be(1);
    }

    [Fact]
    public void Contagem_ausente_vira_zero_e_nao_falha()
    {
        // Visto na instância real: a sugestão 17658 existe em SUGESTOES_COMPRAS e não
        // tem nenhuma linha em SUGESTOES_COMPRAS_RESULTADO. Zero linhas é resposta
        // legítima, não erro — quem decide se dá para extrair é LoadEscopoSugestao.
        using var reader = LeitorDeContagem();

        var contagem = CatalogoService.LerContagem(17658, reader);

        contagem.Should().Be(new SugestaoContagem(17658, 0, 0));
    }

    [Fact]
    public void Filtro_vazio_devolve_o_catalogo_inteiro()
    {
        var catalogo = new[] { Cabecalho(1, "ACHE RX"), Cabecalho(2, "EMS GENERICO") };

        CatalogoService.Filtrar(catalogo, "  ").Should().HaveCount(2);
        CatalogoService.Filtrar(catalogo, null).Should().HaveCount(2);
    }

    [Fact]
    public void Filtro_acha_por_pedaco_da_descricao_ignorando_caixa()
    {
        var catalogo = new[] { Cabecalho(1, "ACHE RX"), Cabecalho(2, "EMS GENERICO") };

        var achados = CatalogoService.Filtrar(catalogo, "generico");

        achados.Should().ContainSingle().Which.SugestaoId.Should().Be(2);
    }

    [Fact]
    public void Filtro_acha_por_id()
    {
        var catalogo = new[] { Cabecalho(18172, "ACHE RX"), Cabecalho(17658, "EMS") };

        CatalogoService.Filtrar(catalogo, "18172").Should().ContainSingle()
            .Which.SugestaoId.Should().Be(18172);
    }

    [Fact]
    public void Filtro_nao_quebra_com_descricao_nula()
    {
        var catalogo = new[] { Cabecalho(1, null) };

        CatalogoService.Filtrar(catalogo, "ache").Should().BeEmpty();
        CatalogoService.Filtrar(catalogo, "1").Should().ContainSingle();
    }

    [Fact]
    public void Filtro_preserva_a_ordem_do_catalogo()
    {
        // A ordem é ORDER BY DATA_HORA DESC na query; filtrar não reordena.
        var catalogo = new[] { Cabecalho(30, "A x"), Cabecalho(10, "B x"), Cabecalho(20, "C x") };

        CatalogoService.Filtrar(catalogo, "x").Select(c => c.SugestaoId).Should().Equal(30L, 10L, 20L);
    }

    private static readonly Etapa QualquerEtapa = new("etapa qualquer", "arquivo.sql");

    /// <summary>
    /// <see cref="SqlException"/> não tem construtor público (ver <c>ExtratorErrosTests</c>).
    /// Só aqui isso importa de verdade: <see cref="CatalogoService.TraduzirFalha{T}"/> recebe a
    /// <see cref="Exception"/> crua do <c>catch</c>, e o único jeito de provar que um número SQL
    /// classificaria como <see cref="ConexaoPerdidaErro"/> — sem um SQL Server vivo — é montar
    /// a exceção real via reflexão sobre a API interna do driver.
    /// </summary>
    private static SqlException CriarSqlException(int numero)
    {
        var construtorErro = typeof(SqlError).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)],
            null)!;
        var erro = construtorErro.Invoke([numero, (byte)0, (byte)0, "servidor", "falha simulada", "procedimento", 1, null]);

        var colecao = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(colecao, [erro]);

        var criarExcecao = typeof(SqlException).GetMethod("CreateException",
            BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(SqlErrorCollection), typeof(string)], null)!;
        return (SqlException)criarExcecao.Invoke(null, [colecao, string.Empty])!;
    }

    [Fact]
    public void Token_cancelado_vence_a_excecao_do_driver()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var acao = () => CatalogoService.TraduzirFalha<int>(
            new InvalidOperationException("qualquer"), cts.Token, QualquerEtapa, conexaoJaAberta: true, TimeSpan.FromSeconds(1));

        acao.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Token_cancelado_vence_mesmo_quando_a_excecao_classificaria_como_transitoria()
    {
        // Pino do bug de verdade: cancelar um ExecuteReader síncrono chega como
        // SqlException, e com conexaoJaAberta=true e um número fora da lista especial
        // isso classificaria como ConexaoPerdidaErro — que é transitório e seria
        // RETENTADO. Sem a guarda, este teste passaria a devolver Result.Fail em vez
        // de lançar, e é exatamente essa regressão que ele precisa pegar.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var acao = () => CatalogoService.TraduzirFalha<int>(
            CriarSqlException(-1), cts.Token, QualquerEtapa, conexaoJaAberta: true, TimeSpan.FromSeconds(129));

        acao.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Token_vivo_ainda_devolve_a_falha_classificada_com_etapa_e_duracao()
    {
        var resultado = CatalogoService.TraduzirFalha<int>(
            new InvalidCastException("cast inválido"), CancellationToken.None, QualquerEtapa,
            conexaoJaAberta: true, TimeSpan.FromSeconds(42));

        resultado.IsFailed.Should().BeTrue();
        var erro = resultado.Errors.Should().ContainSingle().Which.Should().BeOfType<EtapaErro>().Subject;
        erro.Metadata[ExtratorErro.ChaveEtapa].Should().Be(QualquerEtapa.Nome);
        erro.Metadata[ExtratorErro.ChaveQuery].Should().Be(QualquerEtapa.QueryFile);
        erro.Metadata[ExtratorErro.ChaveDuracao].Should().Be(42d);
    }
}
