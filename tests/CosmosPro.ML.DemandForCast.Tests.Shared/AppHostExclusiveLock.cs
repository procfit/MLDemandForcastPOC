namespace CosmosPro.ML.DemandForCast.Tests.Shared;

/// <summary>
/// Exclusão mútua <b>entre processos</b> para quem sobe o AppHost de teste.
/// <para>
/// Os containers do Aspire são <c>ContainerLifetime.Persistent</c>: os bancos
/// <c>Stage</c> e <c>engine</c> e o MinIO são os <b>mesmos</b> para
/// qualquer AppHost que suba a partir deste repositório. Dois AppHosts vivos ao
/// mesmo tempo publicam o mesmo DACPAC, rodam as mesmas migrations, semeiam
/// Identity no mesmo banco e colocam dois Workers (que fazem polling cross-rede)
/// disputando a mesma fila. O invariante é: <b>um AppHost por vez</b>.
/// </para>
/// <para>
/// O <c>ICollectionFixture</c> garante isso só <i>dentro</i> de um projeto de teste.
/// Entre projetos não garante nada: <c>dotnet test</c> na solução executa o target
/// <c>VSTest</c> por projeto, cada um num <c>vstest.console</c> próprio, e o MSBuild
/// agenda esses targets em paralelo. Nenhuma configuração de runsettings alcança
/// esse paralelismo (<c>RunConfiguration/MaxCpuCount</c> só ordena <i>sources</i>
/// dentro de uma mesma execução do vstest, e aqui cada execução recebe um assembly
/// só) — por isso o lock vive aqui, no código que conhece o invariante, e não na
/// configuração do runner. Assim vale também no Test Explorer e em CI.
/// </para>
/// <para>
/// Primitiva: arquivo aberto com <c>FileShare.None</c>. Escolhido sobre
/// <c>Mutex</c> (que tem afinidade de thread e não sobrevive a <c>await</c>) e
/// sobre <c>Semaphore</c> nomeado (cuja contagem não é devolvida quando o processo
/// dono morre, o que travaria a suíte para sempre). O sistema operacional fecha o
/// handle quando o processo termina, inclusive se ele quebrar.
/// </para>
/// </summary>
public sealed class AppHostExclusiveLock : IAsyncDisposable
{
    private static readonly TimeSpan EsperaPadrao = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IntervaloTentativa = TimeSpan.FromMilliseconds(500);

    private readonly FileStream _arquivo;

    private AppHostExclusiveLock(FileStream arquivo) => _arquivo = arquivo;

    /// <summary>
    /// Espera até ser o único processo com o AppHost desta solução no ar.
    /// A espera é longa de propósito: subir o AppHost leva ~60-90s e a suíte de
    /// integração inteira roda dentro do lock, então o vizinho legitimamente demora.
    /// </summary>
    public static async Task<AppHostExclusiveLock> AcquireAsync(
        TimeSpan? espera = null, CancellationToken ct = default)
    {
        var caminho = Path.Combine(
            Path.GetTempPath(), "cosmospro-mldemandforecast-apphost.lock");
        var limite = DateTimeOffset.UtcNow + (espera ?? EsperaPadrao);

        while (true)
        {
            try
            {
                var arquivo = new FileStream(
                    caminho, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                // Registra quem detém o lock: se a suíte travar esperando, o arquivo
                // (legível assim que o dono soltar) diz qual processo era.
                await arquivo.WriteAsync(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"pid={Environment.ProcessId} desde={DateTimeOffset.UtcNow:O}\n"),
                    ct);
                await arquivo.FlushAsync(ct);

                return new AppHostExclusiveLock(arquivo);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < limite)
            {
                await Task.Delay(IntervaloTentativa, ct);
            }
            catch (IOException ex)
            {
                throw new TimeoutException(
                    $"Outro processo manteve o AppHost de teste por mais de " +
                    $"{(espera ?? EsperaPadrao).TotalMinutes:F0} min (lock em '{caminho}'). " +
                    "Verifique se sobrou um `dotnet test` ou um AppHost em debug rodando.",
                    ex);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _arquivo.Dispose();
        return ValueTask.CompletedTask;
    }
}
