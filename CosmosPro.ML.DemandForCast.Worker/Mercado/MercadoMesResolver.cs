namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

/// <summary>
/// Escolhe qual mês da IQVIA a sessão compara: o <b>último mês coberto estritamente
/// anterior ao mês da sugestão</b>. Uma regra, sem caso especial.
///
/// <para>
/// <b>Por que estritamente anterior.</b> O mês da sugestão contém as consequências da
/// própria sugestão. Para um diagnóstico retrospectivo isso passaria; para a afirmação que
/// a dissertação sustenta — "o alerta da IQVIA teria avisado o comprador" — é circular. O
/// atraso real da fonte confirma a regra: o relatório de junho/2026 chegou em agosto/2026,
/// então em junho o comprador não tinha junho.
/// </para>
///
/// <para>
/// Com um relatório só carregado, a regra cai no <b>espelho do ano anterior</b>, que todo
/// arquivo traz e que é sazonalmente casado. Conforme a rede empilha relatórios mensais,
/// ela passa ao mês imediatamente anterior sem mudança de código.
/// </para>
/// </summary>
internal static class MercadoMesResolver
{
    /// <param name="mesesCobertos">
    /// Meses efetivamente cobertos pelas cargas de mercado da rede. Vem da cobertura
    /// declarada, <b>nunca</b> da existência de linhas em <c>MercadoObservacoes</c>: célula
    /// zerada não gera linha, e inferir cobertura da ausência confundiria "vendeu zero" com
    /// "nunca foi enviado".
    /// </param>
    /// <param name="diaDaSugestao">Dia da sugestão do ERP. Só o mês dele é usado.</param>
    public static DateOnly? Resolver(IEnumerable<DateOnly> mesesCobertos, DateOnly diaDaSugestao)
    {
        var corte = new DateOnly(diaDaSugestao.Year, diaDaSugestao.Month, 1);

        DateOnly? escolhido = null;
        foreach (var mes in mesesCobertos)
        {
            if (mes >= corte) continue;
            if (escolhido is null || mes > escolhido.Value) escolhido = mes;
        }

        return escolhido;
    }

    /// <summary>
    /// O mês comparado cabe inteiro na janela de snapshots de estoque? Só então dá para
    /// contar dias sem estoque; fora dela a ruptura é <b>não apurada</b>, e não zero.
    ///
    /// <para>
    /// <b>Os dois extremos, e não só o começo.</b> Mês parcialmente coberto subcontaria os
    /// dias sem estoque -- os dias que faltam contam como se tivessem estoque -- e um item
    /// com ruptura real sairia classificado como <c>SemCausa</c>, que é a afirmação oposta.
    /// </para>
    ///
    /// <para>
    /// <b>A comparação é com o HISTÓRICO, nunca com o dia da sugestão.</b> O mês comparado é
    /// sempre estritamente anterior ao mês da sugestão (é a regra de <see cref="Resolver"/>),
    /// então uma guarda contra o dia da sugestão é verdadeira sempre e zera a regra B3
    /// inteira. Foi o defeito que fez a tela dizer "estoque não apurado" em 100% das linhas.
    /// </para>
    /// </summary>
    public static bool CabeNoHistorico(DateOnly mes, DateOnly primeiroDia, DateOnly ultimoDia)
    {
        var fimDoMes = mes.AddMonths(1).AddDays(-1);
        return mes >= primeiroDia && fimDoMes <= ultimoDia;
    }
}
