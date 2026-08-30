using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddMercadoNoItemDaSessao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MercadoAlerta",
                table: "ComparacaoSessaoItens",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoBrick",
                table: "ComparacaoSessaoItens",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MercadoDiasSemEstoque",
                table: "ComparacaoSessaoItens",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MercadoIndiceDesempenho",
                table: "ComparacaoSessaoItens",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "MercadoMes",
                table: "ComparacaoSessaoItens",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MercadoUnidadesConcorrentes",
                table: "ComparacaoSessaoItens",
                type: "decimal(15,3)",
                precision: 15,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MercadoUnidadesRede",
                table: "ComparacaoSessaoItens",
                type: "decimal(15,3)",
                precision: 15,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComparacaoSessaoItens_SessaoId_MercadoAlerta",
                table: "ComparacaoSessaoItens",
                columns: new[] { "SessaoId", "MercadoAlerta" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComparacaoSessaoItens_SessaoId_MercadoAlerta",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoAlerta",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoBrick",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoDiasSemEstoque",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoIndiceDesempenho",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoMes",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoUnidadesConcorrentes",
                table: "ComparacaoSessaoItens");

            migrationBuilder.DropColumn(
                name: "MercadoUnidadesRede",
                table: "ComparacaoSessaoItens");
        }
    }
}
