namespace CosmosPro.ML.DemandForCast.Migrator.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void NaoDesisteEnquantoAindaHaTentativaNoOrcamento()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(1));

        policy.ShouldGiveUp(1).Should().BeFalse();
        policy.ShouldGiveUp(2).Should().BeFalse();
    }

    [Fact]
    public void DesisteExatamenteNaUltimaTentativaDoOrcamento()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(1));

        // Na terceira tentativa de um orçamento de três não existe "nova tentativa" a
        // anunciar: desistir aqui é o que transforma a falha em mensagem final. Desistir
        // em 4 gastaria uma tentativa além do limite; em 2, uma a menos que o declarado.
        policy.ShouldGiveUp(3).Should().BeTrue();
        policy.ShouldGiveUp(4).Should().BeTrue();
    }

    [Fact]
    public void OrcamentoPadraoCobreOStartFrioDoSqlServer()
    {
        // O start frio observado no VPS é de 30 a 60 segundos. O padrão precisa esperar
        // mais do que isso com folga — e ainda ter fim.
        var total = (RetryPolicy.Default.MaxAttempts - 1) * RetryPolicy.Default.Delay;

        total.Should().BeGreaterThan(TimeSpan.FromSeconds(60));
        total.Should().BeLessThan(TimeSpan.FromMinutes(5));
    }
}
