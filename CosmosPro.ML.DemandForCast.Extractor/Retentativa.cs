using System.Globalization;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Repete leitura curta e idempotente quando a falha é transitória. O <c>dormir</c>
/// é parâmetro para o teste não precisar esperar de verdade.
/// </summary>
internal static class Retentativa
{
    public const int TentativasPadrao = 3;
    public static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromSeconds(2);

    public static Result<T> Executar<T>(
        Func<Result<T>> tentativa, int tentativas, Action<string> log, Action<TimeSpan> dormir)
    {
        var resultado = tentativa();

        for (var numero = 2; numero <= tentativas; numero++)
        {
            if (resultado.IsSuccess) return resultado;
            if (resultado.Errors.FirstOrDefault() is not ExtratorErro { Transitorio: true } erro) return resultado;

            log($"{erro.Message} Retentando (tentativa {numero.ToString(CultureInfo.InvariantCulture)} "
                + $"de {tentativas.ToString(CultureInfo.InvariantCulture)}).");
            dormir(EsperaEntreTentativas);
            resultado = tentativa();
        }

        return resultado;
    }
}
