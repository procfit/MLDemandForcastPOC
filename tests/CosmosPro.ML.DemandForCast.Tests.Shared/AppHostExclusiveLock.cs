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
    // Cinco minutos, não trinta. Subir o AppHost leva ~90s e a suíte inteira roda dentro do
    // lock, mas a espera antiga era mais longa que a paciência de quem olha o CI: um passo
    // parado por meia hora é cancelado à mão antes de chegar à mensagem, e aí a tolerância
    // generosa não protegeu nada — só trocou uma falha explicada por silêncio.
    private static readonly TimeSpan EsperaPadrao = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IntervaloTentativa = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IntervaloAviso = TimeSpan.FromSeconds(20);

    private readonly FileStream _arquivo;
    private readonly string _caminhoDono;

    private AppHostExclusiveLock(FileStream arquivo, string caminhoDono)
    {
        _arquivo = arquivo;
        _caminhoDono = caminhoDono;
    }

    /// <summary>
    /// Espera até ser o único processo com o AppHost desta solução no ar, avisando no console
    /// a cada 20s enquanto espera. O aviso não é enfeite: uma espera calada é indistinguível
    /// de um travamento para quem lê o log do CI, e foi assim que um passo parado virou meia
    /// hora de log em branco.
    /// </summary>
    public static async Task<AppHostExclusiveLock> AcquireAsync(
        TimeSpan? espera = null, CancellationToken ct = default)
    {
        var caminho = Path.Combine(
            Path.GetTempPath(), "cosmospro-mldemandforecast-apphost.lock");
        var caminhoDono = caminho + ".owner";
        var inicio = DateTimeOffset.UtcNow;
        var limite = inicio + (espera ?? EsperaPadrao);
        var proximoAviso = inicio + IntervaloAviso;

        while (true)
        {
            try
            {
                var arquivo = new FileStream(
                    caminho, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                var identificacao = $"pid={Environment.ProcessId} desde={DateTimeOffset.UtcNow:O}";
                await arquivo.WriteAsync(System.Text.Encoding.UTF8.GetBytes(identificacao + "\n"), ct);
                await arquivo.FlushAsync(ct);

                // O mesmo dado num arquivo à parte, porque o lock em si é aberto com
                // FileShare.None: enquanto alguém o detém, ninguém consegue lê-lo — nem para
                // descobrir quem é o dono. O sidecar é escrito com compartilhamento normal,
                // então quem espera consegue nomear quem está segurando, que é a informação
                // que faltava. Best-effort: falhar em escrevê-lo não pode custar o lock.
                try
                {
                    await File.WriteAllTextAsync(caminhoDono, identificacao + "\n", ct);
                }
                catch (IOException)
                {
                }

                return new AppHostExclusiveLock(arquivo, caminhoDono);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < limite)
            {
                if (DateTimeOffset.UtcNow >= proximoAviso)
                {
                    Console.WriteLine(
                        $"[AppHostExclusiveLock] esperando o AppHost de teste há " +
                        $"{(DateTimeOffset.UtcNow - inicio).TotalSeconds:F0}s. Dono: {LerDono(caminhoDono)}");
                    proximoAviso = DateTimeOffset.UtcNow + IntervaloAviso;
                }

                await Task.Delay(IntervaloTentativa, ct);
            }
            catch (IOException ex)
            {
                throw new TimeoutException(
                    $"Outro processo manteve o AppHost de teste por mais de " +
                    $"{(espera ?? EsperaPadrao).TotalMinutes:F0} min (lock em '{caminho}'). " +
                    $"Dono: {LerDono(caminhoDono)}. " +
                    "Verifique se sobrou um `dotnet test` ou um AppHost em debug rodando.",
                    ex);
            }
        }
    }

    private static string LerDono(string caminhoDono)
    {
        try
        {
            return File.Exists(caminhoDono)
                ? File.ReadAllText(caminhoDono).Trim()
                : "desconhecido (sidecar ausente)";
        }
        catch (IOException)
        {
            return "desconhecido (sidecar ilegível)";
        }
    }

    public ValueTask DisposeAsync()
    {
        _arquivo.Dispose();

        // O sidecar não é o lock — se ficar para trás, o próximo a esperar leria um dono que
        // já morreu. Apagar aqui mantém "sidecar ausente" significando "ninguém segurando".
        try
        {
            File.Delete(_caminhoDono);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
