using System.Text.Json;
using System.Text.Json.Serialization;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Features;
using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Forecasting.Comparison;
using CosmosPro.ML.DemandForCast.Forecasting.Engines;
using CosmosPro.ML.DemandForCast.Purchasing.Comparison;
using CosmosPro.ML.DemandForCast.Worker.Training;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace CosmosPro.ML.DemandForCast.Worker.Comparison;

/// <summary>
/// Executa um job de <see cref="ComparacaoPbs"/>: valida que o modelo de ML tem
/// direito de disputar com o ERP, monta a população que o ERP avaliou, prevê nela e
/// roda as três camadas do comparativo F13 (previsão, decisão, intervenção humana).
///
/// <para>
/// <b>Não serve qualquer modelo treinado.</b> O import da comparação traz de propósito
/// os dias <b>posteriores</b> à sugestão do ERP — são o gabarito. Um modelo ajustado
/// sem corte já viu esses dias, e a comparação passaria a medir memória em vez de
/// previsão. Por isso o job é <b>recusado</b> quando o <see cref="TreinoJob"/> de
/// origem não declara <c>TreinoAte</c>, ou quando a data que ele de fato carregou
/// (<c>TrainingResult.UltimaDataTreinada</c>) não é estritamente anterior à sugestão
/// mais antiga da janela.
/// </para>
///
/// <para>
/// <b>O congelamento de preço é aplicado aqui, não declarado aqui.</b> As features de
/// cada sugestão são geradas com <c>FeatureConfig.PrecoCongeladoAPartirDe</c> igual à
/// data da sugestão, e é a MESMA variável local que vai para
/// <see cref="ComparisonItem.PrecoCongeladoAPartirDe"/> e
/// <see cref="DecisionItem.PrecoCongeladoAPartirDe"/>. Os comparadores validam a
/// igualdade mas não conseguem inspecionar o <c>FeatureBuilder</c> (ver
/// <see cref="ForecastVsErpComparer"/>, "O que este comparador NÃO consegue
/// verificar") — este processador é o ponto em que a declaração e o congelamento de
/// fato coincidem, e separá-los em duas variáveis reabriria o buraco.
/// </para>
///
/// <para>
/// <b>A população nunca é alargada.</b> Ela sai de <c>SugestoesCompraItens</c> e só
/// encolhe, mas por motivos que não podem se misturar num balde só: item sem série
/// no Stage, ou de <c>TipoCalculo</c> 1 sem <c>EstoqueMaximo</c>, sai contado em
/// <see cref="ComparacaoOutput.ItensForaCamadaA"/> / <see cref="ComparacaoOutput.ItensForaCamadaB"/>;
/// item cujo SKU não coube no orçamento top-<c>MaxSkus</c> recalculado com o corte
/// da sugestão sai em <see cref="ComparacaoOutput.ItensForaOrcamentoSkus"/>; item cuja
/// janela avança para além do fim do histórico importado sai em
/// <see cref="ComparacaoOutput.ItensForaCamadaAAlemDoHistorico"/> /
/// <see cref="ComparacaoOutput.ItensForaCamadaBAlemDoHistorico"/>. Nenhum par que o
/// ERP não olhou entra.
/// </para>
/// </summary>
internal sealed class ComparacaoProcessor(
    IMinioClient minio,
    IConfiguration config,
    IServiceProvider services,
    ILogger<ComparacaoProcessor> logger)
{
    private static readonly ComparisonOptions PrevisaoOpcoes = new();
    private static readonly DecisionOptions DecisaoOpcoes = new();

    /// <summary>
    /// Enums saem como texto: <c>DecisionComparisonResult.Utilidade</c> é o campo que
    /// distingue "comparou tudo e deu empate" de "não comparou nada porque a cobertura
    /// do PBS excede o horizonte do ML", e um inteiro no JSON deixaria essa distinção
    /// dependendo de quem lê saber a ordem do enum.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record Outcome(string ResultadoJson);

    public async Task<Outcome> ProcessAsync(ComparacaoPbs job, CancellationToken ct)
    {
        var connStr = config.GetConnectionString("Stage")
            ?? throw new InvalidOperationException("Connection string 'Stage' não encontrada.");

        var (treino, modeloTreinadoAte) = await CarregarTreinoAsync(job, ct);

        var sugestoes = await new StageSugestaoLoader(connStr, logger)
            .LoadAsync(job.RedeId, job.JanelaInicio, job.JanelaFim, job.TipoCalculo, ct);

        if (sugestoes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Não há sugestão de compra do ERP com método {job.TipoCalculo} entre " +
                $"{job.JanelaInicio:dd/MM/yyyy} e {job.JanelaFim:dd/MM/yyyy} nos dados importados desta rede. " +
                "Verifique se o ZIP importado inclui sugestoes_compra.csv e sugestoes_compra_itens.csv, " +
                "ou ajuste a janela e o método para os de uma sugestão que exista.");
        }

        var primeiraSugestao = sugestoes.Min(s => DateOnly.FromDateTime(s.DataHora));
        GarantirModeloAnteriorASugestao(treino, modeloTreinadoAte, primeiraSugestao);

        var observacoes = await new StageObservationLoader(connStr, logger)
            .LoadAsync(job.RedeId, treino.MaxSkus, treinoAte: null, ct);

        if (observacoes.Count == 0)
        {
            throw new InvalidOperationException(
                "Não há histórico de vendas no Stage desta rede, então não existe venda real contra a qual " +
                "medir os dois métodos. Importe os dados de vendas antes de comparar.");
        }

        using var modelo = await BaixarModeloAsync(treino.ModeloBlobKey!, ct);

        var populacao = await MontarPopulacaoAsync(
            job, connStr, treino, modeloTreinadoAte, sugestoes, observacoes, modelo, ct);

        logger.LogInformation(
            "Comparação {Id}: {Total} item(ns) do ERP, {A} na camada A e {B} na camada B.",
            job.Id, populacao.ItensDaSugestao, populacao.Previsao.Count, populacao.Decisao.Count);

        var previsao = new ForecastVsErpComparer(PrevisaoOpcoes).Compare(populacao.Previsao);
        var decisao = new DecisionComparer(DecisaoOpcoes).Compare(populacao.Decisao);
        var intervencao = HumanOverrideReport.Compute(populacao.Intervencao);

        logger.LogInformation(
            "Comparação {Id}: camada A avaliou {Pares} par(es); camada B está {Utilidade}.",
            job.Id, previsao.ParesAvaliados, decisao.Utilidade);

        var saida = new ComparacaoOutput(
            GeradoEm: DateTimeOffset.UtcNow,
            TreinoJobId: treino.Id,
            ModeloTreinadoAte: modeloTreinadoAte,
            TreinoAte: treino.TreinoAte,
            TipoCalculo: job.TipoCalculo,
            JanelaInicio: job.JanelaInicio,
            JanelaFim: job.JanelaFim,
            Sugestoes: sugestoes.Count,
            ItensDaSugestao: populacao.ItensDaSugestao,
            ItensCamadaA: populacao.Previsao.Count,
            ItensForaCamadaA: populacao.ForaCamadaA,
            ItensForaCamadaAAlemDoHistorico: populacao.ForaCamadaAAlemDoHistorico,
            ItensCamadaB: populacao.Decisao.Count,
            ItensForaCamadaB: populacao.ForaCamadaB,
            ItensForaCamadaBAlemDoHistorico: populacao.ForaCamadaBAlemDoHistorico,
            ItensForaOrcamentoSkus: populacao.ForaOrcamentoSkus,
            RessalvaTreinoServe: ComparacaoOutput.RessalvaPadraoTreinoServe,
            Previsao: previsao,
            Decisao: decisao,
            Intervencao: intervencao);

        return new Outcome(JsonSerializer.Serialize(saida, Json));
    }

    // --- Contrato 1: o modelo não pode ter sido treinado sobre o gabarito ----

    /// <summary>
    /// Carrega o treino de origem e extrai dele a data que o comparativo vai declarar
    /// como <c>ModeloTreinadoAte</c>. O valor sai de
    /// <c>TrainingResult.UltimaDataTreinada</c> — a data que o treino de fato carregou
    /// —, nunca de <c>TreinoAte</c>: o Stage pode simplesmente parar antes do corte, e
    /// derivar um do outro seria adivinhar.
    /// </summary>
    private async Task<(TreinoJob Treino, DateOnly ModeloTreinadoAte)> CarregarTreinoAsync(
        ComparacaoPbs job, CancellationToken ct)
    {
        TreinoJob? treino;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();
            treino = await db.TreinoJobs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == job.TreinoJobId, ct);
        }

        if (treino is null)
        {
            throw new InvalidOperationException(
                $"O treino {job.TreinoJobId} não existe mais. Rode um treino novo e agende a comparação de novo.");
        }

        // Modelo é sempre por rede (TreinoJob.RedeId). Comparar a sugestão de uma rede
        // contra o modelo de outra cruzaria dado comercial entre inquilinos.
        if (treino.RedeId != job.RedeId)
        {
            throw new InvalidOperationException(
                $"O treino {treino.Id} é de outra rede. O modelo de uma rede não pode ser usado nos dados de outra.");
        }

        if (treino.Status != TreinoStatus.Concluido || string.IsNullOrEmpty(treino.ModeloBlobKey))
        {
            throw new InvalidOperationException(
                $"O treino {treino.Id} não terminou com sucesso (situação: {treino.Status}), então não há modelo " +
                "salvo para comparar. Aguarde o treino concluir ou rode um novo.");
        }

        if (treino.TreinoAte is null)
        {
            throw new InvalidOperationException(
                $"O treino {treino.Id} rodou SEM data de corte, ou seja, aprendeu também com as vendas " +
                "posteriores à sugestão do ERP — que são justamente o gabarito desta comparação. O resultado " +
                "mediria memória, não previsão. Rode um treino novo informando a data de corte (use a data da " +
                "sugestão que será comparada) e agende a comparação apontando para ele.");
        }

        var resultado = string.IsNullOrEmpty(treino.ResultadoJson)
            ? null
            : JsonSerializer.Deserialize<TrainingResult>(treino.ResultadoJson, TrainingResultJson.Options);

        if (resultado?.UltimaDataTreinada is not { } ultimaDataTreinada)
        {
            throw new InvalidOperationException(
                $"O treino {treino.Id} não registrou até que dia os dados dele chegaram, então não há como " +
                "provar que o modelo não viu o gabarito. Rode um treino novo (com data de corte) e agende a " +
                "comparação apontando para ele.");
        }

        return (treino, ultimaDataTreinada);
    }

    private static void GarantirModeloAnteriorASugestao(
        TreinoJob treino, DateOnly modeloTreinadoAte, DateOnly primeiraSugestao)
    {
        if (modeloTreinadoAte < primeiraSugestao) return;

        throw new InvalidOperationException(
            $"O treino {treino.Id} aprendeu com dados até {modeloTreinadoAte:dd/MM/yyyy}, mas a sugestão mais " +
            $"antiga desta janela é de {primeiraSugestao:dd/MM/yyyy}. O modelo já conhece o resultado que deveria " +
            $"prever, e a comparação não valeria nada. Rode um treino novo com data de corte igual a " +
            $"{primeiraSugestao:dd/MM/yyyy} (ou anterior) e agende a comparação apontando para ele.");
    }

    // --- Contrato 2 e 3: congelamento de preço e população -------------------

    private sealed record Populacao(
        int ItensDaSugestao,
        int ForaCamadaA,
        int ForaCamadaAAlemDoHistorico,
        int ForaCamadaB,
        int ForaCamadaBAlemDoHistorico,
        int ForaOrcamentoSkus,
        List<ComparisonItem> Previsao,
        List<DecisionItem> Decisao,
        List<HumanOverrideItem> Intervencao);

    /// <summary>
    /// Monta as três populações. As features são construídas <b>por data de sugestão</b>
    /// porque o congelamento de preço é um parâmetro do <see cref="FeatureBuilder"/>, e
    /// duas sugestões de datas diferentes exigem cortes diferentes — reaproveitar um
    /// único conjunto de features vazaria o preço realizado de uma delas.
    /// </summary>
    private async Task<Populacao> MontarPopulacaoAsync(
        ComparacaoPbs job,
        string connStr,
        TreinoJob treino,
        DateOnly modeloTreinadoAte,
        IReadOnlyList<SugestaoStage> sugestoes,
        IReadOnlyList<DailyObservation> observacoes,
        LightGbmForecastModel modelo,
        CancellationToken ct)
    {
        var previsao = new List<ComparisonItem>();
        var decisao = new List<DecisionItem>();
        var intervencao = new List<HumanOverrideItem>();
        // Memo do orçamento/ABC por corte, vivo só nesta execução. O agrupamento abaixo já
        // é por data, então hoje cada corte aparece uma vez; o memo existe para que iterar
        // sugestão a sugestão (em vez de data a data) não volte a custar uma consulta por
        // linha da sugestão.
        var abcPorCorte = new Dictionary<DateOnly, IReadOnlyDictionary<string, string>>();
        int itensDaSugestao = 0, foraA = 0, foraAAlemDoHistorico = 0, foraB = 0, foraBAlemDoHistorico = 0, foraOrcamentoSkus = 0;

        // Teto do histórico importado: nenhuma camada pode pontuar dia além dele,
        // seja qual for a largura da janela pedida. `JanelaFim` só limita QUAIS
        // sugestões entram (contrato 3) — não até quando elas são pontuadas, ver
        // ComparacaoPbs.JanelaFim. Sem este teto, um item cuja cobertura avança
        // além do que foi importado cairia em ItensForaCamadaA/B indistinguível de
        // série curta ou de TipoCalculo 1 sem eMax.
        var fimDoHistoricoImportado = observacoes.Max(o => o.Data);

        foreach (var grupo in sugestoes.GroupBy(s => DateOnly.FromDateTime(s.DataHora)))
        {
            ct.ThrowIfCancellationRequested();

            // A MESMA variável alimenta o FeatureBuilder e a declaração que os
            // comparadores validam. Ver a nota de classe: separá-las reabre o buraco
            // do preço realizado.
            var corte = grupo.Key;

            var chaves = grupo.SelectMany(s => s.Itens)
                .Select(i => (i.Sku, i.LojaId))
                .ToHashSet();

            if (!abcPorCorte.TryGetValue(corte, out var abc))
            {
                abc = await new StageObservationLoader(connStr, logger)
                    .LoadOrcamentoAbcAsync(job.RedeId, treino.MaxSkus, treinoAte: corte, ct);
                abcPorCorte[corte] = abc;
            }

            var (dias, skusNoOrcamento) = PreverJanela(corte, abc, chaves, observacoes, modelo);

            foreach (var sugestao in grupo)
            {
                foreach (var item in sugestao.Itens)
                {
                    itensDaSugestao++;

                    intervencao.Add(new HumanOverrideItem
                    {
                        RedeId = job.RedeId,
                        SugestaoId = item.SugestaoId,
                        LojaId = item.LojaId,
                        Sku = item.Sku,
                        Curva = item.Curva,
                        CompraSugerida = item.CompraSugerida,
                        CompraAutorizada = item.CompraAutorizada,
                        PrecoCompra = item.PrecoCompra,
                    });

                    // Motivo próprio, checado antes de A/B: um SKU fora do orçamento
                    // top-MaxSkus recalculado com o corte (ver PreverJanela) não
                    // tem classe ABC nem feature nenhuma, então cairia nos dois
                    // balde de A/B ao mesmo tempo por um motivo que não é falta de
                    // dado nem de horizonte — sai contado à parte e não disputa
                    // nenhuma camada.
                    if (!skusNoOrcamento.Contains(item.Sku))
                    {
                        foraOrcamentoSkus++;
                        continue;
                    }

                    // Camada A pontua uma TAXA (unidades/dia), então basta a parte da
                    // cobertura que o lead time das features alcança: além de
                    // corte + LeadTimeDias - 1 o dia-alvo seria alimentado por
                    // observação posterior ao corte, e a regra de informação — com
                    // razão — o recusaria.
                    var diasCamadaA = Math.Min((int)item.DiasEstoque, PrevisaoOpcoes.LeadTimeDias);
                    var alemDoHistoricoA = ExigeDadosAlemDoHistorico(corte, diasCamadaA, fimDoHistoricoImportado);
                    var janelaA = alemDoHistoricoA ? null : Janela(dias, item, corte, diasCamadaA);
                    if (janelaA is null)
                    {
                        if (alemDoHistoricoA) foraAAlemDoHistorico++; else foraA++;
                    }
                    else
                    {
                        previsao.Add(new ComparisonItem
                        {
                            RedeId = job.RedeId,
                            SugestaoId = item.SugestaoId,
                            DataHora = sugestao.DataHora,
                            ModeloTreinadoAte = modeloTreinadoAte,
                            PrecoCongeladoAPartirDe = corte,
                            TipoCalculo = sugestao.TipoCalculo,
                            LojaId = item.LojaId,
                            Sku = item.Sku,
                            DemandaDiaErp = (double)item.DemandaDia,
                            Curva = item.Curva,
                            Dias = janelaA,
                        });
                    }

                    // Camada B pontua uma QUANTIDADE dimensionada para a cobertura
                    // inteira, então exige a janela inteira — inclusive além do
                    // horizonte do ML, onde o próprio comparador reporta o item em
                    // ForaDoHorizonteMl em vez de compará-lo.
                    var alemDoHistoricoB = ExigeDadosAlemDoHistorico(corte, item.DiasEstoque, fimDoHistoricoImportado);
                    var janelaB = alemDoHistoricoB ? null : Janela(dias, item, corte, item.DiasEstoque);
                    var elegivelB = janelaB is not null
                        && (sugestao.TipoCalculo != 1 || item.EstoqueMaximo is not null);

                    if (!elegivelB)
                    {
                        if (alemDoHistoricoB) foraBAlemDoHistorico++; else foraB++;
                    }
                    else
                    {
                        decisao.Add(new DecisionItem
                        {
                            RedeId = job.RedeId,
                            SugestaoId = item.SugestaoId,
                            DataHora = sugestao.DataHora,
                            ModeloTreinadoAte = modeloTreinadoAte,
                            PrecoCongeladoAPartirDe = corte,
                            TipoCalculo = sugestao.TipoCalculo,
                            LojaId = item.LojaId,
                            Sku = item.Sku,
                            Curva = item.Curva,
                            DemandaDiaErp = item.DemandaDia,
                            EstoqueSaldo = item.EstoqueSaldo,
                            PedidosPendentes = item.PedidosPendentes,
                            ConsideraPedidosPendentes = sugestao.ConsideraPedidosPendentes,
                            DiasEstoque = item.DiasEstoque,
                            EstoqueMaximo = item.EstoqueMaximo,
                            EstoqueSeguranca = item.EstoqueSeguranca,
                            CompraSugerida = item.CompraSugerida,
                            PrecoCompra = item.PrecoCompra,
                            FatorEmbalagem = item.FatorEmbalagem,
                            Falteiro = item.Falteiro,
                            Dias = janelaB!,
                        });
                    }
                }
            }
        }

        return new Populacao(
            itensDaSugestao, foraA, foraAAlemDoHistorico, foraB, foraBAlemDoHistorico, foraOrcamentoSkus,
            previsao, decisao, intervencao);
    }

    /// <summary>
    /// Se os <paramref name="quantidade"/> dias a partir do <paramref name="corte"/>
    /// avançam para além do último dia de venda importado no Stage
    /// (<paramref name="fimDoHistoricoImportado"/>). Checado ANTES de tentar montar a
    /// janela: sem isso, o item cairia em <c>ItensForaCamadaA</c>/<c>ItensForaCamadaB</c>
    /// indistinguível de série curta ou de ruptura — aqui o motivo é estrutural, não do
    /// item.
    /// </summary>
    private static bool ExigeDadosAlemDoHistorico(DateOnly corte, int quantidade, DateOnly fimDoHistoricoImportado)
        => quantidade >= 1 && corte.AddDays(quantidade - 1) > fimDoHistoricoImportado;

    /// <summary>
    /// Devolve os <paramref name="quantidade"/> dias a partir do corte, ou <c>null</c> se
    /// algum deles não tem linha de feature — série curta demais ou loja/SKU sem venda no
    /// período. Cobertura que ultrapassa o fim do histórico importado é interceptada ANTES
    /// desta chamada por <see cref="ExigeDadosAlemDoHistorico"/>, então quem chega aqui já
    /// devia ter dado — se ainda assim faltar linha, é falta de série mesmo. Entregar a
    /// janela incompleta faria os comparadores pontuarem uma compra dimensionada para N
    /// dias contra a venda de menos que N.
    /// </summary>
    private static List<DiaAvaliado>? Janela(
        IReadOnlyDictionary<(string Sku, int LojaId, DateOnly Data), DiaAvaliado> dias,
        SugestaoItemStage item,
        DateOnly corte,
        int quantidade)
    {
        if (quantidade < 1) return null;

        var janela = new List<DiaAvaliado>(quantidade);
        for (var i = 0; i < quantidade; i++)
        {
            if (!dias.TryGetValue((item.Sku, item.LojaId, corte.AddDays(i)), out var dia)) return null;
            janela.Add(dia);
        }
        return janela;
    }

    /// <summary>
    /// Gera as features com o preço congelado em <paramref name="corte"/> e prevê nos
    /// dias que a população precisa.
    ///
    /// <para>
    /// A classe ABC vem recalculada com o mesmo corte porque ela é <b>feature</b> e sai de
    /// uma soma sobre a variável-alvo: mantê-la do histórico inteiro faria o modelo saber,
    /// no dia da sugestão, quanto o item ia vender depois dela. A regra de classificação
    /// não é reescrita aqui — é a do <see cref="StageObservationLoader"/>, chamado com o
    /// corte pelo laço de população, e só o rótulo resultante é transposto para a série
    /// completa.
    /// </para>
    ///
    /// <para>
    /// <b>Skew de treino/serviço, não vazamento:</b> este corte é o da SUGESTÃO, não o
    /// <c>TreinoJob.TreinoAte</c> que o modelo de fato usou. Quando o treino termina antes
    /// da sugestão — o caso normal, contrato 1 exige isso —, tanto a ClasseAbc quanto o
    /// orçamento top-<c>maxSkus</c> vêm de uma janela mais longa do que a que o modelo
    /// observou no ajuste. Documentado ao lado do skew de preço em
    /// <see cref="ComparacaoOutput.RessalvaPadraoTreinoServe"/>.
    /// </para>
    /// </summary>
    private (Dictionary<(string Sku, int LojaId, DateOnly Data), DiaAvaliado> Dias, HashSet<string> SkusNoOrcamento)
        PreverJanela(
        DateOnly corte,
        IReadOnlyDictionary<string, string> abc,
        HashSet<(string Sku, int LojaId)> chaves,
        IReadOnlyList<DailyObservation> observacoes,
        LightGbmForecastModel modelo)
    {
        // As chaves de `abc` SÃO o orçamento top-maxSkus recalculado com o corte da
        // sugestão: quem não está aqui não ganhou ClasseAbc nem feature nenhuma, e
        // precisa sair contado à parte em ItensForaOrcamentoSkus — o motivo é
        // orçamento, não falta de série nem de horizonte.
        var skusNoOrcamento = new HashSet<string>(abc.Keys, StringComparer.OrdinalIgnoreCase);

        var serie = observacoes
            .Where(o => chaves.Contains((o.Sku, o.LojaId)) && abc.ContainsKey(o.Sku))
            .Select(o => o with { ClasseAbc = abc[o.Sku] })
            .ToList();

        // LeadTimeDias fica no default do FeatureConfig (7) — o MESMO valor que
        // PrevisaoOpcoes.LeadTimeDias e DecisaoOpcoes.LeadTimeDias assumem por
        // default hoje, mas nenhum dos três deriva dos outros dois. Ver
        // ComparisonOptions.LeadTimeDias: mudar um sem os outros não quebra
        // silenciosamente aqui — quebra ruidosamente no comparador, o que é
        // aceitável, mas o acoplamento em si só está documentado, não imposto.
        var config = new FeatureConfig { PrecoCongeladoAPartirDe = corte };
        var features = new FeatureBuilder(config).Build(serie);

        var dias = new Dictionary<(string, int, DateOnly), DiaAvaliado>();
        foreach (var f in features)
        {
            // Só os dias a partir do corte são pontuados; o resto da série existe para
            // alimentar lags e rolling, e prever nele seria trabalho jogado fora.
            if (f.Data < corte) continue;
            dias[(f.Sku, f.LojaId, f.Data)] = new DiaAvaliado(f, modelo.Predict(f));
        }

        logger.LogInformation(
            "Corte {Corte}: {N} dia(s) previsto(s) sobre {Series} série(s) com preço congelado.",
            corte, dias.Count, chaves.Count);

        return (dias, skusNoOrcamento);
    }

    private async Task<LightGbmForecastModel> BaixarModeloAsync(string blobKey, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket(TreinoProcessor.ModelsBucket)
            .WithObject(blobKey)
            .WithCallbackStream(s => s.CopyTo(ms)),
            ct);
        ms.Position = 0;
        logger.LogInformation("Modelo {Key} baixado ({Bytes} bytes).", blobKey, ms.Length);
        return LightGbmForecastModel.Load(ms);
    }
}

/// <summary>
/// Resultado serializável de uma comparação contra o ERP, gravado em
/// <c>ComparacaoPbs.ResultadoJson</c>. Uma execução mira sempre em UM
/// <paramref name="TipoCalculo"/> e UMA rede — "Emax e Eseg" e "Dias de Reposição" são
/// baselines distintos, e duas redes são dois casos.
/// </summary>
/// <param name="ModeloTreinadoAte">
/// Última data cujo dado entrou no ajuste do modelo, copiada de
/// <c>TrainingResult.UltimaDataTreinada</c> do treino de origem. É o que os
/// comparadores exigiram ser estritamente anterior à sugestão.
/// </param>
/// <param name="TreinoAte">Corte pedido no treino, para leitura ao lado do valor efetivamente alcançado.</param>
/// <param name="ItensDaSugestao">
/// Total de linhas que o ERP avaliou na janela — o teto da população. As camadas A e B
/// só encolhem a partir daqui.
/// </param>
/// <param name="ItensForaCamadaA">
/// Itens sem série completa nos dias pontuados da camada A. <b>Não</b> inclui os itens
/// fora por orçamento de SKUs (<see cref="ItensForaOrcamentoSkus"/>) nem os que caem por
/// o fim do histórico importado (<see cref="ItensForaCamadaAAlemDoHistorico"/>) — cada
/// motivo tem contador próprio para que "não tínhamos dado" não se confunda com "o SKU
/// não coube no orçamento" nem com "faltou venda depois do fim do que foi importado".
/// </param>
/// <param name="ItensForaCamadaAAlemDoHistorico">
/// Itens cuja janela da camada A (<c>min(DiasEstoque, LeadTimeDias)</c> dias a partir do
/// corte) avança para além do último dia de venda importado no Stage. Não é série curta
/// nem falta de horizonte do ML — é o fim dos dados disponíveis, e numa janela de
/// sugestões que se aproxima do fim do que foi importado este número cresce sozinho. Ver
/// a nota em <see cref="ComparacaoPbs.JanelaFim"/>.
/// </param>
/// <param name="ItensForaCamadaB">
/// Itens sem a janela de cobertura completa no histórico, ou de <c>TipoCalculo</c> 1 sem
/// <c>EstoqueMaximo</c> gravado. <b>Não</b> inclui os itens que a camada B recusa por
/// horizonte — esses estão em <c>Decisao.ForaDoHorizonteMl</c>, com o motivo — nem os que
/// caem por orçamento de SKUs (<see cref="ItensForaOrcamentoSkus"/>) ou por fim do
/// histórico importado (<see cref="ItensForaCamadaBAlemDoHistorico"/>).
/// </param>
/// <param name="ItensForaCamadaBAlemDoHistorico">
/// Itens cuja janela da camada B (<c>DiasEstoque</c> dias completos a partir do corte)
/// avança para além do último dia de venda importado no Stage. Comum quando
/// <c>JanelaFim</c> se aproxima do fim do período importado: a sugestão ainda está dentro
/// da janela pedida, mas a cobertura que ela exige para ser pontuada não está — ver a
/// nota em <see cref="ComparacaoPbs.JanelaFim"/>.
/// </param>
/// <param name="ItensForaOrcamentoSkus">
/// Itens cujo SKU ficou fora do orçamento top-<c>MaxSkus</c> recalculado com o corte da
/// sugestão (ver <see cref="ComparacaoProcessor.PreverJanela"/>). Distinto de falta
/// de dado: o SKU pode ter série completa e mesmo assim não caber no orçamento —
/// sobretudo com <c>MaxSkus</c> pequeno perto de um catálogo grande, ou quando o volume
/// do SKU só cresce depois do corte de treino.
/// </param>
/// <param name="RessalvaTreinoServe">
/// Ressalva metodológica que precisa acompanhar os números — ver
/// <see cref="RessalvaPadraoTreinoServe"/>. Viaja junto com o resultado, e não só na
/// documentação do código, para que quem lê o número leia também a ressalva.
/// </param>
/// <param name="Decisao">
/// Camada B. <b>Cheque <c>Utilidade</c> antes de qualquer número daqui:</b> com a
/// cobertura de 15 ou 30 dias corrente no PBS contra o horizonte de 7 dias do
/// pipeline atual, o desfecho esperado hoje é <c>ForaDoHorizonteMl</c> — nenhum item
/// comparado. Zero comparações não é empate.
/// </param>
internal sealed record ComparacaoOutput(
    DateTimeOffset GeradoEm,
    Guid TreinoJobId,
    DateOnly ModeloTreinadoAte,
    DateOnly? TreinoAte,
    byte TipoCalculo,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    int Sugestoes,
    int ItensDaSugestao,
    int ItensCamadaA,
    int ItensForaCamadaA,
    int ItensForaCamadaAAlemDoHistorico,
    int ItensCamadaB,
    int ItensForaCamadaB,
    int ItensForaCamadaBAlemDoHistorico,
    int ItensForaOrcamentoSkus,
    string RessalvaTreinoServe,
    ComparisonResult Previsao,
    DecisionComparisonResult Decisao,
    HumanOverrideResult Intervencao)
{
    /// <summary>
    /// Divergência conhecida entre treino e serviço, registrada em vez de corrigida.
    ///
    /// <para>
    /// O modelo é <b>treinado</b> com o preço realizado de cada dia (legítimo: o passado
    /// é passado) mas <b>servido</b>, nesta comparação, com o preço congelado na data da
    /// sugestão — obrigatório, senão a remarcação do próprio dia pontuado vazaria para a
    /// previsão dele. As duas distribuições de preço não são a mesma, e o modelo recebe
    /// na inferência uma feature com estatística diferente da que aprendeu.
    /// </para>
    ///
    /// <para>
    /// O efeito é conservador: tira informação do braço de ML, nunca acrescenta. Pode
    /// piorar o desempenho medido do ML, jamais inflá-lo, então não ameaça a conclusão
    /// se o ML vencer — e, se o ML perder por pouco, esta é uma explicação candidata
    /// antes de "o ERP prevê melhor". Consertá-la exige treinar com preço planejado (que
    /// o Stage não tem) ou com o mesmo congelamento do serviço, e está fora do escopo
    /// desta tarefa.
    /// </para>
    ///
    /// <para>
    /// <b>Segundo skew, mesma direção do efeito:</b> a ClasseAbc e o próprio orçamento
    /// top-<c>MaxSkus</c> servidos por <see cref="ComparacaoProcessor.PreverJanela"/> são
    /// recalculados com o corte da SUGESTÃO, não com <c>TreinoJob.TreinoAte</c> — o corte que o modelo de fato usou no
    /// ajuste. Quando o treino termina antes da sugestão (o caso normal; o contrato 1
    /// exige isso), o rótulo de ClasseAbc servido ao modelo e a própria composição do
    /// conjunto de SKUs vêm de uma janela mais longa do que a que ele observou treinando.
    /// Não é vazamento — tudo precede a sugestão —, mas é o mesmo tipo de divergência de
    /// distribuição entre treino e serviço do parágrafo acima, e por isso mora na mesma
    /// ressalva em vez de espalhada.
    /// </para>
    /// </summary>
    public const string RessalvaPadraoTreinoServe =
        "O modelo foi TREINADO com o preço realizado de cada dia e SERVIDO, nesta comparação, com o preço " +
        "congelado na data da sugestão — obrigatório para não vazar a remarcação do próprio dia pontuado. " +
        "É uma diferença de distribuição entre treino e serviço, e ela só pode prejudicar o braço de ML, " +
        "nunca favorecê-lo: se o ML vencer, o resultado vale apesar dela; se perder por pouco, considere-a " +
        "antes de concluir que o ERP prevê melhor. Vale uma segunda ressalva, de outra natureza, para a " +
        "classe ABC e para o conjunto de SKUs servidos ao modelo: ambos são recalculados com o corte da " +
        "sugestão, não com o corte que o treino de fato usou, então quando o treino termina antes da " +
        "sugestão (o caso normal) o modelo recebe rótulo e orçamento de SKUs de uma janela mais longa do " +
        "que a que aprendeu. Não é vazamento: tudo aí precede a sugestão, ou seja, cabe dentro do mesmo " +
        "conjunto de informação que o ERP tinha ao calcular. O efeito provável é de ruído — rótulo servido " +
        "diferente do rótulo treinado tende a piorar a previsão, não a melhorá-la —, mas, ao contrário do " +
        "congelamento de preço, aqui a direção não é garantida. Reporte-a junto do resultado.";
}
