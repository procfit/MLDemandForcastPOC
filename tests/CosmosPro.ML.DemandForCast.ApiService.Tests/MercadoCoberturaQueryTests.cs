using CosmosPro.ML.DemandForCast.ApiService.Mercado;
using CosmosPro.ML.DemandForCast.Engine;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.Tests;

/// <summary>
/// Prova que a consulta de cobertura é traduzível para SQL.
///
/// <para>
/// <b>Precisa do provider SqlServer, e não do InMemory usado nos testes de modelo.</b> O
/// InMemory avalia a consulta client-side, em LINQ-to-Objects, e aceita qualquer coisa —
/// foi por isso que a versão original (<c>GroupBy(...).Select(g =&gt; new Record(a, b))</c>)
/// passou por toda a suíte e só quebrou em produção, com a tela vazia sobre 175 mil linhas
/// no banco.
/// </para>
///
/// <para>
/// <c>ToQueryString()</c> compila a consulta e produz o SQL <b>sem abrir conexão</b>: a
/// connection string aponta para um servidor que não existe de propósito. Se a expressão
/// for intraduzível, o EF lança <c>InvalidOperationException</c> antes de qualquer I/O —
/// que é exatamente o que este teste captura, em milissegundos.
/// </para>
/// </summary>
public sealed class MercadoCoberturaQueryTests
{
    private static EngineDbContext NewSqlServerContext() =>
        new(new DbContextOptionsBuilder<EngineDbContext>()
            .UseSqlServer("Server=servidor-inexistente;Database=engine;Trusted_Connection=True;")
            .Options);

    [Fact]
    public void Consulta_de_cobertura_e_traduzivel_para_SQL()
    {
        using var db = NewSqlServerContext();

        var sql = MercadoEndpoints.CoberturaQuery(db, redeId: 1).ToQueryString();

        sql.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Agregacao_acontece_no_banco_e_nao_em_memoria()
    {
        // Sem isto o teste acima passaria com uma consulta que traz as 175 mil linhas
        // para o cliente e agrega em memória — traduzível, porém inaceitável.
        using var db = NewSqlServerContext();

        var sql = MercadoEndpoints.CoberturaQuery(db, redeId: 1).ToQueryString();

        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("COUNT(*)");
        sql.Should().Contain("SUM(");
        sql.Should().Contain("ORDER BY");
    }

    [Fact]
    public void Consulta_de_cobertura_filtra_pela_rede()
    {
        // O escopo por inquilino tem de estar no WHERE, não numa filtragem posterior.
        using var db = NewSqlServerContext();

        var sql = MercadoEndpoints.CoberturaQuery(db, redeId: 1).ToQueryString();

        sql.Should().Contain("WHERE");
        sql.Should().Contain("RedeId");
    }
}
