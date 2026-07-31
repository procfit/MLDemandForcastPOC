using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddQualificadoresDoItemDaSessao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "SobraPbsValor",
                table: "ComparacaoSessaoItens",
                type: "decimal(14,4)",
                precision: 14,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(14,4)",
                oldPrecision: 14,
                oldScale: 4);

            migrationBuilder.AddColumn<bool>(
                name: "JanelaAlemDoHistorico",
                table: "ComparacaoSessaoItens",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JanelaAlemDoHistorico",
                table: "ComparacaoSessaoItens");

            migrationBuilder.AlterColumn<decimal>(
                name: "SobraPbsValor",
                table: "ComparacaoSessaoItens",
                type: "decimal(14,4)",
                precision: 14,
                scale: 4,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(14,4)",
                oldPrecision: 14,
                oldScale: 4,
                oldNullable: true);
        }
    }
}
