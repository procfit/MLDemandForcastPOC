using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "ComparacaoSessoes",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            // Reclassifica as sessões que já estavam concluídas. Sob esta migration,
            // 'Concluida' passa a significar "o comprador respondeu o questionário", e essas
            // sessões nunca responderam — deixá-las como estão faria o painel afirmar avaliação
            // que não houve. Não é cosmético: ComparacaoSessao.PodeExcluir recusa excluir
            // 'Concluida' justamente porque a resposta selada é dado de pesquisa, então sem
            // este UPDATE as sessões antigas ficariam impossíveis de excluir para sempre, sem
            // nunca ter sido avaliadas. Precisa vir depois do AlterColumn acima: o valor novo
            // tem 22 caracteres e não cabe em nvarchar(20).
            migrationBuilder.Sql(
                "UPDATE ComparacaoSessoes SET Status = 'AguardandoQuestionario' WHERE Status = 'Concluida';");

            migrationBuilder.CreateTable(
                name: "Questionarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RedeId = table.Column<int>(type: "int", nullable: false),
                    SessaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersaoCatalogo = table.Column<int>(type: "int", nullable: false),
                    PassoAtual = table.Column<int>(type: "int", nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AtualizadoEm = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EnviadoEm = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ItensComDecisaoMl = table.Column<int>(type: "int", nullable: true),
                    TotalDeItens = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questionarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Questionarios_ComparacaoSessoes_SessaoId",
                        column: x => x.SessaoId,
                        principalTable: "ComparacaoSessoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Questionarios_Redes_RedeId",
                        column: x => x.RedeId,
                        principalTable: "Redes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuestionarioRespostas",
                columns: table => new
                {
                    QuestionarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PerguntaCodigo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PerguntaTexto = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OpcaoCodigo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OpcaoTexto = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    OpcaoValor = table.Column<int>(type: "int", nullable: true),
                    TextoLivre = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionarioRespostas", x => new { x.QuestionarioId, x.PerguntaCodigo });
                    table.ForeignKey(
                        name: "FK_QuestionarioRespostas_Questionarios_QuestionarioId",
                        column: x => x.QuestionarioId,
                        principalTable: "Questionarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Questionarios_Rede_EnviadoEm",
                table: "Questionarios",
                columns: new[] { "RedeId", "EnviadoEm" });

            migrationBuilder.CreateIndex(
                name: "IX_Questionarios_UsuarioId",
                table: "Questionarios",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "UQ_Questionarios_SessaoId",
                table: "Questionarios",
                column: "SessaoId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuestionarioRespostas");

            migrationBuilder.DropTable(
                name: "Questionarios");

            // OBRIGATÓRIO antes do AlterColumn: 'AguardandoQuestionario' tem 22 caracteres e o
            // ALTER para nvarchar(20) falharia com truncamento em qualquer banco que tenha uma
            // sessão nesse estado. O mapeamento de volta é para 'Concluida' porque é o estado
            // que o esquema anterior usava depois de comparar — e assume a perda: uma sessão
            // que ainda não havia sido avaliada volta indistinguível de uma avaliada. Down é
            // escape de desenvolvimento; a alternativa era um Down que não roda.
            migrationBuilder.Sql(
                "UPDATE ComparacaoSessoes SET Status = 'Concluida' WHERE Status = 'AguardandoQuestionario';");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "ComparacaoSessoes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30);
        }
    }
}
