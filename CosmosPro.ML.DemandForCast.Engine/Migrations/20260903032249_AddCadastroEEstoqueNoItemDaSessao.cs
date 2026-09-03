using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddCadastroEEstoqueNoItemDaSessao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Ean",
                table: "ComparacaoSessaoItens",
                type: "nvarchar(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EstoqueNaSugestao",
                table: "ComparacaoSessaoItens",
                type: "decimal(15,3)",
                precision: 15,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EstoqueNoFimDoPeriodo",
                table: "ComparacaoSessaoItens",
                type: "decimal(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fabricante",
                table: "ComparacaoSessaoItens",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Ean",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "EstoqueNaSugestao",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "EstoqueNoFimDoPeriodo",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "Fabricante",
                table: "ComparacaoSessaoItens");
        }
    }
}
