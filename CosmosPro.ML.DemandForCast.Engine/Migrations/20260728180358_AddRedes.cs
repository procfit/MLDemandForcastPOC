using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddRedes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RedeId",
                table: "TreinoJobs",
                type: "int",
                nullable: false,
                // 1 = rede demo, semeada mais abaixo antes das FKs. Backfill de
                // linhas pré-existentes; 0 quebraria a FK (não existe rede 0).
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RedeId",
                table: "SimulacoesCompra",
                type: "int",
                nullable: false,
                // 1 = rede demo, semeada mais abaixo antes das FKs. Backfill de
                // linhas pré-existentes; 0 quebraria a FK (não existe rede 0).
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RedeId",
                table: "CargasStage",
                type: "int",
                nullable: false,
                // 1 = rede demo, semeada mais abaixo antes das FKs. Backfill de
                // linhas pré-existentes; 0 quebraria a FK (não existe rede 0).
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Redes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Nome = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CnpjRaiz = table.Column<string>(type: "nvarchar(14)", maxLength: 14, nullable: true),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Redes", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Redes",
                columns: new[] { "Id", "Ativo", "CnpjRaiz", "CriadoEm", "Nome", "Slug" },
                values: new object[] { 1, true, null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Rede Demo", "demo" });

            migrationBuilder.CreateIndex(
                name: "IX_TreinoJobs_Rede_DataAgendamento",
                table: "TreinoJobs",
                columns: new[] { "RedeId", "DataAgendamento" });

            migrationBuilder.CreateIndex(
                name: "IX_SimulacoesCompra_Rede_DataAgendamento",
                table: "SimulacoesCompra",
                columns: new[] { "RedeId", "DataAgendamento" });

            migrationBuilder.CreateIndex(
                name: "IX_CargasStage_Rede_DataAgendamento",
                table: "CargasStage",
                columns: new[] { "RedeId", "DataAgendamento" });

            migrationBuilder.CreateIndex(
                name: "UQ_Redes_Slug",
                table: "Redes",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CargasStage_Redes_RedeId",
                table: "CargasStage",
                column: "RedeId",
                principalTable: "Redes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SimulacoesCompra_Redes_RedeId",
                table: "SimulacoesCompra",
                column: "RedeId",
                principalTable: "Redes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TreinoJobs_Redes_RedeId",
                table: "TreinoJobs",
                column: "RedeId",
                principalTable: "Redes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CargasStage_Redes_RedeId",
                table: "CargasStage");

            migrationBuilder.DropForeignKey(
                name: "FK_SimulacoesCompra_Redes_RedeId",
                table: "SimulacoesCompra");

            migrationBuilder.DropForeignKey(
                name: "FK_TreinoJobs_Redes_RedeId",
                table: "TreinoJobs");

            migrationBuilder.DropTable(
                name: "Redes");

            migrationBuilder.DropIndex(
                name: "IX_TreinoJobs_Rede_DataAgendamento",
                table: "TreinoJobs");

            migrationBuilder.DropIndex(
                name: "IX_SimulacoesCompra_Rede_DataAgendamento",
                table: "SimulacoesCompra");

            migrationBuilder.DropIndex(
                name: "IX_CargasStage_Rede_DataAgendamento",
                table: "CargasStage");

            migrationBuilder.DropColumn(
                name: "RedeId",
                table: "TreinoJobs");

            migrationBuilder.DropColumn(
                name: "RedeId",
                table: "SimulacoesCompra");

            migrationBuilder.DropColumn(
                name: "RedeId",
                table: "CargasStage");
        }
    }
}
