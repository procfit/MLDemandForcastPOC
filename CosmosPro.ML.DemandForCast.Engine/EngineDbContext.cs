using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Engine.Mercado;
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
    public DbSet<Questionario> Questionarios => Set<Questionario>();
    public DbSet<QuestionarioResposta> QuestionarioRespostas => Set<QuestionarioResposta>();
    public DbSet<MercadoCarga> MercadoCargas => Set<MercadoCarga>();
    public DbSet<MercadoObservacao> MercadoObservacoes => Set<MercadoObservacao>();
    public DbSet<MercadoProduto> MercadoProdutos => Set<MercadoProduto>();
    public DbSet<MercadoBrickPdv> MercadoBrickPdvs => Set<MercadoBrickPdv>();

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

            // 30, e não os 20 das outras filas: "AguardandoQuestionario" tem 22 caracteres e
            // não caberia. O valor viaja como texto (HasConversion<string>), então um nome de
            // estado mais longo que a coluna estoura na escrita — a alternativa era contorcer
            // o nome do estado para caber num limite arbitrário.
            b.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(30)
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
            // Espelham Produtos.Nome NVARCHAR(200), Produtos.Categoria NVARCHAR(80) e
            // SugestoesCompraItens.Curva CHAR(1) no Stage.
            b.Property(x => x.NomeProduto).HasMaxLength(200);
            b.Property(x => x.Categoria).HasMaxLength(80);
            b.Property(x => x.Curva).HasMaxLength(1);

            // Precisão declarada porque o default do EF é decimal(18,2), que truncaria em
            // silêncio. Unidades e taxas espelham o Stage (Tables/SugestoesCompraItens.sql):
            // DECIMAL(15,3) e DECIMAL(12,4). Já o valor NÃO é espelho de coluna nenhuma — a
            // monetária do Stage é PrecoCompra DECIMAL(15,4), preço unitário, e aqui a coluna
            // guarda unidades × preço. Mantém as 4 casas do dinheiro do Stage com 10 dígitos
            // inteiros, ordens de grandeza acima de qualquer sobra de uma sugestão.
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

            // Sinal de mercado da IQVIA. As unidades espelham MercadoObservacao.Unidades
            // (15,3). O índice ganha (9,4): ele é uma razão, e o teto teórico é
            // 1 / fatia agregada da rede no brick -- fatia de 1% já daria 100, então 5
            // dígitos inteiros cobrem com folga.
            b.Property(x => x.MercadoUnidadesRede).HasPrecision(15, 3);
            b.Property(x => x.MercadoUnidadesConcorrentes).HasPrecision(15, 3);
            b.Property(x => x.MercadoIndiceDesempenho).HasPrecision(9, 4);
            // Espelha MercadoObservacao.Brick.
            b.Property(x => x.MercadoBrick).HasMaxLength(80);
            b.Property(x => x.MercadoAlerta).HasMaxLength(MercadoAlertas.TamanhoMaximo);

            // Índice para o filtro "só itens com alerta" não varrer a tabela inteira.
            // SessaoId primeiro porque a consulta sempre fixa a sessão antes de filtrar.
            b.HasIndex(x => new { x.SessaoId, x.MercadoAlerta });

            // Cascade: apagar a sessão apaga o detalhe. É a única FK real que sai da sessão
            // além da de Redes — os ponteiros para as três fases (CargaStageId, TreinoJobId,
            // ComparacaoPbsId) são FKs lógicas, sem constraint, no mesmo padrão de
            // SimulacoesCompra e ComparacoesPbs: o histórico da sessão sobrevive à remoção
            // do job que a produziu.
            b.HasOne<ComparacaoSessao>().WithMany().HasForeignKey(x => x.SessaoId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Questionario>(b =>
        {
            b.ToTable("Questionarios");
            b.HasKey(x => x.Id);

            b.Property(x => x.RedeId).IsRequired();
            b.Property(x => x.SessaoId).IsRequired();
            b.Property(x => x.UsuarioId).IsRequired();
            b.Property(x => x.VersaoCatalogo).IsRequired();
            b.Property(x => x.CriadoEm).IsRequired();
            b.Property(x => x.AtualizadoEm).IsRequired();

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            // Cascade: excluir a sessão leva o questionário. Só alcança rascunho — sessão
            // Concluida (questionário selado) recusa exclusão em ComparacaoSessao.PodeExcluir,
            // e o endpoint repete a condição no WHERE do DELETE.
            b.HasOne<ComparacaoSessao>().WithMany().HasForeignKey(x => x.SessaoId)
             .OnDelete(DeleteBehavior.Cascade);

            // Único: um questionário por sessão. É esta constraint — não a checagem do
            // endpoint — que impede dois envios concorrentes de criarem duas avaliações da
            // mesma comparação.
            b.HasIndex(x => x.SessaoId).IsUnique().HasDatabaseName("UQ_Questionarios_SessaoId");

            // FK lógica (índice sem constraint): a resposta é dado de pesquisa e sobrevive à
            // remoção do usuário que a deu — mesmo padrão de SimulacoesCompra.TreinoJobId.
            b.HasIndex(x => x.UsuarioId).HasDatabaseName("IX_Questionarios_UsuarioId");

            // Listagem e export do TCC: por rede, em ordem de envio. Sem índice de polling
            // porque nenhuma fila reclama questionário — a fase é de humano.
            b.HasIndex(x => new { x.RedeId, x.EnviadoEm })
             .HasDatabaseName("IX_Questionarios_Rede_EnviadoEm");
        });

        modelBuilder.Entity<QuestionarioResposta>(b =>
        {
            b.ToTable("QuestionarioRespostas");
            b.HasKey(x => new { x.QuestionarioId, x.PerguntaCodigo });

            b.Property(x => x.PerguntaCodigo).IsRequired().HasMaxLength(40);
            b.Property(x => x.PerguntaTexto).IsRequired().HasMaxLength(500);
            b.Property(x => x.OpcaoCodigo).IsRequired().HasMaxLength(40);
            b.Property(x => x.OpcaoTexto).IsRequired().HasMaxLength(300);
            b.Property(x => x.TextoLivre).HasMaxLength(1000);

            b.HasOne<Questionario>().WithMany().HasForeignKey(x => x.QuestionarioId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MercadoCarga>(b =>
        {
            b.ToTable("MercadoCargas");
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
            // ResumoJson: meses × bricks é pequeno, mas a lista de EANs descartados não tem teto.
            b.Property(x => x.ResumoJson);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);

            // Mesmo padrão de polling das demais filas — cross-rede, sem RedeId no índice.
            b.HasIndex(x => new { x.Status, x.DataAgendamento })
             .HasDatabaseName("IX_MercadoCargas_Status_DataAgendamento");
            b.HasIndex(x => new { x.RedeId, x.DataAgendamento })
             .HasDatabaseName("IX_MercadoCargas_Rede_DataAgendamento");
        });

        modelBuilder.Entity<MercadoObservacao>(b =>
        {
            b.ToTable("MercadoObservacoes");
            b.HasKey(x => new { x.RedeId, x.Mes, x.Brick, x.Bandeira, x.Ean });

            b.Property(x => x.Brick).HasMaxLength(80);
            b.Property(x => x.Bandeira).HasMaxLength(60);
            b.Property(x => x.Ean).HasMaxLength(14).IsUnicode(false);

            // Unidades espelha as quantidades do Stage (DECIMAL(15,3)). ValorCpp é valor
            // agregado do mês em R$ normalizados pela IQVIA — 2 casas bastam (o arquivo
            // traz artefatos de float tipo 113700.59999999999, arredondados no parse) e
            // 12 dígitos inteiros cobrem qualquer brick.
            b.Property(x => x.Unidades).HasPrecision(15, 3);
            b.Property(x => x.ValorCpp).HasPrecision(14, 2);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MercadoProduto>(b =>
        {
            b.ToTable("MercadoProdutos");
            b.HasKey(x => new { x.RedeId, x.Ean });

            b.Property(x => x.Ean).HasMaxLength(14).IsUnicode(false);
            b.Property(x => x.DescricaoLonga).IsRequired().HasMaxLength(300);
            b.Property(x => x.Laboratorio).HasMaxLength(120);
            // Combinações vêm concatenadas por '|' e não têm teto de componentes.
            b.Property(x => x.Molecula).HasMaxLength(500);
            b.Property(x => x.AreaFarmacia).HasMaxLength(40);
            b.Property(x => x.Nec1).HasMaxLength(80);
            b.Property(x => x.Forma3).HasMaxLength(80);
            b.Property(x => x.Classe4).HasMaxLength(80);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MercadoBrickPdv>(b =>
        {
            b.ToTable("MercadoBrickPdvs");
            b.HasKey(x => new { x.RedeId, x.Brick, x.Cnpj });

            b.Property(x => x.Brick).HasMaxLength(80);
            b.Property(x => x.Cnpj).HasMaxLength(14).IsUnicode(false);
            b.Property(x => x.Bandeira).IsRequired().HasMaxLength(60);

            b.HasOne<Rede>().WithMany().HasForeignKey(x => x.RedeId)
             .OnDelete(DeleteBehavior.Restrict);
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
