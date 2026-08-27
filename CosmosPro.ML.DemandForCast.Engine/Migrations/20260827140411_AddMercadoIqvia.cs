using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddMercadoIqvia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MercadoBrickPdvs",
                columns: table => new
                {
                    RedeId = table.Column<int>(type: "int", nullable: false),
                    Brick = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Cnpj = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    Bandeira = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MercadoBrickPdvs", x => new { x.RedeId, x.Brick, x.Cnpj });
                    table.ForeignKey(
                        name: "FK_MercadoBrickPdvs_Redes_RedeId",
                        column: x => x.RedeId,
                        principalTable: "Redes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MercadoCargas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RedeId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DataAgendamento = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DataInicioProcessamento = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DataConclusao = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NomeArquivoOriginal = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    BlobKey = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    MensagemErro = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LinhasImportadas = table.Column<long>(type: "bigint", nullable: true),
                    UsuarioId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResumoJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MercadoCargas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MercadoCargas_Redes_RedeId",
                        column: x => x.RedeId,
                        principalTable: "Redes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MercadoObservacoes",
                columns: table => new
                {
                    RedeId = table.Column<int>(type: "int", nullable: false),
                    Mes = table.Column<DateOnly>(type: "date", nullable: false),
                    Brick = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Bandeira = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Ean = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    Unidades = table.Column<decimal>(type: "decimal(15,3)", precision: 15, scale: 3, nullable: false),
                    ValorCpp = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MercadoObservacoes", x => new { x.RedeId, x.Mes, x.Brick, x.Bandeira, x.Ean });
                    table.ForeignKey(
                        name: "FK_MercadoObservacoes_Redes_RedeId",
                        column: x => x.RedeId,
                        principalTable: "Redes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MercadoProdutos",
                columns: table => new
                {
                    RedeId = table.Column<int>(type: "int", nullable: false),
                    Ean = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    DescricaoLonga = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Laboratorio = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Molecula = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AreaFarmacia = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Nec1 = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Forma3 = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Classe4 = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MercadoProdutos", x => new { x.RedeId, x.Ean });
                    table.ForeignKey(
                        name: "FK_MercadoProdutos_Redes_RedeId",
                        column: x => x.RedeId,
                        principalTable: "Redes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MercadoCargas_Rede_DataAgendamento",
                table: "MercadoCargas",
                columns: new[] { "RedeId", "DataAgendamento" });

            migrationBuilder.CreateIndex(
                name: "IX_MercadoCargas_Status_DataAgendamento",
                table: "MercadoCargas",
                columns: new[] { "Status", "DataAgendamento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MercadoBrickPdvs");

            migrationBuilder.DropTable(
                name: "MercadoCargas");

            migrationBuilder.DropTable(
                name: "MercadoObservacoes");

            migrationBuilder.DropTable(
                name: "MercadoProdutos");
        }
    }
}
