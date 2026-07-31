using CosmosPro.ML.DemandForCast.Forecasting.Comparison;
using CosmosPro.ML.DemandForCast.Forecasting.Evaluation;

namespace CosmosPro.ML.DemandForCast.Purchasing.Comparison;

/// <summary>
/// Camada B do comparativo F13: decisão contra decisão. Dada a previsão de cada lado,
/// quantas unidades cada um compraria, e qual das duas escolhas teria servido melhor a
/// venda que de fato ocorreu na janela coberta pela compra.
///
/// <para>
/// <b>Troca só a demanda.</b> A quantidade do braço ML sai da aritmética do próprio ERP —
/// mesmo <c>EstoqueSaldo</c>, mesmos <c>PedidosPendentes</c>, mesmo <c>DiasEstoque</c>,
/// mesmo <c>FatorEmbalagem</c> — com o <c>DemandaDia</c> substituído pelo do ML. Se duas
/// coisas mudassem, nenhuma diferença poderia ser atribuída à previsão e a camada não
/// provaria nada.
/// </para>
///
/// <para>
/// <b>Reconciliação é o portão de validade.</b> Não temos o código-fonte do ERP: a
/// aritmética abaixo é um <i>modelo</i> dele. Antes de comparar qualquer coisa, o
/// comparador reproduz o <c>CompraSugerida</c> gravado a partir do <c>DemandaDia</c>
/// gravado. Se reproduz, trocar a demanda é legítimo; se não reproduz, o item sai da
/// comparação e fica listado em <c>DetalheReconciliacao</c>. O agregado sai limpo e a
/// <c>TaxaConcordancia</c> diz o quanto dele foi possível validar. Este projeto já cometeu
/// o erro oposto uma vez — comparar o ML contra uma reimplementação nossa da regra
/// clássica —, e essa comparação teve de ser aposentada.
/// </para>
///
/// <para>
/// <b>Aritmética modelada, por <c>TipoCalculo</c>:</b>
/// <list type="bullet">
/// <item><b>2 — "Dias de Reposição":</b> <c>necessidade = demanda/dia × DiasEstoque</c>.</item>
/// <item><b>1 — "Emax e Eseg":</b> repõe até o <c>EstoqueMaximo</c> gravado. Como o eMax do
/// ERP é função da demanda dele, mantê-lo fixo faria a demanda não entrar na conta e os
/// dois braços comprariam sempre igual; então o braço ML usa o eMax reescalado pela razão
/// entre as demandas, <c>necessidade = EstoqueMaximo × (demanda/dia ÷ DemandaDia)</c>, o
/// que preserva os dias de cobertura implícitos no eMax. <c>EstoqueSeguranca</c> não entra
/// à parte: em tipo 1 já está embutido no eMax, e em tipo 2 vem zerado.</item>
/// </list>
/// E, nos dois casos:
/// <c>compra = arredonda_para_cima(max(0, necessidade − EstoqueSaldo − PedidosPendentes),
/// FatorEmbalagem)</c>.
/// </para>
///
/// <para>
/// <b>O que a reconciliação NÃO valida.</b> Ela é forte no tipo 2, onde a demanda entra na
/// fórmula e reproduzir o resultado é evidência de que a fórmula está certa. No tipo 1 ela
/// valida a posição de estoque, os pendentes e o arredondamento, mas <b>não</b> a
/// linearidade usada para reescalar o eMax: com a demanda do ERP a razão vale 1 e o
/// reescalonamento some da conta por construção. Se o eMax do ERP tiver componente fixo, o
/// braço ML do tipo 1 estará enviesado sem que nada aqui acuse. Está registrado no relatório
/// da tarefa como concern aberto.
/// </para>
///
/// <para>
/// <b>Ruptura</b> segue a decisão da camada A (<c>ForecastVsErpComparer</c>): o default
/// <c>ExcluirPar</c> descarta o item inteiro quando a janela teve ruptura, porque venda em
/// dia de ruptura subestima a demanda. <c>ExcluirDia</c> é recusado aqui — ver
/// <see cref="DecisionOptions.Ruptura"/>.
/// </para>
/// </summary>
public sealed class DecisionComparer
{
    private const string NomeErp = "erp-pbs";
    private const string NomeMl = "ml";
    private const string ParamPopulacao = "populacao";

    private readonly DecisionOptions _opt;

    public DecisionComparer(DecisionOptions? options = null)
    {
        _opt = options ?? new DecisionOptions();

        if (_opt.Ruptura == RupturaTratamento.ExcluirDia)
            throw new ArgumentException(
                "ExcluirDia não se aplica à camada de decisão: a compra é um escalar único " +
                "dimensionado para a janela inteira, então pontuá-la contra a venda de um " +
                "subconjunto de dias compararia unidades compradas para N dias com a demanda " +
                "de menos que N. Use ExcluirPar (padrão) ou Incluir como sensibilidade.",
                nameof(options));
    }

    public DecisionComparisonResult Compare(IReadOnlyList<DecisionItem> populacao)
    {
        ValidarHomogeneidade(populacao);

        var reconciliacao = new List<ItemReconciliado>(populacao.Count);
        var decisoes = new List<Decisao>(populacao.Count);
        var descartadosPorRuptura = 0;
        var semPreco = 0;

        foreach (var item in populacao)
        {
            ValidarItem(item);

            var recalculada = QuantidadeCompra(item, item.DemandaDiaErp);
            var divergencia = Math.Abs(recalculada - item.CompraSugerida);
            var status = Classificar(item, divergencia);

            reconciliacao.Add(new ItemReconciliado(
                item.SugestaoId, item.LojaId, item.Sku, status, item.CompraSugerida, recalculada, divergencia));

            if (status != StatusReconciliacao.Reconciliado) continue;

            var dias = DiasPontuaveis(item);
            if (dias.Count == 0)
            {
                descartadosPorRuptura++;
                continue;
            }

            if (item.PrecoCompra is null) semPreco++;

            var vendaReal = dias.Sum(d => d.Features.Target);
            var demandaDiaMl = Math.Max(0m, (decimal)dias.Average(d => d.PrevisaoMl));

            // O braço ERP usa o CompraSugerida gravado, não a nossa reconstrução: a decisão
            // do método atual tem de ser a que o ERP de fato tomou. A reconciliação é o que
            // autoriza pôr ao lado dela uma quantidade produzida pela nossa fórmula.
            var erp = Braco.Avaliar(item.CompraSugerida, PosicaoInicial(item), vendaReal, item.PrecoCompra);
            var ml = Braco.Avaliar(QuantidadeCompra(item, demandaDiaMl), PosicaoInicial(item), vendaReal, item.PrecoCompra);

            decisoes.Add(new Decisao(item, dias.Count, vendaReal, demandaDiaMl, erp, ml, Julgar(erp, ml)));
        }

        return new DecisionComparisonResult(
            populacao.Count,
            decisoes.Count,
            descartadosPorRuptura,
            semPreco,
            Resumir(reconciliacao),
            reconciliacao,
            Agregar(NomeErp, decisoes, d => d.Erp),
            Agregar(NomeMl, decisoes, d => d.Ml),
            Placar(decisoes),
            AgregarPorDimensao(decisoes),
            [.. decisoes.Select(d => new DecisaoComparada(
                d.Item.SugestaoId, d.Item.LojaId, d.Item.Sku, d.Item.Curva, d.Dias,
                d.VendaReal, d.Item.DemandaDiaErp, d.DemandaDiaMl,
                d.Erp.Compra, d.Ml.Compra,
                d.Erp.Excesso, d.Ml.Excesso,
                d.Erp.Falta, d.Ml.Falta,
                d.Resultado))]);
    }

    // --- Aritmética do ERP ---------------------------------------------------

    private static decimal PosicaoInicial(DecisionItem item) =>
        item.EstoqueSaldo + (item.ConsideraPedidosPendentes ? item.PedidosPendentes : 0m);

    private static decimal QuantidadeCompra(DecisionItem item, decimal demandaDia)
    {
        var necessidade = item.TipoCalculo switch
        {
            // Com DemandaDia zero a razão não existe e o eMax entra sem reescala. Isso
            // reconstrói o braço ERP (é até lá que ele repõe, qualquer que seja a demanda);
            // o braço ML nunca chega aqui nesse caso — o item vira BracoMlIndeterminado.
            1 when item.DemandaDiaErp <= 0m => item.EstoqueMaximo ?? 0m,
            1 => (item.EstoqueMaximo ?? 0m) * (demandaDia / item.DemandaDiaErp),
            2 => demandaDia * item.DiasEstoque,
            _ => throw new ArgumentException(
                $"TipoCalculo {item.TipoCalculo} não é modelado (só 1 = Emax e Eseg e " +
                "2 = Dias de Reposição). Produzir número para um método desconhecido seria " +
                "chutar a fórmula do ERP.",
                ParamPopulacao),
        };

        return ArredondarEmbalagem(Math.Max(0m, necessidade - PosicaoInicial(item)), item.FatorEmbalagem);
    }

    private static decimal ArredondarEmbalagem(decimal quantidade, decimal? fatorEmbalagem) =>
        fatorEmbalagem is not { } fator || fator <= 0m
            ? quantidade
            : Math.Ceiling(quantidade / fator) * fator;

    // --- Reconciliação -------------------------------------------------------

    private StatusReconciliacao Classificar(DecisionItem item, decimal divergencia)
    {
        if (item.TipoCalculo == 1 && item.DemandaDiaErp <= 0m)
            return StatusReconciliacao.BracoMlIndeterminado;

        return divergencia <= _opt.ToleranciaReconciliacao
            ? StatusReconciliacao.Reconciliado
            : StatusReconciliacao.Divergente;
    }

    private static ReconciliacaoResumo Resumir(List<ItemReconciliado> itens)
    {
        var tentados = itens.Where(i => i.Status != StatusReconciliacao.BracoMlIndeterminado).ToList();

        return new ReconciliacaoResumo(
            itens.Count,
            itens.Count(i => i.Status == StatusReconciliacao.Reconciliado),
            itens.Count(i => i.Status == StatusReconciliacao.Divergente),
            itens.Count(i => i.Status == StatusReconciliacao.BracoMlIndeterminado),
            tentados.Count == 0 ? 0m : tentados.Average(i => i.Divergencia),
            tentados.Count == 0 ? 0m : tentados.Max(i => i.Divergencia));
    }

    // --- Pontuação -----------------------------------------------------------

    private List<DiaAvaliado> DiasPontuaveis(DecisionItem item) =>
        _opt.Ruptura == RupturaTratamento.Incluir || item.Dias.All(d => d.Features.IsValidTarget)
            ? [.. item.Dias]
            : [];

    private ResultadoPar Julgar(Braco erp, Braco ml)
    {
        if (Math.Abs(ml.Desvio - erp.Desvio) <= _opt.EmpateTolerancia) return ResultadoPar.Empate;
        return ml.Desvio < erp.Desvio ? ResultadoPar.VitoriaMl : ResultadoPar.VitoriaErp;
    }

    private static ArmDecisionResult Agregar(string nome, List<Decisao> decisoes, Func<Decisao, Braco> selecionar)
    {
        decimal compradas = 0, valorComprado = 0, excesso = 0, excessoValor = 0, falta = 0, faltaValor = 0;
        var pontos = new List<(double Actual, double Predicted)>(decisoes.Count);

        foreach (var decisao in decisoes)
        {
            var braco = selecionar(decisao);
            compradas += braco.Compra;
            valorComprado += braco.Compra * braco.Preco;
            excesso += braco.Excesso;
            excessoValor += braco.Excesso * braco.Preco;
            falta += braco.Falta;
            faltaValor += braco.Falta * braco.Preco;
            pontos.Add(((double)decisao.VendaReal, (double)braco.Disponivel));
        }

        return new ArmDecisionResult(
            nome, compradas, valorComprado, excesso, excessoValor, falta,
            new VendaPerdidaIlustrativa(falta, faltaValor),
            ForecastMetrics.Compute(pontos));
    }

    private static WinRate Placar(IEnumerable<Decisao> decisoes)
    {
        int n = 0, ml = 0, erp = 0, empates = 0;
        foreach (var decisao in decisoes)
        {
            n++;
            switch (decisao.Resultado)
            {
                case ResultadoPar.VitoriaMl: ml++; break;
                case ResultadoPar.VitoriaErp: erp++; break;
                default: empates++; break;
            }
        }
        return new WinRate(n, ml, erp, empates);
    }

    // Curva e loja saem da própria linha do ERP — não dependem das features, ao contrário
    // de categoria/ABC. Curva é o eixo em que o método antigo se parametriza, logo o eixo
    // em que se espera que ele seja mais competitivo (CLAUDE.md §6: média global esconde
    // regressão local).
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, WinRate>> AgregarPorDimensao(
        List<Decisao> decisoes) =>
        decisoes.Count == 0
            ? new Dictionary<string, IReadOnlyDictionary<string, WinRate>>()
            : new Dictionary<string, IReadOnlyDictionary<string, WinRate>>
            {
                ["CurvaErp"] = Agrupar(decisoes, d => d.Item.Curva),
                ["Loja"] = Agrupar(decisoes, d => d.Item.LojaId.ToString()),
            };

    private static IReadOnlyDictionary<string, WinRate> Agrupar(
        List<Decisao> decisoes, Func<Decisao, string> chave) =>
        decisoes.GroupBy(chave)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(g => g.Key, Placar);

    // --- Validação -----------------------------------------------------------

    private void ValidarItem(DecisionItem item)
    {
        if (item.TipoCalculo == 1 && item.EstoqueMaximo is null)
            throw new ArgumentException(
                $"O item (sugestão {item.SugestaoId}, loja {item.LojaId}, sku {item.Sku}) é de " +
                "TipoCalculo 1 (Emax e Eseg) sem EstoqueMaximo. Sem ele não há nível de reposição " +
                "e a aritmética do ERP não pode ser modelada para esta linha.",
                ParamPopulacao);

        if (item.DemandaDiaErp < 0m)
            throw new ArgumentException(
                $"DemandaDia negativa ({item.DemandaDiaErp}) no item (sugestão {item.SugestaoId}, " +
                $"loja {item.LojaId}, sku {item.Sku}).",
                ParamPopulacao);

        if (item.DiasEstoque <= 0)
            throw new ArgumentException(
                $"DiasEstoque {item.DiasEstoque} no item (sugestão {item.SugestaoId}, loja " +
                $"{item.LojaId}, sku {item.Sku}). Sem dias de cobertura não há janela a pontuar.",
                ParamPopulacao);

        if (item.Dias.Count != item.DiasEstoque)
            throw new ArgumentException(
                $"O item (sugestão {item.SugestaoId}, loja {item.LojaId}, sku {item.Sku}) tem " +
                $"DiasEstoque {item.DiasEstoque} e {item.Dias.Count} dia(s) na janela. Pontuar uma " +
                "compra dimensionada para a cobertura inteira contra a venda de um recorte menor " +
                "produziria excesso nos dois braços só por causa do recorte.",
                ParamPopulacao);

        var corte = DateOnly.FromDateTime(item.DataHora);
        var fimJanela = corte.AddDays(item.DiasEstoque);

        if (item.ModeloTreinadoAte >= corte)
            throw new ArgumentException(
                $"Regra de informação violada: o modelo do item (sugestão {item.SugestaoId}, loja " +
                $"{item.LojaId}, sku {item.Sku}) foi treinado até {item.ModeloTreinadoAte:yyyy-MM-dd}, " +
                $"que não é anterior à DataHora da sugestão ({corte:yyyy-MM-dd}).",
                ParamPopulacao);

        if (item.PrecoCongeladoAPartirDe != corte)
            throw new ArgumentException(
                $"Regra de informação violada: o item (sugestão {item.SugestaoId}, loja {item.LojaId}, " +
                $"sku {item.Sku}) declara PrecoCongeladoAPartirDe = " +
                $"{item.PrecoCongeladoAPartirDe:yyyy-MM-dd}, mas a DataHora da sugestão é " +
                $"{corte:yyyy-MM-dd}.",
                ParamPopulacao);

        var datas = new HashSet<DateOnly>();

        foreach (var dia in item.Dias)
        {
            var f = dia.Features;

            if (f.LojaId != item.LojaId || !string.Equals(f.Sku, item.Sku, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Regra de população violada: o item (sugestão {item.SugestaoId}, loja " +
                    $"{item.LojaId}, sku {item.Sku}) recebeu um dia de (loja {f.LojaId}, sku {f.Sku}).",
                    ParamPopulacao);

            if (!datas.Add(f.Data))
                throw new ArgumentException(
                    $"Regra de população violada: o item (sugestão {item.SugestaoId}, loja " +
                    $"{item.LojaId}, sku {item.Sku}) repete o dia {f.Data:yyyy-MM-dd}, o que conta a " +
                    "venda dele duas vezes na janela.",
                    ParamPopulacao);

            if (f.Data < corte || f.Data >= fimJanela)
                throw new ArgumentException(
                    $"Regra de população violada: o dia {f.Data:yyyy-MM-dd} está fora da janela " +
                    $"coberta pela compra do item (sugestão {item.SugestaoId}, loja {item.LojaId}, " +
                    $"sku {item.Sku}), que vai de {corte:yyyy-MM-dd} (inclusive) a " +
                    $"{fimJanela:yyyy-MM-dd} (exclusive).",
                    ParamPopulacao);

            if (!double.IsFinite(dia.PrevisaoMl))
                throw new ArgumentException(
                    $"Previsão não finita ({dia.PrevisaoMl}) do braço ML no dia {f.Data:yyyy-MM-dd} do " +
                    $"item (sugestão {item.SugestaoId}, loja {item.LojaId}, sku {item.Sku}). " +
                    "Uma quantidade calculada a partir de NaN viraria derrota silenciosa do ML.",
                    ParamPopulacao);

            var observacaoAte = f.Data.AddDays(-_opt.LeadTimeDias);
            if (observacaoAte >= corte)
                throw new ArgumentException(
                    $"Regra de informação violada: o dia {f.Data:yyyy-MM-dd} de (loja {f.LojaId}, sku " +
                    $"{f.Sku}) é alimentado por observação até {observacaoAte:yyyy-MM-dd}, que não é " +
                    $"anterior à DataHora da sugestão ({corte:yyyy-MM-dd}).",
                    ParamPopulacao);
        }
    }

    private static void ValidarHomogeneidade(IReadOnlyList<DecisionItem> populacao)
    {
        if (populacao.Count == 0) return;

        var rede = populacao[0].RedeId;
        var tipo = populacao[0].TipoCalculo;
        var chaves = new HashSet<(long, int, string)>();

        foreach (var item in populacao)
        {
            if (item.TipoCalculo is not (1 or 2))
                throw new ArgumentException(
                    $"TipoCalculo {item.TipoCalculo} não é modelado (só 1 = Emax e Eseg e " +
                    "2 = Dias de Reposição). Produzir número para um método desconhecido seria " +
                    "chutar a fórmula do ERP.",
                    ParamPopulacao);

            if (item.RedeId != rede)
                throw new ArgumentException(
                    $"A população mistura as redes {rede} e {item.RedeId}. Com duas redes o estudo é " +
                    "multi-caso, não amostra — o resultado sai sempre por rede.",
                    ParamPopulacao);

            if (item.TipoCalculo != tipo)
                throw new ArgumentException(
                    $"A população mistura TipoCalculo {tipo} e {item.TipoCalculo}. São baselines " +
                    "distintos do ERP; média entre eles não significa nada.",
                    ParamPopulacao);

            if (!chaves.Add((item.SugestaoId, item.LojaId, item.Sku)))
                throw new ArgumentException(
                    $"A população repete o item (sugestão {item.SugestaoId}, loja {item.LojaId}, " +
                    $"sku {item.Sku}). Linha duplicada pesa duas vezes na taxa de vitória e nos " +
                    "agregados — sintoma de join que multiplicou linhas.",
                    ParamPopulacao);
        }
    }

    /// <param name="Disponivel">
    /// Posição resultante: estoque + pendentes + compra. É o que o braço teria para atender
    /// a janela, e o ponto de comparação com a venda real.
    /// </param>
    /// <param name="Desvio">
    /// |posição resultante − venda real|. Arbitra o vencedor do item sem precisar arbitrar
    /// quanto custa uma unidade parada contra uma unidade faltando — peso que este projeto
    /// não tem como calibrar.
    /// </param>
    private readonly record struct Braco(
        decimal Compra,
        decimal Disponivel,
        decimal Excesso,
        decimal Falta,
        decimal Desvio,
        decimal Preco)
    {
        public static Braco Avaliar(decimal compra, decimal posicaoInicial, decimal vendaReal, decimal? preco)
        {
            var disponivel = posicaoInicial + compra;
            return new Braco(
                compra,
                disponivel,
                Math.Max(0m, disponivel - vendaReal),
                Math.Max(0m, vendaReal - disponivel),
                Math.Abs(disponivel - vendaReal),
                preco ?? 0m);
        }
    }

    private readonly record struct Decisao(
        DecisionItem Item,
        int Dias,
        decimal VendaReal,
        decimal DemandaDiaMl,
        Braco Erp,
        Braco Ml,
        ResultadoPar Resultado);
}
