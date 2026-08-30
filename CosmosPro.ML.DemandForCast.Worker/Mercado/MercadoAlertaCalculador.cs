using CosmosPro.ML.DemandForCast.Engine.Mercado;

namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

/// <summary>
/// Medidas de um item num recorte da IQVIA (um EAN, num brick, num mês), prontas para
/// classificação. Quem as busca no banco é o <c>MercadoSinalLoader</c>.
/// </summary>
/// <param name="UnidadesRede">
/// Unidades que a IQVIA atribuiu às bandeiras próprias da rede. <b>Zero é medição</b>, não
/// ausência: dentro de um recorte coberto, zero com concorrentes vendendo significa que o
/// bairro vende e a rede não vendeu nada.
/// </param>
/// <param name="FatiaAgregadaDaRede">
/// Fatia de unidades que a rede tem no brick e mês somando <b>todos</b> os EANs. É a
/// referência interna: o índice mede o item contra ela, e não contra o número de lojas.
/// A régua por número de lojas exigiria o contador de PDVs concorrentes, que o relatório
/// da IQVIA só publica numa área de tabela dinâmica que o parser ignora.
/// </param>
/// <param name="DiasSemEstoque">
/// Dias em que a loja ficou sem estoque do SKU <b>no mês comparado</b> — não na janela de
/// cobertura da sugestão. Nulo quando aquele mês não está no histórico de estoque
/// importado, o que é diferente de zero: zero afirma que havia estoque todos os dias.
/// </param>
internal readonly record struct SinalBruto(
    decimal UnidadesRede,
    decimal UnidadesConcorrentes,
    decimal FatiaAgregadaDaRede,
    int? DiasSemEstoque);

internal readonly record struct AlertaCalculado(decimal Indice, string Alerta);

/// <summary>
/// Regras B2, B3 e B6 do documento de controle da IQVIA, em forma pura.
///
/// <para>
/// <b>B4 (desvio de preço) e B5 estão fora, e não por prioridade.</b> O relatório não
/// contém preço praticado por concorrente: <c>Real CPP / Unidades</c> é idêntico entre
/// bandeiras no mesmo brick e mês (37.410 pares medidos, zero diferença) e idêntico entre
/// bricks (55.979 pares), variando só por mês — é um preço de referência normalizado. B4
/// devolveria sempre um número, e o número não significaria o que a regra promete.
/// </para>
/// </summary>
internal static class MercadoAlertaCalculador
{
    /// <summary>
    /// "Mais de 50% abaixo do mercado" do documento de controle, lido literalmente:
    /// dispara com índice <b>estritamente</b> menor que 0,5. Índice de 0,5 cravado não é
    /// "mais de 50% abaixo".
    /// </summary>
    public const decimal LimiarDeAlerta = 0.5m;

    /// <returns>
    /// Nulo quando não há como calcular índice — e nulo é o que a coluna recebe. Devolver
    /// zero afirmaria desempenho péssimo onde não houve medição nenhuma.
    /// </returns>
    public static AlertaCalculado? Calcular(SinalBruto sinal)
    {
        var totalDoBrick = sinal.UnidadesRede + sinal.UnidadesConcorrentes;

        // Ninguém vendeu no recorte, ou a rede não vendeu nada no brick inteiro. O segundo
        // caso dividiria por zero, e "infinitamente acima do normal" não é afirmação que a
        // tela possa fazer.
        if (totalDoBrick <= 0m || sinal.FatiaAgregadaDaRede <= 0m) return null;

        var fatiaDoItem = sinal.UnidadesRede / totalDoBrick;
        var indice = fatiaDoItem / sinal.FatiaAgregadaDaRede;

        var alerta = indice >= LimiarDeAlerta
            ? MercadoAlertas.SemAlerta
            : sinal.DiasSemEstoque switch
            {
                null => MercadoAlertas.NaoApurado,
                > 0 => MercadoAlertas.Ruptura,
                _ => MercadoAlertas.SemCausa,
            };

        return new AlertaCalculado(indice, alerta);
    }
}
