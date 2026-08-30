using CosmosPro.ML.DemandForCast.Engine.Mercado;
using CosmosPro.ML.DemandForCast.Worker.Mercado;

namespace CosmosPro.ML.DemandForCast.Worker.Tests;

/// <summary>
/// Regras B2, B3 e B6 do documento de controle da IQVIA. O que se afirma aqui não é
/// aritmética — é a fronteira entre quatro afirmações que a tela faz ao comprador, e três
/// delas são fáceis de colapsar numa só por descuido: "está bem", "está mal por ruptura",
/// "está mal sem causa aparente" e "está mal e ninguém checou o estoque". Colapsar a
/// última na terceira faz o software afirmar que não houve ruptura sem ter olhado.
/// </summary>
public sealed class MercadoAlertaCalculadorTests
{
    /// <summary>
    /// Fatia que a rede tem no brick somando todos os EANs — a referência interna contra a
    /// qual cada item é medido. 20% é o número real do brick 526 em junho/2026.
    /// </summary>
    private const decimal FatiaAgregada = 0.20m;

    [Fact]
    public void Item_no_mesmo_patamar_da_rede_tem_indice_um_e_nao_alerta()
    {
        // 20 nossas de 100 no brick = 20% = exatamente a fatia agregada.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(UnidadesRede: 20m, UnidadesConcorrentes: 80m,
                           FatiaAgregadaDaRede: FatiaAgregada, DiasSemEstoque: 0));

        r.Should().NotBeNull();
        r!.Value.Indice.Should().BeApproximately(1m, 0.0001m);
        r.Value.Alerta.Should().Be(MercadoAlertas.SemAlerta);
    }

    [Fact]
    public void O_limiar_de_cinquenta_por_cento_e_estrito()
    {
        // 10 de 100 = 10% = metade da fatia agregada -> índice exatamente 0,5.
        // "Mais de 50% abaixo" não inclui os 50% cravados.
        var noLimiar = MercadoAlertaCalculador.Calcular(
            new SinalBruto(10m, 90m, FatiaAgregada, DiasSemEstoque: 0));

        noLimiar!.Value.Indice.Should().BeApproximately(0.5m, 0.0001m);
        noLimiar.Value.Alerta.Should().Be(MercadoAlertas.SemAlerta);
    }

    [Fact]
    public void Abaixo_do_limiar_com_dia_sem_estoque_a_ruptura_explica()
    {
        // 5 de 100 = 5% -> índice 0,25.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(5m, 95m, FatiaAgregada, DiasSemEstoque: 3));

        r!.Value.Indice.Should().BeApproximately(0.25m, 0.0001m);
        r.Value.Alerta.Should().Be(MercadoAlertas.Ruptura);
    }

    [Fact]
    public void Abaixo_do_limiar_sem_dia_sem_estoque_fica_sem_causa()
    {
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(5m, 95m, FatiaAgregada, DiasSemEstoque: 0));

        r!.Value.Alerta.Should().Be(MercadoAlertas.SemCausa);
    }

    [Fact]
    public void Estoque_nao_apurado_nunca_vira_sem_causa()
    {
        // Nulo = o mês comparado não está no histórico de estoque importado. Dizer
        // "sem causa" afirmaria que não houve ruptura, o que ninguém verificou.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(5m, 95m, FatiaAgregada, DiasSemEstoque: null));

        r!.Value.Alerta.Should().Be(MercadoAlertas.NaoApurado);
    }

    [Fact]
    public void Venda_nossa_zero_com_mercado_vendendo_e_o_alerta_mais_forte()
    {
        // Zero é medição, não ausência: o item está no cadastro, está na sugestão, o
        // bairro vende, e a rede vendeu nada. Índice zero e alerta, nunca nulo.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(0m, 500m, FatiaAgregada, DiasSemEstoque: 0));

        r.Should().NotBeNull();
        r!.Value.Indice.Should().Be(0m);
        r.Value.Alerta.Should().Be(MercadoAlertas.SemCausa);
    }

    [Fact]
    public void Item_acima_do_patamar_da_rede_nao_alerta()
    {
        // 50 de 100 = 50%, contra fatia agregada de 20% -> índice 2,5.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(50m, 50m, FatiaAgregada, DiasSemEstoque: 0));

        r!.Value.Indice.Should().BeApproximately(2.5m, 0.0001m);
        r.Value.Alerta.Should().Be(MercadoAlertas.SemAlerta);
    }

    [Fact]
    public void Ninguem_vendendo_no_recorte_nao_tem_indice()
    {
        // Nem a rede nem os concorrentes. Devolver zero afirmaria desempenho péssimo
        // onde não houve medição nenhuma.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(0m, 0m, FatiaAgregada, DiasSemEstoque: 0));

        r.Should().BeNull();
    }

    [Fact]
    public void Rede_sem_venda_no_brick_inteiro_nao_tem_indice()
    {
        // Fatia agregada zero: dividir por ela produziria infinito, e "infinitamente
        // acima do normal" não é afirmação que a tela possa fazer.
        var r = MercadoAlertaCalculador.Calcular(
            new SinalBruto(10m, 90m, FatiaAgregadaDaRede: 0m, DiasSemEstoque: 0));

        r.Should().BeNull();
    }

    [Fact]
    public void O_vocabulario_cabe_na_coluna_do_banco()
    {
        // A coluna é declarada com MercadoAlertas.TamanhoMaximo. Se um valor novo
        // passar do tamanho, o SqlBulkCopy estoura na materialização -- três fases
        // depois de quem introduziu o valor.
        string[] todos =
        [
            MercadoAlertas.SemAlerta,
            MercadoAlertas.Ruptura,
            MercadoAlertas.SemCausa,
            MercadoAlertas.NaoApurado,
        ];

        todos.Should().OnlyContain(v => v.Length <= MercadoAlertas.TamanhoMaximo);
    }
}
