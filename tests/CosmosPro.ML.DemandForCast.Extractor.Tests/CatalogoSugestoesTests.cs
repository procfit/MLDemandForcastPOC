using CosmosPro.ML.DemandForCast.Extractor;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// Cobre as duas peças puras da leitura do catálogo em duas idas ao banco:
/// <see cref="ExtractionService.LotesDeSugestoes"/>, que mantém cada comando
/// abaixo do teto de parâmetros do SQL Server, e
/// <see cref="ExtractionService.MesclarCatalogo"/>, que junta cabeçalho e
/// contagem sem perder a sugestão que não tem linha de resultado nenhuma.
/// A execução das queries em si depende de um SQL Server vivo e não é testada aqui.
/// </summary>
public sealed class CatalogoSugestoesTests
{
    private static SugestaoCatalogoCabecalho Cabecalho(long id, int diasCobertura = 30) =>
        new(id, $"Sugestão {id}", new DateTime(2026, 3, 1, 8, 0, 0), 1, diasCobertura);

    [Fact]
    public void Lote_cabe_com_folga_no_teto_de_parametros_do_sql_server()
    {
        ExtractionService.SugestoesPorLote.Should().BeLessThan(ExtractionService.MaxParametrosPorComando);
    }

    [Fact]
    public void Catalogo_vazio_nao_gera_nenhum_lote()
    {
        ExtractionService.LotesDeSugestoes([]).Should().BeEmpty();
    }

    [Fact]
    public void Catalogo_menor_que_o_lote_sai_num_lote_so()
    {
        var lotes = ExtractionService.LotesDeSugestoes([1L, 2L, 3L]);

        lotes.Should().ContainSingle();
        lotes[0].Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public void Nenhum_lote_passa_do_tamanho_declarado()
    {
        var ids = Enumerable.Range(1, 5_000).Select(i => (long)i).ToArray();

        var lotes = ExtractionService.LotesDeSugestoes(ids);

        lotes.Should().OnlyContain(lote => lote.Count <= ExtractionService.SugestoesPorLote);
    }

    [Fact]
    public void Lotes_preservam_todos_os_ids_na_ordem()
    {
        // 1.201 força um resto parcial: o bug clássico do fatiamento é perder a
        // sobra que não completa um lote inteiro.
        var ids = Enumerable.Range(1, 1_201).Select(i => (long)i).ToArray();

        var lotes = ExtractionService.LotesDeSugestoes(ids);

        lotes.SelectMany(lote => lote).Should().Equal(ids);
        lotes[^1].Count.Should().Be(1_201 % ExtractionService.SugestoesPorLote);
    }

    [Fact]
    public void Merge_casa_cada_cabecalho_com_a_sua_contagem()
    {
        var catalogo = ExtractionService.MesclarCatalogo(
            [Cabecalho(10), Cabecalho(20)],
            [new SugestaoContagem(20, 999, 9), new SugestaoContagem(10, 500, 4)]);

        catalogo.Should().HaveCount(2);
        catalogo[0].SugestaoId.Should().Be(10);
        catalogo[0].QtdLinhas.Should().Be(500);
        catalogo[0].QtdLojas.Should().Be(4);
        catalogo[1].SugestaoId.Should().Be(20);
        catalogo[1].QtdLinhas.Should().Be(999);
        catalogo[1].QtdLojas.Should().Be(9);
    }

    [Fact]
    public void Sugestao_sem_linha_de_resultado_fica_no_catalogo_com_zero()
    {
        // Visto na instância real: a sugestão 17658 existe em SUGESTOES_COMPRAS e
        // não tem nenhuma linha em SUGESTOES_COMPRAS_RESULTADO. Sumir da lista faria
        // o comprador procurar por uma sugestão que o ERP mostra e a tela não.
        var catalogo = ExtractionService.MesclarCatalogo(
            [Cabecalho(17658), Cabecalho(17659)],
            [new SugestaoContagem(17659, 42, 3)]);

        catalogo.Should().HaveCount(2);
        catalogo[0].SugestaoId.Should().Be(17658);
        catalogo[0].QtdLinhas.Should().Be(0);
        catalogo[0].QtdLojas.Should().Be(0);
    }

    [Fact]
    public void Merge_sem_nenhuma_contagem_devolve_o_catalogo_inteiro_zerado()
    {
        var catalogo = ExtractionService.MesclarCatalogo([Cabecalho(10), Cabecalho(20)], []);

        catalogo.Should().HaveCount(2);
        catalogo.Should().OnlyContain(c => c.QtdLinhas == 0 && c.QtdLojas == 0);
    }

    [Fact]
    public void Merge_preserva_a_ordem_do_cabecalho_e_nao_a_da_contagem()
    {
        // O ORDER BY DATA_HORA DESC vive na query de cabeçalho; a de contagens não
        // ordena nada. Quem manda na ordem da tela é o cabeçalho.
        var catalogo = ExtractionService.MesclarCatalogo(
            [Cabecalho(30), Cabecalho(10), Cabecalho(20)],
            [new SugestaoContagem(10, 1, 1), new SugestaoContagem(20, 2, 2), new SugestaoContagem(30, 3, 3)]);

        catalogo.Select(c => c.SugestaoId).Should().Equal(30L, 10L, 20L);
    }

    [Fact]
    public void Merge_carrega_os_campos_do_cabecalho_sem_alterar()
    {
        var cabecalho = new SugestaoCatalogoCabecalho(77, null, new DateTime(2026, 5, 4, 13, 45, 0), 2, 0);

        var catalogo = ExtractionService.MesclarCatalogo([cabecalho], [new SugestaoContagem(77, 8, 2)]);

        catalogo[0].Descricao.Should().BeNull();
        catalogo[0].DataHora.Should().Be(new DateTime(2026, 5, 4, 13, 45, 0));
        catalogo[0].TipoCalculo.Should().Be(2);
        catalogo[0].DiasCoberturaMax.Should().Be(0);
    }

    [Fact]
    public void Contagem_orfa_nao_inventa_linha_no_catalogo()
    {
        var catalogo = ExtractionService.MesclarCatalogo(
            [Cabecalho(10)],
            [new SugestaoContagem(10, 5, 1), new SugestaoContagem(99, 7, 2)]);

        catalogo.Should().ContainSingle();
        catalogo[0].SugestaoId.Should().Be(10);
    }

    [Fact]
    public void Merge_sem_cabecalho_nenhum_devolve_catalogo_vazio()
    {
        ExtractionService.MesclarCatalogo([], []).Should().BeEmpty();
    }
}
