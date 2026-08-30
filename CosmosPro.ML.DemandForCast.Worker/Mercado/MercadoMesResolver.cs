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
}
