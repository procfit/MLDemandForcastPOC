using System.ComponentModel;
using System.Globalization;
using FluentResults;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Etapa nomeada de uma operação. O nome e o arquivo <c>.sql</c> são a informação
/// que faltava quando a extração morria com uma mensagem de conversão de tipo sem
/// dizer onde.
/// </summary>
internal sealed record Etapa(string Nome, string? QueryFile)
{
    public override string ToString() => QueryFile is null ? Nome : $"{Nome} ({QueryFile})";
}

/// <summary>
/// O recorte de uma exceção que a classificação de fato usa. Existe porque
/// <see cref="SqlException"/> não tem construtor público: sem este intermediário a
/// classificação só seria exercitável com um SQL Server vivo.
/// </summary>
/// <param name="SqlSeveridade">
/// <c>SqlException.Class</c> — a severidade que o SQL Server atribuiu. É ela, e não o
/// número, que separa "o servidor processou e recusou" de "a conexão morreu": até <b>16</b> é
/// erro corrigível pelo usuário (objeto inexistente, sintaxe, permissão) e a conexão segue
/// intacta; de 20 em diante o servidor encerra a conexão. Nula quando a cadeia não traz
/// <c>SqlException</c> com <c>Class</c> utilizável.
/// </param>
internal sealed record FalhaBruta(
    Type Tipo,
    string Mensagem,
    int? SqlNumber,
    bool ConexaoJaAberta,
    string DetalheCompleto,
    byte? SqlSeveridade = null)
{
    public static FalhaBruta De(Exception ex, bool conexaoJaAberta)
    {
        var raiz = ex.InnerException ?? ex;

        // Toda falha de transporte real (host inalcançável, porta fechada, timeout de
        // comando, conexão derrubada no meio) chega com um Win32Exception/IOException
        // como inner do SqlException — então "raiz" (o inner, quando existe) quase
        // nunca É o SqlException. O número tem que vir de onde o driver realmente o
        // coloca: no próprio ex, ou no inner, tanto faz qual dos dois é SqlException.
        var sqlException = ex as SqlException ?? ex.InnerException as SqlException;
        var sqlNumber = sqlException?.Number;

        return new FalhaBruta(
            raiz.GetType(),
            raiz.Message,
            sqlNumber,
            conexaoJaAberta,
            ex.ToString(),
            sqlException?.Class);
    }
}

internal abstract class ExtratorErro : Error
{
    public const string ChaveEtapa = "etapa";
    public const string ChaveQuery = "query";
    public const string ChaveSqlNumber = "sqlNumber";
    public const string ChaveSqlSeveridade = "sqlSeveridade";
    public const string ChaveDuracao = "duracaoSegundos";
    public const string ChaveDetalhe = "detalhe";

    protected ExtratorErro(string mensagem) : base(mensagem)
    {
    }

    /// <summary>
    /// Se repetir a mesma leitura tem chance de dar outro resultado. Só isto é
    /// retentado (ver <see cref="Retentativa"/>); repetir credencial errada ou
    /// contrato divergente só faz o operador esperar três vezes pela mesma resposta.
    /// </summary>
    public virtual bool Transitorio => false;
}

internal sealed class ConexaoErro(string detalheDoServidor) : ExtratorErro(
    $"Não foi possível conectar ao SQL Server. Confira servidor, porta e banco — "
    + $"uma porta errada costuma responder e falhar no logon, sem dizer que o problema é a porta. "
    + $"Detalhe: {detalheDoServidor}");

internal sealed class ConexaoPerdidaErro(Etapa etapa, TimeSpan duracao) : ExtratorErro(
    $"A conexão caiu durante a etapa '{etapa}', depois de "
    + $"{duracao.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)}s. "
    + "O servidor e as credenciais estavam certos: a rede desistiu no meio da consulta.")
{
    public override bool Transitorio => true;
}

internal sealed class LogonTriggerErro() : ExtratorErro(
    "O servidor tem um logon trigger que recusou a conexão — normalmente por causa do "
    + "nome da aplicação. Ajuste 'ApplicationName' em extrator.config.json (vazio usa o "
    + "padrão do provider) ou use --app-name no modo linha de comando.");

internal sealed class TempoExcedidoErro(Etapa etapa, TimeSpan duracao) : ExtratorErro(
    $"A etapa '{etapa}' passou do tempo limite "
    + $"({duracao.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)}s). "
    + "Os limites ficam em extrator.config.json.");

internal sealed class ConcorrenciaErro(Etapa etapa) : ExtratorErro(
    $"A etapa '{etapa}' foi escolhida como vítima de deadlock pelo SQL Server.")
{
    public override bool Transitorio => true;
}

internal sealed class EtapaErro(Etapa etapa, string causa) : ExtratorErro(
    $"Falha na etapa '{etapa}': {causa}");

internal sealed class ContratoErro(string arquivo, string divergencia) : ExtratorErro(
    $"A query de '{arquivo}' não bate com o contrato do Stage: {divergencia}. "
    + "O ZIP não foi gravado — subir com coluna trocada embaralharia o dado no import.");

internal sealed class SugestaoNaoEncontradaErro(long sugestaoId) : ExtratorErro(
    $"Sugestão {sugestaoId} não existe no PBS, ou não tem método de cálculo declarado. "
    + "Confira o id com --list.");

internal sealed class SugestaoSemItensErro(long sugestaoId) : ExtratorErro(
    $"Sugestão {sugestaoId} não tem itens no PBS — nada para extrair.");

internal sealed class JanelaInviavelErro(string motivo) : ExtratorErro(motivo);

internal sealed class EscritaErro(string caminho, string causa) : ExtratorErro(
    $"Não foi possível escrever em '{caminho}': {causa}. "
    + "Confira espaço em disco, permissão na pasta e antivírus travando o arquivo.");

internal sealed class InesperadoErro(Type tipo, string mensagem) : ExtratorErro(
    $"Falha inesperada ({tipo.Name}): {mensagem}");

internal sealed class LojasNaoSelecionadasErro() : ExtratorErro(
    "Nenhuma loja selecionada — não há o que extrair. Escolha ao menos uma loja da sugestão.");

internal sealed class LojaForaDaSugestaoErro(IReadOnlyList<int> ids) : ExtratorErro(
    $"Esta sugestão não tem a(s) loja(s) {string.Join(", ", ids)}. "
    + "Só é possível extrair lojas que a própria sugestão cita.");

internal sealed class EmpresaDivergeDeFilialErro(int divergencias) : ExtratorErro(
    $"Esta instalação do PBS tem {divergencias} linha(s) desta sugestão em que EMPRESA é "
    + "diferente de FILIAL. O filtro de lojas usa FILIAL como identificador, então ele não "
    + "pode garantir que só as lojas escolhidas por você saíram no ZIP. Extraia sem escolher "
    + "lojas (todas as lojas da sugestão) se precisar do arquivo agora.");

internal static class ClassificadorDeFalha
{
    private const int LogonTriggerRecusou = 17892;
    private const int TimeoutDeComando = -2;
    private const int VitimaDeDeadlock = 1205;
    private const int LogonFalhou = 18456;
    private const int BancoInacessivel = 4060;

    /// <summary>
    /// Teto da faixa que o SQL Server documenta como "erros que o usuário pode corrigir":
    /// objeto inexistente, sintaxe, permissão, tipo incompatível. Nessa faixa o servidor
    /// respondeu e a conexão segue de pé. De 17 a 19 são problemas de recurso do servidor;
    /// de 20 em diante são fatais e a conexão é encerrada — e aí "a conexão caiu" é a
    /// leitura certa.
    /// </summary>
    private const byte MaiorSeveridadeCorrigivelPeloUsuario = 16;

    public static ExtratorErro Classificar(FalhaBruta falha, Etapa etapa, TimeSpan duracao)
    {
        var erro = Escolher(falha, etapa, duracao);

        erro.Metadata[ExtratorErro.ChaveEtapa] = etapa.Nome;
        erro.Metadata[ExtratorErro.ChaveDuracao] = Math.Round(duracao.TotalSeconds, 3);
        erro.Metadata[ExtratorErro.ChaveDetalhe] = falha.DetalheCompleto;
        if (etapa.QueryFile is { } query) erro.Metadata[ExtratorErro.ChaveQuery] = query;
        if (falha.SqlNumber is { } numero) erro.Metadata[ExtratorErro.ChaveSqlNumber] = numero;
        // A severidade acompanha o número porque é ela que distingue 207 de uma queda real.
        // Sem ela no log, o diagnóstico seguinte recomeça do zero.
        if (falha.SqlSeveridade is { } severidade) erro.Metadata[ExtratorErro.ChaveSqlSeveridade] = severidade;

        return erro;
    }

    private static ExtratorErro Escolher(FalhaBruta falha, Etapa etapa, TimeSpan duracao) =>
        falha.SqlNumber switch
        {
            LogonTriggerRecusou => new LogonTriggerErro(),
            TimeoutDeComando => new TempoExcedidoErro(etapa, duracao),
            VitimaDeDeadlock => new ConcorrenciaErro(etapa),
            LogonFalhou or BancoInacessivel => new ConexaoErro(falha.Mensagem),

            // O SERVIDOR PROCESSOU E RECUSOU. Vem antes do fallback de conexão perdida, e
            // essa ordem é a correção: o fallback tratava qualquer número não listado como
            // queda de rede desde que a conexão estivesse aberta -- que é justamente o estado
            // de um erro de consulta. Em 2026-09-01 o extrator morreu na Retiro com
            // "Invalid column name 'CGC'" (207, severidade 16) e o operador leu "a rede
            // desistiu no meio da consulta": foi conferir a rede em vez da query.
            not null when EhErroDeConsulta(falha) => ErroDeConsulta(falha, etapa),

            not null when falha.ConexaoJaAberta => new ConexaoPerdidaErro(etapa, duracao),
            not null => new ConexaoErro(falha.Mensagem),

            null => SemNumeroSql(falha, etapa),
        };

    /// <summary>
    /// Severidade até <see cref="MaiorSeveridadeCorrigivelPeloUsuario"/> significa que o SQL
    /// Server <b>respondeu</b>: ele processou o comando e o recusou. A conexão está intacta, e
    /// nada aqui é transitório — retentar um nome de coluna inválido chega à mesma recusa.
    ///
    /// <para>
    /// O discriminador é a severidade, e não uma lista de números, de propósito: uma lista
    /// cobriria o 207 e deixaria 208, 102, 229 e as centenas de outros caírem no mesmo balde
    /// errado. Sem severidade disponível o comportamento antigo vale — não dá para afirmar de
    /// que lado está a falha.
    /// </para>
    /// </summary>
    private static bool EhErroDeConsulta(FalhaBruta falha) =>
        falha.SqlSeveridade is { } severidade
        && severidade is > 0 and <= MaiorSeveridadeCorrigivelPeloUsuario;

    /// <summary>
    /// Mensagem do servidor <b>sem paráfrase</b>, mais a etapa e o arquivo da consulta. A
    /// mensagem do SQL Server para esta classe de erro já é precisa ("Invalid column name
    /// 'CGC'"); o que faltava era dizer <b>onde</b> — e não inventar uma causa de rede.
    /// </summary>
    private static ExtratorErro ErroDeConsulta(FalhaBruta falha, Etapa etapa) =>
        new EtapaErro(etapa,
            $"{falha.Mensagem} — o servidor respondeu e recusou a consulta; a conexão está "
            + "normal. Isto é defeito da consulta ou do schema desta instalação do PBS, "
            + "não da rede, e não adianta tentar de novo.");

    private static ExtratorErro SemNumeroSql(FalhaBruta falha, Etapa etapa)
    {
        if (falha.Tipo == typeof(InvalidCastException))
        {
            return new EtapaErro(etapa,
                $"{falha.Mensagem} — provavelmente uma coluna sem CONVERT na query. "
                + "Todo numérico do PBS é numeric(p,s) e chega como System.Decimal.");
        }

        // Segunda linha de defesa para quando a cadeia inteira não tem SqlException
        // nenhum: uma queda de transporte bruta o bastante (host inalcançável, porta
        // fechada) chega como Win32Exception puro, sem o driver ter chegado a montar
        // um SqlException em volta. Sem isto ela caía no balde de InesperadoErro.
        if (falha.Tipo == typeof(Win32Exception))
        {
            return new ConexaoErro(falha.Mensagem);
        }

        if (falha.Tipo == typeof(IOException)
            || falha.Tipo.IsSubclassOf(typeof(IOException))
            || falha.Tipo == typeof(UnauthorizedAccessException))
        {
            // O discriminador é a presença de QueryFile, não o nome da etapa: toda
            // etapa deste arquivo que carrega um QueryFile nasceu de uma consulta
            // (CatalogoService só lê), e "confira espaço em disco" é conselho errado
            // para uma leitura de rede que caiu. Só a etapa sem QueryFile (o ZIP em
            // ExtractionService.Run) é escrita de verdade.
            return etapa.QueryFile is null
                ? new EscritaErro(etapa.Nome, falha.Mensagem)
                : new EtapaErro(etapa, falha.Mensagem);
        }

        return new InesperadoErro(falha.Tipo, falha.Mensagem);
    }
}

internal static class ResultadoExtensoes
{
    /// <summary>
    /// Todo serviço deste projeto só falha com <see cref="ExtratorErro"/> -- mas
    /// <c>.First()</c> sobre isso é uma aposta: um IError de outra origem faria o
    /// LINQ estourar <see cref="InvalidOperationException"/> num handler
    /// <c>async void</c>, o que vira diálogo de exceção não tratada na cara do
    /// operador em vez da mensagem de erro normal.
    /// </summary>
    public static ExtratorErro ErroOuFallback<T>(this Result<T> resultado) =>
        resultado.Errors.OfType<ExtratorErro>().FirstOrDefault()
        ?? new InesperadoErro(typeof(Exception), resultado.Errors.FirstOrDefault()?.Message ?? "falha sem mensagem");
}
