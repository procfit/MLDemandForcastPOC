using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddValorDeMercadoNoItemDaSessao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MercadoValorConcorrentes",
                table: "ComparacaoSessaoItens",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MercadoValorRede",
                table: "ComparacaoSessaoItens",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MercadoValorConcorrentes",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoValorRede",
                table: "ComparacaoSessaoItens");
        }
    }
}
