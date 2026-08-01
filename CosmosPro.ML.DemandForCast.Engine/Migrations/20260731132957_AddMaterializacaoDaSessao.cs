using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterializacaoDaSessao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SkusSemCadastro",
                table: "ComparacaoSessoes",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "SobraMlValor",
                table: "ComparacaoSessaoItens",
                type: "decimal(14,4)",
                precision: 14,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(14,4)",
                oldPrecision: 14,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "SobraMlUnidades",
                table: "ComparacaoSessaoItens",
                type: "decimal(15,3)",
                precision: 15,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(15,3)",
                oldPrecision: 15,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "DemandaDiaReal",
                table: "ComparacaoSessaoItens",
                type: "decimal(12,4)",
                precision: 12,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(12,4)",
                oldPrecision: 12,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "DemandaDiaMl",
                table: "ComparacaoSessaoItens",
                type: "decimal(12,4)",
                precision: 12,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(12,4)",
                oldPrecision: 12,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "CompraSugeridaMl",
                table: "ComparacaoSessaoItens",
                type: "decimal(15,3)",
                precision: 15,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(15,3)",
                oldPrecision: 15,
                oldScale: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkusSemCadastro",
                table: "ComparacaoSessoes");

            migrationBuilder.AlterColumn<decimal>(
                name: "SobraMlValor",
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

            migrationBuilder.AlterColumn<decimal>(
                name: "SobraMlUnidades",
                table: "ComparacaoSessaoItens",
                type: "decimal(15,3)",
                precision: 15,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(15,3)",
                oldPrecision: 15,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "DemandaDiaReal",
                table: "ComparacaoSessaoItens",
                type: "decimal(12,4)",
                precision: 12,
                scale: 4,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(12,4)",
                oldPrecision: 12,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "DemandaDiaMl",
                table: "ComparacaoSessaoItens",
                type: "decimal(12,4)",
                precision: 12,
                scale: 4,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(12,4)",
                oldPrecision: 12,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CompraSugeridaMl",
                table: "ComparacaoSessaoItens",
                type: "decimal(15,3)",
                precision: 15,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(15,3)",
                oldPrecision: 15,
                oldScale: 3,
                oldNullable: true);
        }
    }
}
