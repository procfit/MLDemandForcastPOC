using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace CosmosPro.ML.DemandForCast.Migrator;

/// <summary>
/// Quantas vezes a espera pelo SQL Server tenta abrir conexão e quanto aguarda entre as
/// tentativas. A decisão de desistir fica aqui, separada do I/O, porque é a única regra
/// desta espera que se pode errar em silêncio: um limite baixo demais desiste antes de o
/// banco abrir e derruba o deploy sem motivo; um limite ausente trancaria a subida da
/// aplicação para sempre em vez de falhar e devolver o controle ao operador.
/// </summary>
internal sealed record RetryPolicy(int MaxAttempts, TimeSpan Delay)
{
    /// <summary>
    /// 30 tentativas de 5 segundos: um teto de ~2,5 minutos. Um SQL Server frio leva de
    /// 30 a 60 segundos para aceitar login, então a margem cobre um host lento com folga
    /// e ainda cabe dentro da janela de um deploy — o objetivo é falhar com mensagem, não
    /// esperar indefinidamente.
    /// </summary>
    public static readonly RetryPolicy Default = new(30, TimeSpan.FromSeconds(5));

    public bool ShouldGiveUp(int attemptsMade) => attemptsMade >= MaxAttempts;
}

/// <summary>
/// Espera o SQL Server aceitar conexão antes de o migrador tentar publicar schema.
/// O <c>healthcheck</c> do compose já cobre a primeira subida; esta espera cobre o resto:
/// um SQL Server reiniciado depois, um host que degradou, e o caso em que o banco
/// realmente não vai responder — aí ela existe para dizer isso no log em vez de deixar o
/// DacFx falhar com um erro deslocado.
/// </summary>
internal sealed class SqlServerReadiness(ILogger<SqlServerReadiness> logger)
{
    public async Task WaitAsync(string connectionString, RetryPolicy policy, CancellationToken ct = default)
    {
        // `master` porque a pergunta é sobre o servidor, não sobre o banco alvo: no
        // primeiro `docker compose up` o `Stage` ainda não existe — quem o cria é o
        // deploy do DACPAC, logo depois desta espera.
        var alvo = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };

        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                await using var conexao = new SqlConnection(alvo.ConnectionString);
                await conexao.OpenAsync(ct);

                logger.LogInformation(
                    "SQL Server em {Servidor} aceitou conexão na tentativa {Tentativa} de {Limite}.",
                    alvo.DataSource, tentativa, policy.MaxAttempts);
                return;
            }
            catch (SqlException ex) when (policy.ShouldGiveUp(tentativa))
            {
                throw new InvalidOperationException(
                    $"SQL Server em '{alvo.DataSource}' não aceitou conexão em {tentativa} tentativas " +
                    $"ao longo de {(tentativa - 1) * policy.Delay.TotalSeconds:F0}s. O migrador não publica " +
                    "schema num servidor inalcançável: confira se o serviço do banco subiu e se a senha do " +
                    "ambiente é a que ele espera.",
                    ex);
            }
            catch (SqlException ex)
            {
                logger.LogWarning(
                    "SQL Server em {Servidor} ainda não aceita conexão (tentativa {Tentativa} de {Limite}): {Erro}. Nova tentativa em {Espera}s.",
                    alvo.DataSource, tentativa, policy.MaxAttempts, ex.Message, policy.Delay.TotalSeconds);
                await Task.Delay(policy.Delay, ct);
            }
        }
    }
}
