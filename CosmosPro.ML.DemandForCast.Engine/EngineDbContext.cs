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
    public DbSet<ComparacaoSessao> ComparacaoSessoes => Set<ComparacaoSessao>();
    public DbSet<ComparacaoSessaoItem> ComparacaoSessaoItens => Set<ComparacaoSessaoItem>();
    public DbSet<ComparacaoPbs> ComparacoesPbs => Set<ComparacaoPbs>();

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

        modelBuilder.Entity<ComparacaoSessao>(b =>
        {
            b.ToTable("ComparacaoSessoes");
            b.HasKey(x => x.Id);

            b.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();

            b.Property(x => x.RedeId).IsRequired();
            b.Property(x => x.CriadoEm).IsRequired();
            b.Property(x => x.AtualizadoEm).IsRequired();
            b.Property(x => x.MensagemErro).HasMaxLength(2000);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            // Mesmo padrão de polling das cargas e treinos — cross-rede, sem RedeId no índice.
            b.HasIndex(x => new { x.Status, x.AtualizadoEm })
             .HasDatabaseName("IX_ComparacaoSessoes_Status_AtualizadoEm");
            b.HasIndex(x => new { x.RedeId, x.CriadoEm })
             .HasDatabaseName("IX_ComparacaoSessoes_Rede_CriadoEm");
        });

        modelBuilder.Entity<ComparacaoSessaoItem>(b =>
        {
            b.ToTable("ComparacaoSessaoItens");
            b.HasKey(x => new { x.SessaoId, x.LojaId, x.Sku });

            // Mesmo NVARCHAR(30) do Sku no Stage (código de ERP, colide entre redes).
            b.Property(x => x.Sku).IsRequired().HasMaxLength(30);
            // Espelham Produtos.Nome NVARCHAR(200) e SugestoesCompraItens.Curva CHAR(1) no Stage.
            b.Property(x => x.NomeProduto).HasMaxLength(200);
            b.Property(x => x.Curva).HasMaxLength(1);

            // Precisão espelha o Stage (Tables/SugestoesCompraItens.sql): unidades em
            // DECIMAL(15,3), taxas de demanda/dia em DECIMAL(12,4), valor em DECIMAL(14,4).
            // Sem isso o EF usa decimal(18,2) por padrão e trunca silenciosamente.
            b.Property(x => x.CompraSugeridaPbs).HasPrecision(15, 3);
            b.Property(x => x.CompraSugeridaMl).HasPrecision(15, 3);
            b.Property(x => x.VendidoNaJanela).HasPrecision(15, 3);
            b.Property(x => x.DemandaDiaPbs).HasPrecision(12, 4);
            b.Property(x => x.DemandaDiaMl).HasPrecision(12, 4);
            b.Property(x => x.DemandaDiaReal).HasPrecision(12, 4);
            b.Property(x => x.SobraPbsUnidades).HasPrecision(15, 3);
            b.Property(x => x.SobraMlUnidades).HasPrecision(15, 3);
            b.Property(x => x.SobraPbsValor).HasPrecision(14, 4);
            b.Property(x => x.SobraMlValor).HasPrecision(14, 4);

            // Cascade: apagar a sessão apaga o detalhe — diferente das FKs Restrict
            // dos jobs, que preservam histórico mesmo com o pai removido.
            b.HasOne<ComparacaoSessao>().WithMany().HasForeignKey(x => x.SessaoId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ComparacaoPbs>(b =>
        {
            b.ToTable("ComparacoesPbs", t =>
                t.HasCheckConstraint("CK_ComparacoesPbs_TipoCalculo", "[TipoCalculo] IN (1, 2)"));
            b.HasKey(x => x.Id);

            b.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();

            b.Property(x => x.DataAgendamento).IsRequired();
            b.Property(x => x.RedeId).IsRequired();
            b.Property(x => x.TreinoJobId).IsRequired();
            b.Property(x => x.TipoCalculo).IsRequired();
            b.Property(x => x.MensagemErro).HasMaxLength(2000);
            // ResultadoJson é potencialmente grande (métricas por hierarquia) → nvarchar(max).
            b.Property(x => x.ResultadoJson);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            // FK lógica (sem cascata) para preservar histórico mesmo se o treino for removido.
            b.HasIndex(x => x.TreinoJobId).HasDatabaseName("IX_ComparacoesPbs_TreinoJobId");
            // Mesmo padrão de polling das demais filas — cross-rede, sem RedeId no
            // índice de polling (um Worker serve todos os inquilinos e pega a
            // próxima Pendente de qualquer rede).
            b.HasIndex(x => new { x.Status, x.DataAgendamento })
             .HasDatabaseName("IX_ComparacoesPbs_Status_DataAgendamento");
            b.HasIndex(x => new { x.RedeId, x.DataAgendamento })
             .HasDatabaseName("IX_ComparacoesPbs_Rede_DataAgendamento");
        });
    }
}
