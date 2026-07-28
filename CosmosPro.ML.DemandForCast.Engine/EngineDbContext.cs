using CosmosPro.ML.DemandForCast.Engine.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Engine;

public sealed class EngineDbContext(DbContextOptions<EngineDbContext> options)
    : IdentityDbContext<Usuario, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Rede> Redes => Set<Rede>();
    public DbSet<CargaStage> CargasStage => Set<CargaStage>();
    public DbSet<TreinoJob> TreinoJobs => Set<TreinoJob>();
    public DbSet<SimulacaoCompra> SimulacoesCompra => Set<SimulacaoCompra>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // OBRIGATÓRIO e fácil de esquecer: sem esta chamada as tabelas do Identity
        // ficam sem configuração e a migration sai incompleta.
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Usuario>(b =>
        {
            b.Property(x => x.NomeCompleto).IsRequired().HasMaxLength(160);

            // Restrict: rede com usuário vinculado não pode ser apagada. A UI
            // desativa em vez de excluir.
            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => x.RedeId).HasDatabaseName("IX_Usuarios_RedeId");
        });

        modelBuilder.Entity<Rede>(b =>
        {
            b.ToTable("Redes");
            b.HasKey(x => x.Id);

            b.Property(x => x.Nome).IsRequired().HasMaxLength(120);
            b.Property(x => x.Slug).IsRequired().HasMaxLength(40);
            b.Property(x => x.CnpjRaiz).HasMaxLength(14);

            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("UQ_Redes_Slug");

            // Rede demo: mantém dataset sintético, testes e E2E funcionando sem
            // precisar de UI de cadastro. CriadoEm é literal fixo de propósito —
            // DateTimeOffset.UtcNow em HasData gera migration nova a cada dotnet ef.
            b.HasData(new Rede
            {
                Id = 1,
                Nome = "Rede Demo",
                Slug = "demo",
                Ativo = true,
                CriadoEm = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        });

        modelBuilder.Entity<SimulacaoCompra>(b =>
        {
            b.ToTable("SimulacoesCompra");
            b.HasKey(x => x.Id);

            b.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();

            b.Property(x => x.DataAgendamento).IsRequired();
            b.Property(x => x.TreinoJobId).IsRequired();
            b.Property(x => x.RedeId).IsRequired();
            b.Property(x => x.MensagemErro).HasMaxLength(2000);
            b.Property(x => x.ResultadoJson);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            // FK lógica (sem cascata) para preservar histórico mesmo se o treino for removido.
            b.HasIndex(x => x.TreinoJobId).HasDatabaseName("IX_SimulacoesCompra_TreinoJobId");
            // Polling do Worker é cross-rede (pega a próxima Pendente de qualquer
            // inquilino), então RedeId NÃO entra neste índice — entra no de listagem.
            b.HasIndex(x => new { x.Status, x.DataAgendamento })
             .HasDatabaseName("IX_SimulacoesCompra_Status_DataAgendamento");
            b.HasIndex(x => new { x.RedeId, x.DataAgendamento })
             .HasDatabaseName("IX_SimulacoesCompra_Rede_DataAgendamento");
        });

        modelBuilder.Entity<TreinoJob>(b =>
        {
            b.ToTable("TreinoJobs");
            b.HasKey(x => x.Id);

            b.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();

            b.Property(x => x.DataAgendamento).IsRequired();
            b.Property(x => x.RedeId).IsRequired();
            b.Property(x => x.ModeloBlobKey).HasMaxLength(260);
            b.Property(x => x.MensagemErro).HasMaxLength(2000);
            // ResultadoJson é potencialmente grande (métricas por hierarquia) → nvarchar(max).
            b.Property(x => x.ResultadoJson);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            // Mesmo padrão de polling das cargas — cross-rede, sem RedeId no índice.
            b.HasIndex(x => new { x.Status, x.DataAgendamento })
             .HasDatabaseName("IX_TreinoJobs_Status_DataAgendamento");
            b.HasIndex(x => new { x.RedeId, x.DataAgendamento })
             .HasDatabaseName("IX_TreinoJobs_Rede_DataAgendamento");
        });

        modelBuilder.Entity<CargaStage>(b =>
        {
            b.ToTable("CargasStage");
            b.HasKey(x => x.Id);

            b.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();

            b.Property(x => x.DataAgendamento).IsRequired();
            b.Property(x => x.RedeId).IsRequired();
            b.Property(x => x.NomeArquivoOriginal).IsRequired().HasMaxLength(260);
            b.Property(x => x.BlobKey).IsRequired().HasMaxLength(260);
            b.Property(x => x.MensagemErro).HasMaxLength(2000);
            b.Property(x => x.UsuarioId).HasMaxLength(100);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            // Padrão de acesso típico do Worker: pegar próxima Pendente em ordem
            // cronológica de upload, usando WITH (UPDLOCK, READPAST) para
            // serialização (competing consumers em SQL Server puro).
            // Cross-rede de propósito — um Worker serve todos os inquilinos.
            b.HasIndex(x => new { x.Status, x.DataAgendamento })
             .HasDatabaseName("IX_CargasStage_Status_DataAgendamento");
            b.HasIndex(x => new { x.RedeId, x.DataAgendamento })
             .HasDatabaseName("IX_CargasStage_Rede_DataAgendamento");
        });
    }
}
