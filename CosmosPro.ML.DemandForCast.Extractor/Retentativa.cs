using System.Globalization;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Repete leitura curta e idempotente quando a falha é transitória. O <c>dormir</c>
/// é parâmetro para o teste não precisar esperar de verdade, e recebe o token para a
/// espera real (<see cref="Dormir"/>) devolver assim que o operador cancelar, em vez
/// de segurar Cancelar mudo pelos 2s inteiros do backoff.
/// </summary>
internal static class Retentativa
{
    public const int TentativasPadrao = 3;
    public static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromSeconds(2);

    public static Result<T> Executar<T>(
        Func<Result<T>> tentativa, int tentativas, Action<string> log,
        Action<CancellationToken, TimeSpan> dormir, CancellationToken ct)
    {
        var resultado = tentativa();

        for (var numero = 2; numero <= tentativas; numero++)
        {
            // Checado no topo de cada volta: pega o cancelamento pedido durante a
            // espera da volta anterior antes de logar e dormir de novo. A espera da
            // ÚLTIMA volta não tem uma volta seguinte para checar aqui -- mas
            // <c>tentativa</c> é sempre CatalogoService.Consultar, que já checa o
            // token antes do ExecuteReader, então essa espera também não escapa.
            ct.ThrowIfCancellationRequested();

            if (resultado.IsSuccess) return resultado;
            if (resultado.Errors.FirstOrDefault() is not ExtratorErro { Transitorio: true } erro) return resultado;

            log($"{erro.Message} Retentando (tentativa {numero.ToString(CultureInfo.InvariantCulture)} "
                + $"de {tentativas.ToString(CultureInfo.InvariantCulture)}).");
            dormir(ct, EsperaEntreTentativas);
            resultado = tentativa();
        }

        return resultado;
    }

    /// <summary>
    /// Espera real de produção: <c>WaitOne</c> devolve na hora se <paramref name="ct"/>
    /// for cancelado, ao contrário do <c>Thread.Sleep</c> anterior, que era surdo a
    /// Cancelar pelos 2s inteiros do backoff.
    /// </summary>
    public static void Dormir(CancellationToken ct, TimeSpan espera) => ct.WaitHandle.WaitOne(espera);
}
