using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddPrecoVendaPraticadoNoItemDaSessao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PrecoVendaPraticado",
                table: "ComparacaoSessaoItens",
                type: "decimal(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrecoVendaPraticado",
                table: "ComparacaoSessaoItens");
        }
    }
}
