using CosmosPro.ML.DemandForCast.Features.Models;
using CosmosPro.ML.DemandForCast.Purchasing;
using CosmosPro.ML.DemandForCast.Purchasing.Policies;
using CosmosPro.ML.DemandForCast.Purchasing.Simulation;

namespace CosmosPro.ML.DemandForCast.Purchasing.Tests;

public sealed class PurchasingSimulatorTests
{
    private static readonly DateOnly Origem = new(2026, 1, 1);

    /// <summary>
    /// Gera uma série diária densa, demanda constante = <paramref name="qty"/>,
    /// sem ruptura. Usado para verificações determinísticas do simulador.
    /// </summary>
    private static List<DailyObservation> SeriesConstante(int dias, decimal qty)
    {
        var list = new List<DailyObservation>(dias);
        for (int i = 0; i < dias; i++)
        {
            list.Add(new DailyObservation
            {
                Data = Origem.AddDays(i),
                LojaId = 1,
                Sku = "SKU1",
                Quantidade = qty,
                EmRuptura = false,
                EmPromocao = false,
                PrecoUnitario = 10,
                Categoria = "Cat",
                PrincipioAtivo = "PA",
                ClasseAbc = "A",
                UF = "SP",
                Regiao = "Sudeste",
                PerfilLoja = "P",
            });
        }
        return list;
    }

    [Fact]
    public void Com_estoque_alto_e_demanda_constante_o_nivel_de_servico_eh_total()
    {
        var obs = SeriesConstante(dias: 30, qty: 10m);
        var key = new PurchasingSimulator.SerieKey("SKU1", 1);
        var estoqueInicial = new Dictionary<PurchasingSimulator.SerieKey, decimal> { [key] = 1000m };
        var atributos = new Dictionary<PurchasingSimulator.SerieKey, PurchasingSimulator.SerieAttributes>
        {
            [key] = new("SKU1", 1, "Cat", "A", "SP", "Sudeste"),
        };
        var options = new SimulationOptions
        {
            DataInicio = Origem.AddDays(7),  // pula header pra ter histórico
            DataFim = Origem.AddDays(20),
            LeadTimeDias = 7,
            CicloDias = 7,
            FatorServico = 1.65,
        };
        var sim = new PurchasingSimulator();

        var r = sim.Run(options, obs, estoqueInicial, atributos, [new ForecastRopPolicy()]);

        r.Politicas.Should().HaveCount(1);
        var kpi = r.Politicas[0].Global;
        kpi.NivelServicoUnidades.Should().Be(1.0); // todo dia atendido
        kpi.DiasComRuptura.Should().Be(0);
        kpi.VendaPerdida.Should().Be(0);
    }

    [Fact]
    public void Sem_estoque_inicial_e_sem_pedido_gera_ruptura_total()
    {
        var obs = SeriesConstante(dias: 30, qty: 10m);
        var key = new PurchasingSimulator.SerieKey("SKU1", 1);
        var estoqueInicial = new Dictionary<PurchasingSimulator.SerieKey, decimal> { [key] = 0 };
        var atributos = new Dictionary<PurchasingSimulator.SerieKey, PurchasingSimulator.SerieAttributes>
        {
            [key] = new("SKU1", 1, "Cat", "A", "SP", "Sudeste"),
        };

        // Política "fantoche" que nunca pede — força ruptura.
        var policy = new NeverOrderPolicy();
        var options = new SimulationOptions
        {
            DataInicio = Origem.AddDays(7),
            DataFim = Origem.AddDays(13),
            LeadTimeDias = 7, CicloDias = 7, FatorServico = 0,
        };

        var r = new PurchasingSimulator().Run(options, obs, estoqueInicial, atributos, [policy]);
        var kpi = r.Politicas[0].Global;

        kpi.NivelServicoUnidades.Should().Be(0);
        kpi.DiasComRuptura.Should().Be(kpi.DiasTotais);
        kpi.VendaPerdida.Should().Be(kpi.DemandaTotal);
    }

    [Fact]
    public void Pedido_chega_no_dia_da_data_mais_leadtime_e_nao_antes()
    {
        // Demanda 0 (estoque parado) — só observamos quando os pedidos chegam.
        var obs = SeriesConstante(dias: 30, qty: 0m);
        var key = new PurchasingSimulator.SerieKey("SKU1", 1);
        var estoqueInicial = new Dictionary<PurchasingSimulator.SerieKey, decimal> { [key] = 0 };
        var atributos = new Dictionary<PurchasingSimulator.SerieKey, PurchasingSimulator.SerieAttributes>
        {
            [key] = new("SKU1", 1, "Cat", "A", "SP", "Sudeste"),
        };
        var policy = new AlwaysOrder100OnceThenStopPolicy();
        var options = new SimulationOptions
        {
            DataInicio = Origem.AddDays(7),
            DataFim = Origem.AddDays(20),
            LeadTimeDias = 7, CicloDias = 7, FatorServico = 0,
        };

        var r = new PurchasingSimulator().Run(options, obs, estoqueInicial, atributos, [policy]);
        var kpi = r.Politicas[0].Global;

        // 1 pedido lançado de 100 unidades — só deve estar disponível depois de 7 dias.
        kpi.Pedidos.Should().Be(1);
        kpi.UnidadesPedidas.Should().Be(100);
    }

    [Fact]
    public void Drill_down_agrega_por_categoria()
    {
        // 2 séries: SKU1/Cat-OTC e SKU2/Cat-RX. Cada uma roda independente; drill por Categoria deve ter ambas.
        var obs = SeriesConstante(30, 10m);
        obs.AddRange(SeriesConstante(30, 10m).Select(o => o with { Sku = "SKU2", Categoria = "RX" }));
        var keys = new[]
        {
            new PurchasingSimulator.SerieKey("SKU1", 1),
            new PurchasingSimulator.SerieKey("SKU2", 1),
        };
        var estoqueInicial = keys.ToDictionary(k => k, _ => 1000m);
        var atributos = new Dictionary<PurchasingSimulator.SerieKey, PurchasingSimulator.SerieAttributes>
        {
            [keys[0]] = new("SKU1", 1, "Cat", "A", "SP", "Sudeste"),
            [keys[1]] = new("SKU2", 1, "RX", "B", "SP", "Sudeste"),
        };
        var options = new SimulationOptions
        {
            DataInicio = Origem.AddDays(7),
            DataFim = Origem.AddDays(14),
            LeadTimeDias = 7, CicloDias = 7, FatorServico = 1.65,
        };

        var r = new PurchasingSimulator().Run(options, obs, estoqueInicial, atributos, [new ForecastRopPolicy()]);
        var porCat = r.Politicas[0].PorDimensao["Categoria"];

        porCat.Should().ContainKeys("Cat", "RX");
        // Cada categoria tem 1 série × 8 dias.
        porCat["Cat"].DiasTotais.Should().Be(8);
        porCat["RX"].DiasTotais.Should().Be(8);
    }

    [Fact]
    public void Politicas_partem_do_mesmo_estado_inicial()
    {
        // Estoque inicial e demanda iguais; duas políticas idênticas devem produzir KPIs idênticos.
        var obs = SeriesConstante(30, 10m);
        var key = new PurchasingSimulator.SerieKey("SKU1", 1);
        var atributos = new Dictionary<PurchasingSimulator.SerieKey, PurchasingSimulator.SerieAttributes>
        {
            [key] = new("SKU1", 1, "Cat", "A", "SP", "Sudeste"),
        };
        var estoqueInicial = new Dictionary<PurchasingSimulator.SerieKey, decimal> { [key] = 50m };
        var options = new SimulationOptions
        {
            DataInicio = Origem.AddDays(7), DataFim = Origem.AddDays(20),
            LeadTimeDias = 7, CicloDias = 7, FatorServico = 1.65,
        };

        var r = new PurchasingSimulator().Run(
            options, obs, estoqueInicial, atributos,
            [new ForecastRopPolicy(), new ForecastRopPolicy()]);

        // Resultado deve ser bit-exato (mesma política, mesma seed implícita do replay determinístico).
        r.Politicas[0].Global.Should().BeEquivalentTo(r.Politicas[1].Global);
    }

    [Fact]
    public void Livro_de_pedidos_registra_cada_pedido_lancado()
    {
        var obs = SeriesConstante(dias: 30, qty: 0m);
        var key = new PurchasingSimulator.SerieKey("SKU1", 1);
        var estoqueInicial = new Dictionary<PurchasingSimulator.SerieKey, decimal> { [key] = 0 };
        var atributos = new Dictionary<PurchasingSimulator.SerieKey, PurchasingSimulator.SerieAttributes>
        {
            [key] = new("SKU1", 1, "Cat", "A", "SP", "Sudeste"),
        };
        var policy = new AlwaysOrder100OnceThenStopPolicy();
        var options = new SimulationOptions
        {
            DataInicio = Origem.AddDays(7), DataFim = Origem.AddDays(20),
            LeadTimeDias = 7, CicloDias = 7, FatorServico = 0,
        };

        var r = new PurchasingSimulator().Run(options, obs, estoqueInicial, atributos, [policy]);
        var pedidos = r.Politicas[0].Pedidos;

        pedidos.Should().HaveCount(1);
        pedidos[0].Sku.Should().Be("SKU1");
        pedidos[0].Quantidade.Should().Be(100);
        pedidos[0].Data.Should().Be(options.DataInicio); // pediu no 1º dia
        r.Politicas[0].Global.Pedidos.Should().Be(1); // KPI bate com o livro
    }

    [Fact]
    public void Snapshot_do_ultimo_dia_tem_uma_linha_por_serie_com_decisao()
    {
        // 2 séries, demanda alta o suficiente pra esvaziar e disparar pedido no fim.
        var obs = SeriesConstante(30, 10m);
        obs.AddRange(SeriesConstante(30, 10m).Select(o => o with { Sku = "SKU2" }));
        var keys = new[]
        {
            new PurchasingSimulator.SerieKey("SKU1", 1),
            new PurchasingSimulator.SerieKey("SKU2", 1),
        };
        var estoqueInicial = keys.ToDictionary(k => k, _ => 5m); // pouco estoque → vai pedir
        var atributos = new Dictionary<PurchasingSimulator.SerieKey, PurchasingSimulator.SerieAttributes>
        {
            [keys[0]] = new("SKU1", 1, "Cat", "A", "SP", "Sudeste"),
            [keys[1]] = new("SKU2", 1, "Cat", "A", "SP", "Sudeste"),
        };
        var options = new SimulationOptions
        {
            DataInicio = Origem.AddDays(7), DataFim = Origem.AddDays(20),
            LeadTimeDias = 7, CicloDias = 7, FatorServico = 1.65,
        };

        var r = new PurchasingSimulator().Run(options, obs, estoqueInicial, atributos, [new ForecastRopPolicy()]);
        var snap = r.Politicas[0].ListaCompraFinal;

        // Uma linha por série, com S >= s e qtd sugerida = max(0, S - posição) quando dispara.
        snap.Should().HaveCount(2);
        snap.Should().OnlyContain(i => i.OrderUpToLevel >= i.ReorderPoint);
        snap.Should().Contain(i => i.Sku == "SKU1");
        snap.Should().Contain(i => i.Sku == "SKU2");
    }

    private sealed class NeverOrderPolicy : IPurchasingPolicy
    {
        public string Name => "never";
        public PolicyParameters Compute(PolicyContext context) => new(0m, 0m);
    }

    private sealed class AlwaysOrder100OnceThenStopPolicy : IPurchasingPolicy
    {
        private bool _done;
        public string Name => "once";
        public PolicyParameters Compute(PolicyContext context)
        {
            if (_done) return new(0m, 0m);
            _done = true;
            // Posição == 0, então retornar S=100 dispara um pedido de 100.
            return new(decimal.MaxValue, 100m);
        }
    }
}
