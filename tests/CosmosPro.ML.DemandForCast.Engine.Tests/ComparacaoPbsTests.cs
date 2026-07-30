using CosmosPro.ML.DemandForCast.Engine.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CosmosPro.ML.DemandForCast.Engine.Tests;

public sealed class ComparacaoPbsTests
{
    private static EngineDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<EngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public void Pendente_eh_o_unico_status_pelo_qual_o_Worker_faz_polling()
    {
        // O Worker consulta "Status = Pendente" (competing-consumers, mesmo padrão
        // de CargaStage/TreinoJob/SimulacaoCompra) — se um novo membro for
        // adicionado ao enum, ele não pode acidentalmente virar sinônimo de
        // Pendente nem repetir seu valor numérico, senão o polling pega jobs errados.
        var valores = Enum.GetValues<ComparacaoPbsStatus>();

        valores.Should().OnlyHaveUniqueItems();
        valores.Select(v => (int)v).Should().OnlyHaveUniqueItems();
        ComparacaoPbsStatus.Pendente.Should().Be((ComparacaoPbsStatus)0,
            "o valor persistido é o nome (HasConversion<string>()), mas o default do CLR ao instanciar a entity é 0 — Pendente precisa continuar sendo o primeiro membro");
    }

    [Fact]
    public void Status_eh_persistido_como_string_para_legibilidade_no_DB()
    {
        using var db = NewInMemoryContext();
        var property = db.Model
            .FindEntityType(typeof(ComparacaoPbs))!
            .FindProperty(nameof(ComparacaoPbs.Status))!;

        property.GetMaxLength().Should().Be(20);
        // O CLR type da entity é o enum, mas o provider type (o que vai pro DB)
        // deve ser string por causa do HasConversion<string>() — igual às demais
        // entidades de job.
        property.GetProviderClrType().Should().Be(typeof(string),
            "Status enum deve ser persistido como string no DB");
    }

    [Fact]
    public async Task Salvar_e_recuperar_um_ComparacaoPbs_com_status_concluido_funciona()
    {
        await using var db = NewInMemoryContext();

        var job = new ComparacaoPbs
        {
            Id = Guid.NewGuid(),
            RedeId = 1,
            Status = ComparacaoPbsStatus.Concluido,
            DataAgendamento = DateTimeOffset.UtcNow,
            TreinoJobId = Guid.NewGuid(),
            JanelaInicio = new DateOnly(2026, 1, 1),
            JanelaFim = new DateOnly(2026, 1, 31),
            TipoCalculo = 1,
        };

        db.ComparacoesPbs.Add(job);
        await db.SaveChangesAsync();

        var loaded = await db.ComparacoesPbs.SingleAsync(c => c.Id == job.Id);
        loaded.Status.Should().Be(ComparacaoPbsStatus.Concluido);
        loaded.TipoCalculo.Should().Be((byte)1);
        loaded.JanelaInicio.Should().Be(new DateOnly(2026, 1, 1));
    }

    [Fact]
    public void Indice_de_polling_nao_lidera_com_RedeId_e_indice_de_listagem_lidera()
    {
        using var db = NewInMemoryContext();
        var indexes = db.Model
            .FindEntityType(typeof(ComparacaoPbs))!
            .GetIndexes();

        indexes.Should().ContainSingle(i =>
            i.GetDatabaseName() == "IX_ComparacoesPbs_Status_DataAgendamento"
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "Status", "DataAgendamento" }));

        indexes.Should().ContainSingle(i =>
            i.GetDatabaseName() == "IX_ComparacoesPbs_Rede_DataAgendamento"
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "RedeId", "DataAgendamento" }));
    }
}
