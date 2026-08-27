using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <summary>
    /// <c>TreinoJobs.MaxSkus</c> passa a aceitar nulo, que significa "sem teto de SKUs" — o
    /// novo default do treino.
    ///
    /// <para>
    /// <b>Sem backfill, de propósito.</b> Os jobs já gravados guardam o orçamento que de fato
    /// limitou cada um; zerá-los para nulo diria que treinaram o catálogo inteiro, e o número
    /// de SKUs é justamente o que explica por que aquelas comparações descartaram metade da
    /// sugestão. Nulo é para job novo.
    /// </para>
    /// </summary>
    public partial class MaxSkusSemTeto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MaxSkus",
                table: "TreinoJobs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MaxSkus",
                table: "TreinoJobs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
