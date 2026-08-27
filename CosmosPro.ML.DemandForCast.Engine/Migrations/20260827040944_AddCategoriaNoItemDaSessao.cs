using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <summary>
    /// <c>ComparacaoSessaoItens.Categoria</c>, copiada de <c>Produtos.Categoria</c> na
    /// materialização — para a tela poder filtrar por categoria e para o comprador ver onde a
    /// diferença entre PBS e ML se concentra.
    ///
    /// <para>
    /// <b>Sem backfill, e não por preguiça:</b> a categoria vive em <c>Stage.Produtos</c>, que
    /// cada import substitui inteiro. Preencher retroativamente leria o cadastro do envio
    /// <b>atual</b> e o atribuiria a sessões que descrevem outro envio — dado plausível e
    /// errado, que é pior que nulo. Sessão antiga fica com categoria nula e a tela declara
    /// isso; sessão nova traz a categoria correta.
    /// </para>
    /// </summary>
    public partial class AddCategoriaNoItemDaSessao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Categoria",
                table: "ComparacaoSessaoItens",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Categoria",
                table: "ComparacaoSessaoItens");
        }
    }
}
