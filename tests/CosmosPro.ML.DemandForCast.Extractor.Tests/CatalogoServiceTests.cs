using System.Data;
using CosmosPro.ML.DemandForCast.Extractor;

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
        long id = 18172, string? descricao = "ACHE RX", byte tipoCalculo = 1)
    {
        var tabela = new DataTable();
        tabela.Columns.Add("SugestaoId", typeof(long));
        tabela.Columns.Add("Descricao", typeof(string));
        tabela.Columns.Add("DataHora", typeof(DateTime));
        tabela.Columns.Add("TipoCalculo", typeof(byte));
        tabela.Rows.Add(
            id,
            descricao ?? (object)DBNull.Value,
            new DateTime(2026, 6, 9, 14, 30, 0),
            tipoCalculo);
        return tabela.CreateDataReader();
    }

    private static DataTableReader LeitorDeContagem(params (long Id, int Linhas, int Lojas, object? Cobertura)[] linhas)
    {
        var tabela = new DataTable();
        tabela.Columns.Add("SugestaoId", typeof(long));
        tabela.Columns.Add("QtdLinhas", typeof(int));
        tabela.Columns.Add("QtdLojas", typeof(int));
        tabela.Columns.Add("DiasCoberturaMax", typeof(int));
        foreach (var (id, qtdLinhas, qtdLojas, cobertura) in linhas)
            tabela.Rows.Add(id, qtdLinhas, qtdLojas, cobertura ?? DBNull.Value);
        return tabela.CreateDataReader();
    }

    private static SugestaoCatalogoCabecalho Cabecalho(long id, string? descricao) =>
        new(id, descricao, new DateTime(2026, 3, 1, 8, 0, 0), 1);

    [Fact]
    public void Cabecalho_e_lido_na_ordem_dos_ordinais_da_query()
    {
        using var reader = LeitorDeCabecalho();
        reader.Read();

        var cabecalho = CatalogoService.LerCabecalho(reader);

        cabecalho.SugestaoId.Should().Be(18172);
        cabecalho.Descricao.Should().Be("ACHE RX");
        cabecalho.DataHora.Should().Be(new DateTime(2026, 6, 9, 14, 30, 0));
        cabecalho.TipoCalculo.Should().Be(1);
    }

    [Fact]
    public void Descricao_nula_no_pbs_vira_nulo_e_nao_quebra()
    {
        using var reader = LeitorDeCabecalho(descricao: null);
        reader.Read();

        CatalogoService.LerCabecalho(reader).Descricao.Should().BeNull();
    }

    /// <summary>
    /// MAX(DIAS_ESTOQUE) volta NULL quando a sugestao nao tem item nenhum. Zero e o valor
    /// certo: sem item nao ha o que cobrir, e ExtractionWindow.Derive recusa cobertura zero
    /// com motivo — em vez de o catalogo travar ou de a extracao seguir sem gabarito.
    /// </summary>
    [Fact]
    public void Cobertura_nula_na_contagem_vira_zero_e_a_janela_recusa()
    {
        using var reader = LeitorDeContagem((18172, 0, 0, null));

        var contagem = CatalogoService.LerContagem(18172, reader);

        contagem.DiasCoberturaMax.Should().Be(0);

        var janela = ExtractionWindow.Derive(
            new DateOnly(2026, 3, 1), contagem.DiasCoberturaMax, new DateOnly(2026, 7, 28));
        janela.Viavel.Should().BeFalse("cobertura zero nao produz um dia de gabarito");
    }

    [Fact]
    public void Contagem_da_sugestao_e_lida_da_linha_que_veio()
    {
        using var reader = LeitorDeContagem((18172, 365, 1, 5));

        var contagem = CatalogoService.LerContagem(18172, reader);

        contagem.SugestaoId.Should().Be(18172);
        contagem.QtdLinhas.Should().Be(365);
        contagem.QtdLojas.Should().Be(1);
        contagem.DiasCoberturaMax.Should().Be(5);
    }

    [Fact]
    public void Contagem_ausente_vira_zero_e_nao_falha()
    {
        // Visto na instância real: a sugestão 17658 existe em SUGESTOES_COMPRAS e não
        // tem nenhuma linha em SUGESTOES_COMPRAS_RESULTADO. Zero linhas é resposta
        // legítima, não erro — quem decide se dá para extrair é LoadEscopoSugestao.
        using var reader = LeitorDeContagem();

        var contagem = CatalogoService.LerContagem(17658, reader);

        contagem.Should().Be(new SugestaoContagem(17658, 0, 0, 0));
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

    [Fact]
    public void Token_cancelado_vence_a_excecao_do_driver()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var acao = () => CatalogoService.TraduzirFalha<int>(
            new InvalidOperationException("qualquer"), cts.Token, QualquerEtapa, conexaoJaAberta: true, TimeSpan.FromSeconds(1));

        acao.Should().Throw<OperationCanceledException>();
    }

    [Theory]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FormatException))]
    public void Token_cancelado_vence_qualquer_excecao_antes_da_classificacao(Type exceptionType)
    {
        // TraduzirFalha lança OperationCanceledException como primeira instrução,
        // antes de classificar a exceção. O tipo de exceção não importa aqui.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exc = (Exception)Activator.CreateInstance(exceptionType, "simulada")!;

        var acao = () => CatalogoService.TraduzirFalha<int>(
            exc, cts.Token, QualquerEtapa, conexaoJaAberta: true, TimeSpan.FromSeconds(1));

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

    [Fact]
    public void Loja_da_sugestao_recebe_o_nome_do_cadastro()
    {
        var casadas = CatalogoService.Casar(
            [(10, 40), (20, 2)],
            [new LojaOption(10, "MATRIZ"), new LojaOption(20, "FILIAL CENTRO"), new LojaOption(99, "OUTRA")]);

        casadas.Should().HaveCount(2);
        casadas[0].Should().Be(new LojaDaSugestao(10, "MATRIZ", 40));
        casadas[1].Should().Be(new LojaDaSugestao(20, "FILIAL CENTRO", 2));
    }

    [Fact]
    public void Loja_sem_cadastro_ativo_continua_na_lista()
    {
        // lojas_disponiveis.sql filtra ATIVO = 'S'. Sumir da lista seria pior: a loja
        // esta na sugestao e o comprador precisa saber para decidir sobre ela.
        var casadas = CatalogoService.Casar([(86, 7)], [new LojaOption(10, "MATRIZ")]);

        casadas.Should().ContainSingle();
        casadas[0].LojaId.Should().Be(86);
        casadas[0].Itens.Should().Be(7);
        casadas[0].Nome.Should().Contain("sem cadastro");
    }

    [Fact]
    public void Lojas_saem_ordenadas_por_id()
    {
        var casadas = CatalogoService.Casar(
            [(30, 1), (10, 1), (20, 1)],
            [new LojaOption(10, "A"), new LojaOption(20, "B"), new LojaOption(30, "C")]);

        casadas.Select(l => l.LojaId).Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Leitura_de_lojas_da_sugestao_le_id_e_contagem_na_ordem_da_query()
    {
        var tabela = new DataTable();
        tabela.Columns.Add("LojaId", typeof(int));
        tabela.Columns.Add("Itens", typeof(int));
        tabela.Rows.Add(86, 7);
        using var reader = tabela.CreateDataReader();

        var lidas = CatalogoService.LerLojasDaSugestao(reader).ToArray();

        lidas.Should().ContainSingle();
        lidas[0].Should().Be((86, 7));
    }
}
