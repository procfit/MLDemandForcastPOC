using System.Net;
using System.Net.Http.Json;
using CosmosPro.ML.DemandForCast.Web.Services;

namespace CosmosPro.ML.DemandForCast.Web;

/// <summary>
/// O questionário do comprador sobre o protótipo. Como nos demais clients, <c>redeId</c> e
/// <c>usuarioId</c> vêm do <see cref="IRedeContext"/> — nunca de parâmetro de página.
///
/// <para>
/// <b>Não há id de sessão em rota nenhuma daqui.</b> O questionário deixou de pertencer à
/// execução em 19/09/2026: é um por comprador, sobre a experiência acumulada em pelo menos
/// duas execuções avaliadas.
/// </para>
/// </summary>
public class QuestionariosApiClient(HttpClient httpClient, IRedeContext redeContext)
{
    private static readonly TimeSpan LeituraTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// O instrumento. Estático e igual para todas as redes, então sem escopo de inquilino.
    /// </summary>
    public async Task<CatalogoView?> GetCatalogoAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);
        return await httpClient.GetFromJsonAsync<CatalogoView>("/api/questionarios/catalogo", cts.Token);
    }

    /// <summary>
    /// O questionário do comprador logado, com o estado do portão junto.
    ///
    /// <para>
    /// <b>Portão fechado não é ausência.</b> A resposta vem com <c>Liberado = false</c> e as
    /// contagens, porque a tela da Seção G explica quantas execuções faltam em vez de esconder
    /// o botão — botão sumido lê-se como defeito.
    /// </para>
    /// </summary>
    public async Task<QuestionarioView?> GetAsync(CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var usuarioId = await redeContext.GetUsuarioIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var resp = await httpClient.GetAsync(
            $"/api/questionario?redeId={redeId}&usuarioId={usuarioId}", cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<QuestionarioView>(cancellationToken: cts.Token);
    }

    /// <summary>
    /// A tabulação da rede, em dois blocos: as execuções com a Seção G, e os questionários por
    /// comprador. É a fonte da tela de tabulação e da planilha.
    /// </summary>
    public async Task<TabulacaoView> GetTabulacaoAsync(CancellationToken ct = default)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var resp = await httpClient.GetFromJsonAsync<TabulacaoView>(
            $"/api/comparacoes/avaliacoes?redeId={redeId}", cts.Token);

        return resp ?? new TabulacaoView(redeId, [], [], 0, [], []);
    }

    /// <summary>Grava o rascunho. Idempotente: manda o estado completo do wizard.</summary>
    public Task<QuestionarioResult> SalvarAsync(
        int passoAtual, IReadOnlyList<RespostaEnviada> respostas, CancellationToken ct = default)
        => EnviarCorpoAsync(passoAtual, respostas, selar: false, ct);

    /// <summary>
    /// Sela o questionário. Falha com 400 quando falta pergunta obrigatória, e com 409 quando
    /// o comprador ainda não avaliou execuções suficientes ou já respondeu antes.
    /// </summary>
    public Task<QuestionarioResult> EnviarAsync(
        int passoAtual, IReadOnlyList<RespostaEnviada> respostas, CancellationToken ct = default)
        => EnviarCorpoAsync(passoAtual, respostas, selar: true, ct);

    /// <summary>
    /// Rascunho e envio diferem só na rota; a tradução do erro é a mesma, e duplicá-la faria
    /// as duas pontas divergirem na mensagem que o comprador lê.
    /// </summary>
    private async Task<QuestionarioResult> EnviarCorpoAsync(
        int passoAtual, IReadOnlyList<RespostaEnviada> respostas, bool selar, CancellationToken ct)
    {
        var redeId = await redeContext.GetRedeIdAtualAsync();
        var usuarioId = await redeContext.GetUsuarioIdAtualAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LeituraTimeout);

        var escopo = $"redeId={redeId}&usuarioId={usuarioId}";
        var rota = selar
            ? $"/api/questionario/enviar?{escopo}"
            : $"/api/questionario?{escopo}";
        var corpo = new SalvarQuestionarioRequest(passoAtual, respostas);

        var resp = selar
            ? await httpClient.PostAsJsonAsync(rota, corpo, cts.Token)
            : await httpClient.PutAsJsonAsync(rota, corpo, cts.Token);

        if (resp.IsSuccessStatusCode)
        {
            var view = await resp.Content.ReadFromJsonAsync<QuestionarioView>(cancellationToken: cts.Token);
            return new QuestionarioResult(true, view, null);
        }

        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var err = await resp.Content.ReadFromJsonAsync<ValidationErrorResponse>(cancellationToken: cts.Token);
            return new QuestionarioResult(false, null, err?.Errors ?? ["Não foi possível gravar suas respostas."]);
        }

        var texto = await resp.Content.ReadAsStringAsync(cts.Token);
        return new QuestionarioResult(false, null, [$"Erro HTTP {(int)resp.StatusCode}: {texto}"]);
    }
}

public sealed record SalvarQuestionarioRequest(int PassoAtual, IReadOnlyList<RespostaEnviada> Respostas);

public sealed record RespostaEnviada(string PerguntaCodigo, string OpcaoCodigo, string? TextoLivre);

public sealed record QuestionarioResult(
    bool Success, QuestionarioView? Questionario, IReadOnlyList<string>? Errors);

// --- Espelho dos DTOs de QuestionariosEndpoints ---------------------------------------

public sealed record CatalogoView(int Versao, IReadOnlyList<SecaoView> Secoes);

public sealed record SecaoView(string Titulo, string? Descricao, IReadOnlyList<PerguntaView> Perguntas);

public sealed record PerguntaView(
    string Codigo, string Texto, bool Obrigatoria, IReadOnlyList<OpcaoView> Opcoes);

public sealed record OpcaoView(string Codigo, string Texto, int? Valor, bool PermiteTextoLivre);

/// <param name="Id"><c>null</c> = ainda não há rascunho; a tela desenha o wizard vazio.</param>
/// <param name="ExecucoesAvaliadas">Execuções com Seção G respondida por este comprador.</param>
public sealed record QuestionarioView(
    Guid? Id,
    DateTimeOffset? EnviadoEm,
    int PassoAtual,
    int VersaoCatalogo,
    int ExecucoesAvaliadas,
    int MinimoExigido,
    bool Liberado,
    IReadOnlyList<RespostaView> Respostas)
{
    /// <summary>
    /// Se o questionário já foi selado — a tela entra em modo leitura.
    ///
    /// <para>
    /// <b>Vem de <see cref="EnviadoEm"/>, e não mais do status da sessão.</b> Enquanto o
    /// questionário pertencia a uma execução, a autoridade sobre "selado" era ela estar
    /// concluída; agora o questionário não pertence a execução nenhuma, e o carimbo é a única
    /// autoridade que resta — que é também por isso que a tabela não tem coluna de situação.
    /// </para>
    /// </summary>
    public bool Selado => EnviadoEm is not null;

    /// <summary>
    /// Se o comprador pode responder agora: o portão abriu e ele ainda não enviou.
    /// </summary>
    public bool PodeResponder => Liberado && !Selado;

    /// <summary>Quantas execuções ainda faltam avaliar. Zero quando o portão já abriu.</summary>
    public int Faltam => Math.Max(0, MinimoExigido - ExecucoesAvaliadas);
}

public sealed record RespostaView(
    string PerguntaCodigo, string OpcaoCodigo, int? OpcaoValor, string? TextoLivre);

// --- Tabulação das avaliações ---------------------------------------------------------

/// <param name="Participantes">
/// Pessoas distintas com atividade nesta rede. Vem do servidor, das próprias atribuições de
/// pseudônimo — a tela não recalcula, senão passariam a existir duas definições de
/// "participante".
/// </param>
/// <param name="Execucoes">Uma linha por execução, com a Seção G.</param>
/// <param name="Questionarios">
/// Uma linha por <b>comprador</b>. Os dois blocos são separados desde 19/09/2026: repetir as
/// respostas do questionário em cada execução do mesmo comprador faria a planilha parecer ter
/// N questionários onde há um.
/// </param>
public sealed record TabulacaoView(
    int RedeId,
    IReadOnlyList<string> Codigos,
    IReadOnlyList<string> CodigosDeTexto,
    int Participantes,
    IReadOnlyList<ExecucaoAvaliada> Execucoes,
    IReadOnlyList<QuestionarioDoComprador> Questionarios)
{
    /// <summary>
    /// Se a célula deste código traz o texto da opção em vez do número da escala. Vem do
    /// catálogo, e não de heurística sobre o dado — foi a heurística que exportou "2" onde o
    /// patrocinador esperava "Entre 2 e 5 anos".
    /// </summary>
    public bool EhTexto(string codigo) => CodigosDeTexto.Contains(codigo);

    public int ComAvaliacao => Execucoes.Count(l => l.AvaliacaoVeredito is not null);

    public int Respondidos => Questionarios.Count(q => q.EnviadoEm is not null);
}

/// <param name="Avaliador">
/// <b>Pseudônimo</b> de quem registrou a Seção G — P01, P02, P03… —, nunca nome nem e-mail. A
/// identidade não sai do banco: o servidor não consulta a tabela de usuários. Ver a nota em
/// <c>QuestionariosEndpoints.TabulacaoAsync</c>.
/// </param>
public sealed record ExecucaoAvaliada(
    Guid SessaoId,
    DateTimeOffset CriadoEm,
    string Status,
    long? SugestaoId,
    string? SugestaoDescricao,
    string? AvaliacaoVeredito,
    string? AvaliacaoComentario,
    DateTimeOffset? AvaliacaoEm,
    string? Avaliador)
{
    public bool Avaliada => AvaliacaoVeredito is not null;
}

/// <param name="Respondente">
/// <b>Pseudônimo</b> de quem respondeu. É o mesmo código que aparece como
/// <c>Avaliador</c> nas execuções dele — é o que liga os dois blocos sem identificar ninguém.
/// </param>
public sealed record QuestionarioDoComprador(
    string? Respondente,
    DateTimeOffset? EnviadoEm,
    int VersaoCatalogo,
    IReadOnlyList<RespostaTabulada> Respostas)
{
    /// <summary>
    /// O que vai na célula deste código. Devolve string vazia quando não há resposta, e
    /// <b>nunca "0"</b>: zero seria uma posição na escala, e a mais baixa dela.
    /// </summary>
    /// <param name="comoTexto">
    /// Força o texto da opção mesmo havendo número na escala — é o caso do A2, cujas faixas de
    /// experiência têm ordem real mas cuja leitura útil é "Entre 2 e 5 anos". Quem decide é o
    /// catálogo, via <see cref="TabulacaoView.EhTexto"/>; deduzir da presença de
    /// <c>OpcaoValor</c> confunde "a escala é ordenada?" com "o que vai na planilha?".
    /// </param>
    public string Valor(string codigo, bool comoTexto = false)
    {
        var r = Respostas.FirstOrDefault(x => x.PerguntaCodigo == codigo);
        if (r is null) return "";
        if (comoTexto) return r.OpcaoTexto;
        return r.OpcaoValor is { } v ? v.ToString() : r.OpcaoTexto;
    }

    /// <summary>Complemento de "Outro:", quando existe. Vai numa coluna própria na planilha.</summary>
    public string? TextoLivre(string codigo) =>
        Respostas.FirstOrDefault(x => x.PerguntaCodigo == codigo)?.TextoLivre;

    public bool Respondido => EnviadoEm is not null;
}

public sealed record RespostaTabulada(
    string PerguntaCodigo,
    /// <summary>
    /// Retrato do enunciado como foi exibido. Viaja junto porque e a UNICA forma de
    /// interpretar resposta de versao anterior do instrumento: o codigo B7 designou tres
    /// afirmacoes diferentes, e o catalogo atual so conhece a ultima.
    /// </summary>
    string PerguntaTexto,
    string OpcaoTexto,
    int? OpcaoValor,
    string? TextoLivre);
