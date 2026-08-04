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
internal sealed record FalhaBruta(
    Type Tipo,
    string Mensagem,
    int? SqlNumber,
    bool ConexaoJaAberta,
    string DetalheCompleto)
{
    public static FalhaBruta De(Exception ex, bool conexaoJaAberta)
    {
        var raiz = ex.InnerException ?? ex;

        return new FalhaBruta(
            raiz.GetType(),
            raiz.Message,
            raiz is SqlException sql ? sql.Number : null,
            conexaoJaAberta,
            ex.ToString());
    }
}

internal abstract class ExtratorErro : Error
{
    public const string ChaveEtapa = "etapa";
    public const string ChaveQuery = "query";
    public const string ChaveSqlNumber = "sqlNumber";
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
    $"Falha inesperada ({tipo.Name}): {mensagem}. O detalhe completo está no arquivo de log.");

internal static class ClassificadorDeFalha
{
    private const int LogonTriggerRecusou = 17892;
    private const int TimeoutDeComando = -2;
    private const int VitimaDeDeadlock = 1205;
    private const int LogonFalhou = 18456;
    private const int BancoInacessivel = 4060;

    public static ExtratorErro Classificar(FalhaBruta falha, Etapa etapa, TimeSpan duracao)
    {
        var erro = Escolher(falha, etapa, duracao);

        erro.Metadata[ExtratorErro.ChaveEtapa] = etapa.Nome;
        erro.Metadata[ExtratorErro.ChaveDuracao] = Math.Round(duracao.TotalSeconds, 3);
        erro.Metadata[ExtratorErro.ChaveDetalhe] = falha.DetalheCompleto;
        if (etapa.QueryFile is { } query) erro.Metadata[ExtratorErro.ChaveQuery] = query;
        if (falha.SqlNumber is { } numero) erro.Metadata[ExtratorErro.ChaveSqlNumber] = numero;

        return erro;
    }

    private static ExtratorErro Escolher(FalhaBruta falha, Etapa etapa, TimeSpan duracao) =>
        falha.SqlNumber switch
        {
            LogonTriggerRecusou => new LogonTriggerErro(),
            TimeoutDeComando => new TempoExcedidoErro(etapa, duracao),
            VitimaDeDeadlock => new ConcorrenciaErro(etapa),
            LogonFalhou or BancoInacessivel => new ConexaoErro(falha.Mensagem),

            not null when falha.ConexaoJaAberta => new ConexaoPerdidaErro(etapa, duracao),
            not null => new ConexaoErro(falha.Mensagem),

            null => SemNumeroSql(falha, etapa),
        };

    private static ExtratorErro SemNumeroSql(FalhaBruta falha, Etapa etapa)
    {
        if (falha.Tipo == typeof(InvalidCastException))
        {
            return new EtapaErro(etapa,
                $"{falha.Mensagem} — provavelmente uma coluna sem CONVERT na query. "
                + "Todo numérico do PBS é numeric(p,s) e chega como System.Decimal.");
        }

        if (falha.Tipo == typeof(IOException)
            || falha.Tipo.IsSubclassOf(typeof(IOException))
            || falha.Tipo == typeof(UnauthorizedAccessException))
        {
            return new EscritaErro(etapa.Nome, falha.Mensagem);
        }

        return new InesperadoErro(falha.Tipo, falha.Mensagem);
    }
}
