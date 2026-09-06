using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddAvaliacaoDaExecucao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvaliacaoComentario",
                table: "ComparacaoSessoes",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvaliacaoEm",
                table: "ComparacaoSessoes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvaliacaoUsuarioId",
                table: "ComparacaoSessoes",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvaliacaoVeredito",
                table: "ComparacaoSessoes",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvaliacaoComentario",
                table: "ComparacaoSessoes");

            migrationBuilder.DropColumn(
                name: "AvaliacaoEm",
                table: "ComparacaoSessoes");

            migrationBuilder.DropColumn(
                name: "AvaliacaoUsuarioId",
                table: "ComparacaoSessoes");

            migrationBuilder.DropColumn(
                name: "AvaliacaoVeredito",
                table: "ComparacaoSessoes");
        }
    }
}
