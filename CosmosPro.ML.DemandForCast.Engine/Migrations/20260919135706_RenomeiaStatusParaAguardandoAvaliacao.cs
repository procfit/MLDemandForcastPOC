using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CosmosPro.ML.DemandForCast.Engine.Migrations
{
    /// <summary>
    /// Renomeia o valor gravado de <c>AguardandoQuestionario</c> para
    /// <c>AguardandoAvaliacao</c>, e restaura a equivalência de que
    /// <c>ComparacaoSessao.PodeExcluir</c> depende.
    ///
    /// <para>
    /// <b>Sem DDL, e ainda assim indispensável.</b> O <c>Status</c> viaja como texto
    /// (<c>HasConversion&lt;string&gt;</c>), então uma linha gravada com o nome antigo deixa
    /// de ser legível assim que o membro do enum muda — o EF estoura ao materializar, e a
    /// sessão some da tela sem mensagem que explique.
    /// </para>
    /// </summary>
    public partial class RenomeiaStatusParaAguardandoAvaliacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE dbo.ComparacaoSessoes
                   SET Status = 'AguardandoAvaliacao'
                 WHERE Status = 'AguardandoQuestionario';");

            // A SEGUNDA METADE, e é a que não é óbvia. `PodeExcluir` recusa excluir sessão
            // Concluida "para proteger o dado", e essa recusa só faz sentido se concluída
            // significar que o comprador de fato avaliou. Até aqui concluída significava
            // "respondeu o questionário"; a partir de agora significa "registrou a Seção G",
            // e as duas não coincidem: uma sessão fechada pelo envio do questionário pode
            // nunca ter tido veredito.
            //
            // Sem este UPDATE elas ficariam inexcluíveis para sempre sem nunca ter sido
            // avaliadas — exatamente o que a migration AddQuestionarios evitou quando a
            // equivalência foi criada, agora aplicado na direção oposta.
            migrationBuilder.Sql(@"
                UPDATE dbo.ComparacaoSessoes
                   SET Status = 'AguardandoAvaliacao'
                 WHERE Status = 'Concluida'
                   AND AvaliacaoVeredito IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Só o rename volta. A reclassificação de Concluida não é desfeita: não há como
            // saber quais sessões estavam concluídas por questionário antes do Up, e chutar
            // marcaria como avaliada uma execução que ninguém avaliou.
            migrationBuilder.Sql(@"
                UPDATE dbo.ComparacaoSessoes
                   SET Status = 'AguardandoQuestionario'
                 WHERE Status = 'AguardandoAvaliacao';");
        }
    }
}
