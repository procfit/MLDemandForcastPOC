using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddComparacaoSessoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComparacaoSessoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RedeId = table.Column<int>(type: "int", nullable: false),
                    Nome = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AtualizadoEm = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SugestaoId = table.Column<long>(type: "bigint", nullable: true),
                    SugestaoDescricao = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SugestaoDataHora = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SugestaoTipoCalculo = table.Column<byte>(type: "tinyint", nullable: true),
                    CargaStageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TreinoJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComparacaoPbsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultadoJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MotivoInviabilidade = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MensagemErro = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComparacaoSessoes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComparacaoSessoes_Redes_RedeId",
                        column: x => x.RedeId,
                        principalTable: "Redes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComparacaoSessaoItens",
                columns: table => new
                {
                    SessaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LojaId = table.Column<int>(type: "int", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NomeProduto = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Curva = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: true),
                    CompraSugeridaPbs = table.Column<decimal>(type: "decimal(15,3)", precision: 15, scale: 3, nullable: false),
                    CompraSugeridaMl = table.Column<decimal>(type: "decimal(15,3)", precision: 15, scale: 3, nullable: false),
                    VendidoNaJanela = table.Column<decimal>(type: "decimal(15,3)", precision: 15, scale: 3, nullable: false),
                    DemandaDiaPbs = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    DemandaDiaMl = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    DemandaDiaReal = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    SobraPbsUnidades = table.Column<decimal>(type: "decimal(15,3)", precision: 15, scale: 3, nullable: false),
                    SobraMlUnidades = table.Column<decimal>(type: "decimal(15,3)", precision: 15, scale: 3, nullable: false),
                    SobraPbsValor = table.Column<decimal>(type: "decimal(14,4)", precision: 14, scale: 4, nullable: false),
                    SobraMlValor = table.Column<decimal>(type: "decimal(14,4)", precision: 14, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComparacaoSessaoItens", x => new { x.SessaoId, x.LojaId, x.Sku });
                    table.ForeignKey(
                        name: "FK_ComparacaoSessaoItens_ComparacaoSessoes_SessaoId",
                        column: x => x.SessaoId,
                        principalTable: "ComparacaoSessoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComparacaoSessoes_Rede_CriadoEm",
                table: "ComparacaoSessoes",
                columns: new[] { "RedeId", "CriadoEm" });

            migrationBuilder.CreateIndex(
                name: "IX_ComparacaoSessoes_Status_AtualizadoEm",
                table: "ComparacaoSessoes",
                columns: new[] { "Status", "AtualizadoEm" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComparacaoSessaoItens");

            migrationBuilder.DropTable(
                name: "ComparacaoSessoes");
        }
    }
}
