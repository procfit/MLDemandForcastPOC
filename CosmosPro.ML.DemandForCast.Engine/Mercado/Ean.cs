namespace CosmosPro.ML.DemandForCast.Engine.Mercado;

/// <summary>
/// Normalização do código de barras. Vive no Engine porque os três lados do join precisam
/// da <b>mesma</b> regra: quem grava o catálogo da rede (import), quem grava as observações
/// da IQVIA (parser) e quem cruza os dois (sinal de mercado e tela de oportunidades).
/// Duplicar isto é convidar as cópias a divergirem, e a divergência é silenciosa.
/// </summary>
public static class Ean
{
    /// <summary>
    /// Só dígitos, sem zeros à esquerda.
    ///
    /// <para>
    /// <b>Por que existe, medido em 2026-08-30 contra o cadastro real:</b> o PBS grava o
    /// código com 14 caracteres e zero à esquerda (<c>07896094928060</c>) e a IQVIA grava 13
    /// (<c>7891721201806</c>). Comparação exata casa <b>zero</b> — nenhum produto, em nenhuma
    /// sessão. E a falha não levanta exceção nem escreve log: o dicionário sai vazio, a tela
    /// mostra travessão em toda linha, e a lista de oportunidades passa a ser o cadastro
    /// inteiro.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>null</c> para entrada vazia, sem dígito, ou toda zero — nenhuma delas identifica
    /// produto, e gravá-las criaria uma chave que casa com lixo do outro lado.
    /// </returns>
    public static string? Normalizar(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;

        var digitos = new string([.. bruto.Where(char.IsAsciiDigit)]).TrimStart('0');
        return digitos.Length == 0 ? null : digitos;
    }
}
