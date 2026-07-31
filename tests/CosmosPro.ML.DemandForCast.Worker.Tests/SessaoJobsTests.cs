using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Worker.Sessoes;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// Os dois jobs que a sessão cria carregam parâmetros que ninguém a jusante consegue
/// adivinhar: o corte anti-vazamento do treino e a janela que seleciona a sugestão. Errar
/// qualquer um deles não dá erro na hora — dá uma comparação recusada na última fase, ou
/// pior, uma comparação que roda medindo memória. Por isso os valores são afirmados aqui,
/// em teste puro, e não só observados de longe na integração.
/// </summary>
public sealed class SessaoJobsTests
{
    private static readonly DateTimeOffset Agora = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime SugestaoDataHora = new(2026, 7, 1, 9, 30, 0);
    private static readonly Guid TreinoJobId = Guid.Parse("0198a0f0-0000-7000-8000-000000000001");

    /// <summary>Retrato normal do Stage: uma sugestão só, com poucos SKUs.</summary>
    private static readonly SugestaoNoStage UmaSugestao = new(Cabecalhos: 1, SkusDistintos: 12);

    private static SessaoEmAndamento Sessao(
        SessaoStatus status = SessaoStatus.ProcessandoDados,
        byte? tipoCalculo = 2,
        Guid? treinoJobId = null)
        => SessaoComData(SugestaoDataHora, status, tipoCalculo, treinoJobId);

    /// <summary>
    /// Sobrecarga separada porque a data <b>ausente</b> é um dos casos afirmados: um
    /// parâmetro opcional com <c>?? padrão</c> transformaria o nulo do teste no valor
    /// default e a asserção passaria medindo o caminho feliz.
    /// </summary>
    private static SessaoEmAndamento SessaoComData(
        DateTime? sugestaoDataHora,
        SessaoStatus status = SessaoStatus.ProcessandoDados,
        byte? tipoCalculo = 2,
        Guid? treinoJobId = null) => new(
            Id: Guid.Parse("0198a0f0-0000-7000-8000-0000000000ff"),
            RedeId: 42,
            Status: status,
            CargaStageId: Guid.Parse("0198a0f0-0000-7000-8000-000000000002"),
            TreinoJobId: treinoJobId,
            ComparacaoPbsId: null,
            SugestaoDataHora: sugestaoDataHora,
            SugestaoTipoCalculo: tipoCalculo,
            SkusSemCadastro: null);

    /// <summary>
    /// O corte é o próprio dia da sugestão porque as duas pontas concordam nisso: o
    /// <c>StageObservationLoader</c> aplica <c>TreinoAte</c> de forma <b>exclusiva</b>
    /// (<c>Data &lt; @treinoAte</c>), então cortar em 01/07 faz o modelo parar em 30/06; e o
    /// <c>ComparacaoProcessor</c> exige que a data alcançada seja <b>estritamente anterior</b>
    /// à sugestão. Cortar no dia seguinte deixaria o modelo aprender com o próprio dia da
    /// sugestão e a última fase recusaria o job.
    /// </summary>
    [Fact]
    public void Treino_corta_no_dia_da_sugestao_porque_o_corte_e_exclusivo()
    {
        var (job, motivo) = SessaoJobs.Treino(Sessao(), UmaSugestao, Agora);

        motivo.Should().BeNull();
        job!.TreinoAte.Should().Be(new DateOnly(2026, 7, 1),
            "corte exclusivo em 01/07 faz o treino parar em 30/06, estritamente antes da sugestão");
        job.TreinoAte.Should().NotBe(new DateOnly(2026, 7, 2),
            "cortar no dia seguinte deixaria o dia da sugestão entrar no ajuste e a comparação recusaria o job");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(23, 59, 59)]
    public void Hora_da_sugestao_nao_desloca_o_corte(int hora, int minuto, int segundo)
    {
        var dataHora = new DateTime(2026, 7, 1, hora, minuto, segundo);

        var (job, _) = SessaoJobs.Treino(SessaoComData(dataHora), UmaSugestao, Agora);

        job!.TreinoAte.Should().Be(new DateOnly(2026, 7, 1),
            "o corte é por dia: nenhuma venda do dia da sugestão pode entrar, seja a que hora ela foi calculada");
    }

    [Fact]
    public void Treino_nasce_pendente_na_rede_da_sessao()
    {
        var sessao = Sessao();

        var (job, _) = SessaoJobs.Treino(sessao, UmaSugestao, Agora);

        job!.RedeId.Should().Be(sessao.RedeId, "modelo é sempre por rede — treinar com o Stage de outra cruzaria dado comercial");
        job.Status.Should().Be(TreinoStatus.Pendente);
        job.DataAgendamento.Should().Be(Agora);
        job.Id.Should().NotBeEmpty();
        job.MaxSkus.Should().BeGreaterThan(0, "o TreinoProcessor usa MaxSkus como orçamento de SKUs; zero não treina nada");
    }

    /// <summary>
    /// O orçamento sai do tamanho da sugestão, não de uma constante. Com número fixo pequeno,
    /// uma sugestão real do PBS (milhares de SKUs) perderia quase toda a população para
    /// <c>ItensForaOrcamentoSkus</c> e a comparação sairia vazia por um motivo que não tem nada
    /// a ver com o método sob teste.
    /// </summary>
    [Fact]
    public void Orcamento_do_treino_acompanha_os_skus_da_sugestao()
    {
        var (job, _) = SessaoJobs.Treino(Sessao(), new SugestaoNoStage(1, SkusDistintos: 640), Agora);

        job!.MaxSkus.Should().Be(640,
            "todo SKU que o ERP avaliou precisa ter chance de entrar no orçamento top-N do treino");
    }

    /// <summary>
    /// Teto: o <c>StageObservationLoader</c> manda um parâmetro por SKU num único
    /// <c>IN (…)</c>, e o SQL Server para em 2100 parâmetros por comando — passar disso não
    /// deixa o treino lento, quebra o treino. O tempo de ajuste também cresce com o número de
    /// SKUs, e ninguém mediu isso em dado real ainda.
    /// </summary>
    [Fact]
    public void Orcamento_do_treino_para_no_teto_mesmo_com_sugestao_gigante()
    {
        var (job, _) = SessaoJobs.Treino(Sessao(), new SugestaoNoStage(1, SkusDistintos: 45_000), Agora);

        job!.MaxSkus.Should().Be(SessaoJobs.TetoDeSkusDoTreino);
        job.MaxSkus.Should().BeLessThan(2_000,
            "acima de ~2000 SKUs o IN (@s0…@sN) do loader estoura o limite de parâmetros do SQL Server");
    }

    /// <summary>
    /// Piso: o orçamento escolhe os SKUs de maior volume <b>da rede</b>, não os da sugestão.
    /// Pedir exatamente 2 porque a sugestão tem 2 itens selecionaria os 2 mais vendidos do
    /// catálogo — que podem não ser os da sugestão — e a comparação sairia vazia por orçamento.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Orcamento_do_treino_nao_desce_abaixo_do_piso(int skusDaSugestao)
    {
        var (job, _) = SessaoJobs.Treino(Sessao(), new SugestaoNoStage(1, skusDaSugestao), Agora);

        job!.MaxSkus.Should().Be(SessaoJobs.PisoDeSkusDoTreino);
    }

    /// <summary>
    /// Uma sessão é ancorada a UMA sugestão e a comparação agrega sem separar por sugestão:
    /// duas selecionadas pelo mesmo recorte sairiam somadas num resultado que não descreve
    /// nenhuma das duas. A recusa é aqui, antes do treino, e não na comparação — que também
    /// atende a execução avulsa da F13, onde janela com várias sugestões é o comportamento
    /// pedido.
    /// </summary>
    [Fact]
    public void Duas_sugestoes_no_mesmo_recorte_nao_geram_treino()
    {
        var (job, motivo) = SessaoJobs.Treino(Sessao(), new SugestaoNoStage(Cabecalhos: 2, SkusDistintos: 30), Agora);

        job.Should().BeNull("somar duas sugestões daria um número que não descreve nenhuma delas");
        motivo.Should().NotBeNullOrWhiteSpace();
        motivo.Should().Contain("extrator", "quem lê é comprador: o texto tem de terminar numa próxima ação");
    }

    /// <summary>
    /// A janela filtra <c>SugestoesCompra.DataHora</c> e o
    /// <c>StageSugestaoLoader</c> a converte em <c>&gt;= JanelaInicio 00:00</c> e
    /// <c>&lt; JanelaFim + 1 dia 00:00</c>. Uma sessão está ancorada a UMA sugestão, então a
    /// janela mais estreita que ainda a captura — qualquer que seja a hora do cálculo — é o
    /// próprio dia dela nas duas pontas.
    /// </summary>
    [Fact]
    public void Comparacao_mira_o_dia_da_sugestao_nas_duas_pontas_da_janela()
    {
        var (job, motivo) = SessaoJobs.Comparacao(
            Sessao(SessaoStatus.Treinando, treinoJobId: TreinoJobId), TreinoJobId, Agora);

        motivo.Should().BeNull();
        job!.JanelaInicio.Should().Be(new DateOnly(2026, 7, 1));
        job.JanelaFim.Should().Be(new DateOnly(2026, 7, 1),
            "o fim é convertido para exclusivo no dia seguinte, então o próprio dia captura a sugestão das 09:30");
    }

    [Fact]
    public void Comparacao_carrega_o_metodo_do_ERP_declarado_na_sessao()
    {
        foreach (byte tipoCalculo in (byte[])[1, 2])
        {
            var sessao = Sessao(SessaoStatus.Treinando, tipoCalculo: tipoCalculo, treinoJobId: TreinoJobId);

            var (job, _) = SessaoJobs.Comparacao(sessao, TreinoJobId, Agora);

            job!.TipoCalculo.Should().Be(tipoCalculo,
                "\"Emax e Eseg\" e \"Dias de Reposição\" são baselines distintos: a disputa é contra o que o ERP usou");
        }
    }

    [Fact]
    public void Comparacao_nasce_pendente_na_rede_da_sessao_e_aponta_para_o_treino()
    {
        var sessao = Sessao(SessaoStatus.Treinando, treinoJobId: TreinoJobId);

        var (job, _) = SessaoJobs.Comparacao(sessao, TreinoJobId, Agora);

        job!.RedeId.Should().Be(sessao.RedeId,
            "comparar a sugestão de uma rede contra o modelo de outra cruzaria dado comercial entre inquilinos");
        job.TreinoJobId.Should().Be(TreinoJobId);
        job.Status.Should().Be(ComparacaoPbsStatus.Pendente);
        job.DataAgendamento.Should().Be(Agora);
        job.Id.Should().NotBeEmpty();
    }

    /// <summary>
    /// Sem a data da sugestão não existe corte, e sem corte a última fase recusa o job. A
    /// sessão não pode seguir: ou ela para com um motivo que o comprador consegue agir, ou
    /// caminha três fases para morrer na última com uma recusa técnica.
    /// </summary>
    [Fact]
    public void Sessao_sem_data_da_sugestao_nao_gera_treino_e_devolve_motivo_acionavel()
    {
        var (job, motivo) = SessaoJobs.Treino(SessaoComData(null), UmaSugestao, Agora);

        job.Should().BeNull("sem corte o treino aprenderia com o gabarito e a comparação seria recusada");
        motivo.Should().NotBeNullOrWhiteSpace();
        motivo.Should().Contain("extrator", "quem lê é comprador de farmácia: o texto tem de terminar numa próxima ação");
    }

    [Fact]
    public void Sessao_sem_metodo_do_ERP_nao_gera_treino_e_devolve_motivo_acionavel()
    {
        var (job, motivo) = SessaoJobs.Treino(Sessao(tipoCalculo: null), UmaSugestao, Agora);

        job.Should().BeNull("o método viaja na mesma declaração da data: faltando um, a fase seguinte não tem contra o que disputar");
        motivo.Should().NotBeNullOrWhiteSpace();
        motivo.Should().Contain("extrator");
    }

    /// <summary>
    /// A fase abandonada é o único jeito de a sessão sair de um job que ninguém vai terminar: as
    /// três filas reclamam com <c>Status = 'Pendente'</c> e gravam <c>Processando</c>, sem lease
    /// e sem heartbeat, então um worker que morre no meio do treino deixa a sessão sendo
    /// repescada a cada 5 segundos para sempre.
    /// </summary>
    [Fact]
    public void Fase_reclamada_ha_mais_que_o_limite_encerra_a_sessao_com_proxima_acao()
    {
        var inicio = Agora - ComparacaoSessao.LimiteDeFaseSemProgresso - TimeSpan.FromMinutes(1);

        var motivo = SessaoJobs.FaseAbandonada("aprendizado do padrão de venda", inicio, Agora);

        motivo.Should().NotBeNullOrWhiteSpace();
        motivo.Should().Contain("Envie os dados novamente",
            "quem lê é comprador de farmácia: o texto tem de terminar numa próxima ação");
    }

    [Fact]
    public void Fase_dentro_do_limite_continua_em_andamento()
    {
        var inicio = Agora - ComparacaoSessao.LimiteDeFaseSemProgresso + TimeSpan.FromMinutes(1);

        SessaoJobs.FaseAbandonada("aprendizado do padrão de venda", inicio, Agora).Should().BeNull(
            "treino longo é normal; cortar cedo mataria job vivo dizendo que o processamento foi interrompido");
    }

    /// <summary>
    /// Job ainda <c>Pendente</c> (sem início de processamento) nunca é abandono: fila parada
    /// significa worker nenhum de pé, e nesse cenário quem julgaria também não está rodando.
    /// Quando um worker sobe, o job pendente é reclamado — matar a sessão seria o desfecho errado.
    /// </summary>
    [Fact]
    public void Fase_ainda_nao_reclamada_nunca_e_abandono()
        => SessaoJobs.FaseAbandonada("importação dos seus dados", null, Agora).Should().BeNull();

    [Fact]
    public void Comparacao_sem_declaracao_da_sugestao_tambem_devolve_motivo()
    {
        var (job, motivo) = SessaoJobs.Comparacao(
            SessaoComData(null, SessaoStatus.Treinando, treinoJobId: TreinoJobId), TreinoJobId, Agora);

        job.Should().BeNull();
        motivo.Should().NotBeNullOrWhiteSpace();
    }
}
