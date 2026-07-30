using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddComparacoesPbs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComparacoesPbs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RedeId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DataAgendamento = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DataInicioProcessamento = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DataConclusao = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TreinoJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JanelaInicio = table.Column<DateOnly>(type: "date", nullable: false),
                    JanelaFim = table.Column<DateOnly>(type: "date", nullable: false),
                    TipoCalculo = table.Column<byte>(type: "tinyint", nullable: false),
                    ResultadoJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MensagemErro = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComparacoesPbs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComparacoesPbs_Redes_RedeId",
                        column: x => x.RedeId,
                        principalTable: "Redes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComparacoesPbs_Rede_DataAgendamento",
                table: "ComparacoesPbs",
                columns: new[] { "RedeId", "DataAgendamento" });

            migrationBuilder.CreateIndex(
                name: "IX_ComparacoesPbs_Status_DataAgendamento",
                table: "ComparacoesPbs",
                columns: new[] { "Status", "DataAgendamento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComparacoesPbs");
        }
    }
}
