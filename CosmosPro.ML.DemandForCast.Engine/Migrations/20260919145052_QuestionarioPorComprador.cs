using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <summary>
    /// O questionário deixa de pertencer à execução e passa a pertencer ao comprador.
    ///
    /// <para>
    /// <b>Esta migration APAGA as respostas existentes, de propósito.</b> O índice único por
    /// usuário não pode nascer sobre linhas chaveadas por sessão: o mesmo comprador tem uma
    /// linha por execução, e todas colidiriam. A destruição está autorizada — reset dos dados
    /// da POC, 19/09/2026 — e o dado é de teste.
    /// </para>
    ///
    /// <para>
    /// <b>Por que o DELETE mora aqui e não só no script operacional.</b> O <c>db-migrator</c>
    /// roda no deploy e encerra com código != 0 se qualquer etapa falhar, e nesse caso
    /// <b>nenhum serviço sobe</b> (ver CLAUDE.md §4). Sem este DELETE, um deploy que chegasse
    /// antes do reset derrubaria a aplicação inteira por causa de uma constraint. Com ele, a
    /// ordem entre reset e deploy deixa de importar.
    /// </para>
    /// </summary>
    public partial class QuestionarioPorComprador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ver a nota da classe: sem isto o índice único abaixo não pode ser criado.
            // As respostas apagadas são dado de teste da POC, e a remoção foi autorizada.
            migrationBuilder.Sql("DELETE FROM dbo.QuestionarioRespostas;");
            migrationBuilder.Sql("DELETE FROM dbo.Questionarios;");

            migrationBuilder.DropForeignKey(
                name: "FK_Questionarios_ComparacaoSessoes_SessaoId",
                table: "Questionarios");

            migrationBuilder.DropIndex(
                name: "IX_Questionarios_UsuarioId",
                table: "Questionarios");

            migrationBuilder.DropIndex(
                name: "UQ_Questionarios_SessaoId",
                table: "Questionarios");

            migrationBuilder.DropColumn(
                name: "ItensComDecisaoMl",
                table: "Questionarios");

            migrationBuilder.DropColumn(
                name: "SessaoId",
                table: "Questionarios");

            migrationBuilder.DropColumn(
                name: "TotalDeItens",
                table: "Questionarios");

            migrationBuilder.CreateIndex(
                name: "UQ_Questionarios_UsuarioId",
                table: "Questionarios",
                column: "UsuarioId",
                unique: true);
        }

        /// <summary>
        /// Recria a forma antiga da tabela, e <b>não</b> o dado: as respostas apagadas no
        /// <c>Up</c> não voltam, e não há como reatribuí-las a execuções. O
        /// <c>defaultValue</c> de <c>SessaoId</c> é o Guid vazio — se houvesse linha, ela
        /// apontaria para sessão nenhuma; como não há, a coluna nasce vazia.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_Questionarios_UsuarioId",
                table: "Questionarios");

            migrationBuilder.AddColumn<int>(
                name: "ItensComDecisaoMl",
                table: "Questionarios",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessaoId",
                table: "Questionarios",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "TotalDeItens",
                table: "Questionarios",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Questionarios_UsuarioId",
                table: "Questionarios",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "UQ_Questionarios_SessaoId",
                table: "Questionarios",
                column: "SessaoId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Questionarios_ComparacaoSessoes_SessaoId",
                table: "Questionarios",
                column: "SessaoId",
                principalTable: "ComparacaoSessoes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
