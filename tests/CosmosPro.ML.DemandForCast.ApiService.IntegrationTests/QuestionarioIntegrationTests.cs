using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Engine.Questionarios;
using Microsoft.EntityFrameworkCore;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// O questionário como <b>última fase</b> da sessão: o que pode ser gravado, o que sela, e o que
/// a selagem trava depois.
///
/// <para>
/// A sessão é posta em <c>AguardandoQuestionario</c> por escrita direta no banco, e não pelo
/// caminho legítimo: chegar lá de verdade custa importar um ZIP, treinar um modelo e esperar
/// três filas — dezenas de minutos, já cobertos por
/// <see cref="SessaoOrquestracaoIntegrationTests"/>. O que estes testes exercitam é o
/// comportamento dos endpoints do questionário, que começa exatamente naquele estado.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class QuestionarioIntegrationTests(AppHostFixture fixture)
{
    private static readonly Guid Usuario = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Catalogo_responde_com_a_versao_e_as_secoes_do_engine()
    {
        var resp = await fixture.QuestionariosApi.CatalogoAsync(TestContext.Current.CancellationToken);

        resp.IsSuccessStatusCode.Should().BeTrue();
        resp.Content!.Versao.Should().Be(QuestionarioCatalogo.Versao);
        resp.Content.Secoes.Should().HaveCount(QuestionarioCatalogo.Secoes.Count);

        // O contrato que a tela consome: cada pergunta chega com suas alternativas, senão o
        // wizard desenha um passo sem nada para escolher.
        resp.Content.Secoes.SelectMany(s => s.Perguntas).Should().OnlyContain(p => p.Opcoes.Count > 1);
    }

    [Fact]
    public async Task GET_sem_rascunho_devolve_wizard_vazio_e_nao_404()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-get-vazio");

        var resp = await fixture.QuestionariosApi.GetAsync(sessaoId, rede, TestContext.Current.CancellationToken);

        resp.IsSuccessStatusCode.Should().BeTrue(
            "ainda não haver rascunho é o estado inicial normal, não erro");
        resp.Content!.Id.Should().BeNull();
        resp.Content.Respostas.Should().BeEmpty();
        resp.Content.SessaoStatus.Should().Be("AguardandoQuestionario");
    }

    [Fact]
    public async Task PUT_grava_parcial_e_o_GET_devolve_o_que_foi_gravado()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-rascunho");
        var primeira = Respostas().Take(1).ToList();

        var salvo = await fixture.QuestionariosApi.SalvarAsync(
            sessaoId, new SalvarQuestionarioBody(1, primeira), rede, Usuario,
            TestContext.Current.CancellationToken);

        DeveTerSucesso(salvo,
            "rascunho incompleto tem de gravar — é o que permite 'salvo, volto depois'");
        salvo.Content!.EnviadoEm.Should().BeNull("rascunho não é envio");

        var lido = await fixture.QuestionariosApi.GetAsync(sessaoId, rede, TestContext.Current.CancellationToken);

        lido.Content!.PassoAtual.Should().Be(1);
        lido.Content.Respostas.Should().ContainSingle()
            .Which.PerguntaCodigo.Should().Be(primeira[0].PerguntaCodigo);
        lido.Content.SessaoStatus.Should().Be("AguardandoQuestionario",
            "gravar rascunho não pode concluir a sessão");
    }

    /// <summary>
    /// O <c>PUT</c> manda o estado completo do wizard, então ele substitui em vez de somar.
    /// Sem isso, trocar de alternativa deixaria as duas gravadas — ou estouraria a PK.
    /// </summary>
    [Fact]
    public async Task PUT_repetido_substitui_o_conjunto_em_vez_de_acumular()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-idempotente");
        var todas = Respostas();
        var body = new SalvarQuestionarioBody(2, todas);

        await fixture.QuestionariosApi.SalvarAsync(sessaoId, body, rede, Usuario, TestContext.Current.CancellationToken);
        var segundo = await fixture.QuestionariosApi.SalvarAsync(
            sessaoId, body, rede, Usuario, TestContext.Current.CancellationToken);

        segundo.IsSuccessStatusCode.Should().BeTrue();
        segundo.Content!.Respostas.Should().HaveCount(todas.Count);

        await using var db = await AbrirEngineAsync();
        var questionarios = await db.Questionarios.CountAsync(q => q.SessaoId == sessaoId);
        questionarios.Should().Be(1, "o índice único em SessaoId é um questionário por sessão");
    }

    [Fact]
    public async Task PUT_com_opcao_fora_do_catalogo_retorna_400_e_nao_grava()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-opcao-invalida");
        var alvo = QuestionarioCatalogo.Perguntas[0];

        var resp = await fixture.QuestionariosApi.SalvarAsync(
            sessaoId,
            new SalvarQuestionarioBody(0, [new RespostaBody(alvo.Codigo, "NAO_EXISTE", null)]),
            rede, Usuario, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = await AbrirEngineAsync();
        (await db.Questionarios.AnyAsync(q => q.SessaoId == sessaoId)).Should().BeFalse(
            "uma resposta recusada não pode deixar cabeçalho para trás");
    }

    [Fact]
    public async Task Enviar_incompleto_retorna_400_e_mantem_a_sessao_aguardando()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-envio-incompleto");

        var resp = await fixture.QuestionariosApi.EnviarAsync(
            sessaoId, new SalvarQuestionarioBody(0, []), rede, Usuario,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "400 e não 409: o que falta é conteúdo da requisição, e quem chama pode corrigir");

        var sessao = await fixture.ComparacoesApi.GetAsync(sessaoId, rede);
        sessao.Content!.Status.Should().Be("AguardandoQuestionario");
    }

    [Fact]
    public async Task Enviar_completo_sela_a_avaliacao_e_conclui_a_sessao()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-envio-ok");

        var resp = await fixture.QuestionariosApi.EnviarAsync(
            sessaoId, new SalvarQuestionarioBody(9, Respostas()), rede, Usuario,
            TestContext.Current.CancellationToken);

        resp.IsSuccessStatusCode.Should().BeTrue();
        resp.Content!.EnviadoEm.Should().NotBeNull();
        resp.Content.SessaoStatus.Should().Be("Concluida");

        var sessao = await fixture.ComparacoesApi.GetAsync(sessaoId, rede);
        sessao.Content!.Status.Should().Be("Concluida",
            "a última transição da máquina de estados é feita por este endpoint, não pelo Worker");

        await using var db = await AbrirEngineAsync();
        var gravado = await db.Questionarios.SingleAsync(q => q.SessaoId == sessaoId);
        gravado.VersaoCatalogo.Should().Be(QuestionarioCatalogo.Versao);
        gravado.UsuarioId.Should().Be(Usuario);
    }

    /// <summary>
    /// O texto viaja denormalizado porque o catálogo vive em código e muda com deploy: sem o
    /// retrato, um ajuste de redação reescreveria retroativamente o que foi perguntado.
    /// </summary>
    [Fact]
    public async Task Envio_grava_o_retrato_do_texto_da_pergunta_e_da_opcao()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-snapshot");
        var respostas = Respostas();

        await fixture.QuestionariosApi.EnviarAsync(
            sessaoId, new SalvarQuestionarioBody(9, respostas), rede, Usuario,
            TestContext.Current.CancellationToken);

        await using var db = await AbrirEngineAsync();
        var questionarioId = await db.Questionarios
            .Where(q => q.SessaoId == sessaoId).Select(q => q.Id).SingleAsync();

        var linhas = await db.QuestionarioRespostas
            .Where(r => r.QuestionarioId == questionarioId).ToListAsync();

        foreach (var linha in linhas)
        {
            var pergunta = QuestionarioCatalogo.Pergunta(linha.PerguntaCodigo);
            pergunta.Should().NotBeNull();
            linha.PerguntaTexto.Should().Be(pergunta!.Texto);
            linha.OpcaoTexto.Should().Be(pergunta.Opcao(linha.OpcaoCodigo)!.Texto);
            linha.OpcaoValor.Should().Be(pergunta.Opcao(linha.OpcaoCodigo)!.Valor);
        }
    }

    [Fact]
    public async Task Segundo_envio_retorna_409_e_nao_reescreve_a_avaliacao()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-envio-duplo");
        var respostas = Respostas();

        var primeiro = await fixture.QuestionariosApi.EnviarAsync(
            sessaoId, new SalvarQuestionarioBody(9, respostas), rede, Usuario,
            TestContext.Current.CancellationToken);
        primeiro.IsSuccessStatusCode.Should().BeTrue();
        var selo = primeiro.Content!.EnviadoEm;

        var segundo = await fixture.QuestionariosApi.EnviarAsync(
            sessaoId, new SalvarQuestionarioBody(9, respostas), rede, Usuario,
            TestContext.Current.CancellationToken);

        segundo.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var lido = await fixture.QuestionariosApi.GetAsync(sessaoId, rede, TestContext.Current.CancellationToken);
        lido.Content!.EnviadoEm.Should().Be(selo, "o selo original não pode ser sobrescrito");
    }

    [Fact]
    public async Task PUT_depois_do_envio_retorna_409()
    {
        var (rede, sessaoId) = await SessaoAguardandoAsync("q-put-pos-envio");
        await fixture.QuestionariosApi.EnviarAsync(
            sessaoId, new SalvarQuestionarioBody(9, Respostas()), rede, Usuario,
            TestContext.Current.CancellationToken);

        var resp = await fixture.QuestionariosApi.SalvarAsync(
            sessaoId, new SalvarQuestionarioBody(0, Respostas()), rede, Usuario,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "resposta selada é imutável: corrigir depois seria reescrever dado de pesquisa");
    }

    /// <summary>
    /// O questionário abre quando o resultado fica pronto. Antes disso não há o que avaliar, e
    /// deixar gravar produziria avaliação de uma comparação que ninguém viu.
    /// </summary>
    [Fact]
    public async Task PUT_em_sessao_que_ainda_nao_comparou_retorna_409()
    {
        var rede = await EnsureRedeAsync("q-antes-da-hora");
        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest("Antes"), rede);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await fixture.QuestionariosApi.SalvarAsync(
            criada.Content!.Id, new SalvarQuestionarioBody(0, Respostas()), rede, Usuario,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Sessao_de_outra_rede_retorna_404_no_get_e_no_put()
    {
        var (redeA, sessaoId) = await SessaoAguardandoAsync("q-rede-a");
        var redeB = await EnsureRedeAsync("q-rede-b");

        (await fixture.QuestionariosApi.GetAsync(sessaoId, redeB, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "404 e não 403: um 403 confirmaria que a sessão existe em outro inquilino");

        (await fixture.QuestionariosApi.SalvarAsync(
                sessaoId, new SalvarQuestionarioBody(0, Respostas()), redeB, Usuario,
                TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A recusa de exclusão depois do envio é o que impede a resposta de evaporar por clique —
    /// e o rascunho, por não ser resposta, não pode trancar a sessão.
    /// </summary>
    [Fact]
    public async Task Sessao_avaliada_recusa_exclusao_mas_com_rascunho_ainda_exclui()
    {
        var (rede, comRascunho) = await SessaoAguardandoAsync("q-exclusao-rascunho");
        await fixture.QuestionariosApi.SalvarAsync(
            comRascunho, new SalvarQuestionarioBody(0, Respostas().Take(1).ToList()), rede, Usuario,
            TestContext.Current.CancellationToken);

        (await fixture.ComparacoesApi.ExcluirAsync(comRascunho, rede))
            .StatusCode.Should().Be(HttpStatusCode.NoContent,
                "rascunho abandonado não pode trancar a sessão");

        var (_, avaliada) = await SessaoAguardandoAsync("q-exclusao-rascunho", rede);
        await fixture.QuestionariosApi.EnviarAsync(
            avaliada, new SalvarQuestionarioBody(9, Respostas()), rede, Usuario,
            TestContext.Current.CancellationToken);

        (await fixture.ComparacoesApi.ExcluirAsync(avaliada, rede))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await fixture.ComparacoesApi.GetAsync(avaliada, rede))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a sessão avaliada continua lá");
    }

    /// <summary>
    /// Afirma sucesso <b>mostrando o corpo do erro</b> quando falha. Um
    /// <c>IsSuccessStatusCode.Should().BeTrue()</c> cru diz só "esperava True, achei False" —
    /// e aí descobrir se foi 400 de validação ou 500 de bug exige rodar o cenário à mão.
    /// </summary>
    private static void DeveTerSucesso(IApiResponse resp, string porque)
    {
        var corpo = resp.Error is ApiException api ? api.Content : resp.Error?.Message;
        resp.IsSuccessStatusCode.Should().BeTrue($"{porque}. Status {(int)resp.StatusCode}: {corpo}");
    }

    /// <summary>Uma resposta válida para cada pergunta obrigatória, montada do próprio catálogo.</summary>
    private static List<RespostaBody> Respostas() =>
    [
        .. QuestionarioCatalogo.Perguntas
            .Where(p => p.Obrigatoria)
            .Select(p => new RespostaBody(p.Codigo, p.Opcoes[0].Codigo, null))
    ];

    /// <summary>
    /// Cria a sessão pela API e a move para <c>AguardandoQuestionario</c> por escrita direta.
    /// Ver a nota da classe para por que o caminho legítimo não é usado aqui.
    /// </summary>
    private async Task<(int Rede, Guid SessaoId)> SessaoAguardandoAsync(string slug, int? redeExistente = null)
    {
        var rede = redeExistente ?? await EnsureRedeAsync(slug);

        var criada = await fixture.ComparacoesApi.CreateAsync(new CreateSessaoRequest(slug), rede);
        criada.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = criada.Content!.Id;

        await using var db = await AbrirEngineAsync();
        var sessao = await db.ComparacaoSessoes.SingleAsync(s => s.Id == id);
        sessao.Status = SessaoStatus.AguardandoQuestionario;
        sessao.AtualizadoEm = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return (rede, id);
    }

    private async Task<EngineDbContext> AbrirEngineAsync()
    {
        var connStr = await fixture.GetEngineConnectionStringAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<EngineDbContext>().UseSqlServer(connStr).Options;
        return new EngineDbContext(options);
    }

    /// <summary>
    /// Mesma abordagem das outras classes de integração: cria a rede ou devolve a existente,
    /// porque o banco é persistente entre runs e o Slug é único.
    /// </summary>
    private async Task<int> EnsureRedeAsync(string slug)
    {
        var criacao = await fixture.RedesApi.CreateAsync(new CreateRedeRequest($"Questionario {slug}", slug));
        if (criacao.IsSuccessStatusCode) return criacao.Content!.Id;

        criacao.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "só conflito de slug é aceitável aqui; outro status é falha real");

        var lista = await fixture.RedesApi.ListAsync();
        lista.IsSuccessStatusCode.Should().BeTrue();
        var existente = lista.Content!.SingleOrDefault(r => r.Slug == slug);
        existente.Should().NotBeNull($"rede '{slug}' deu 409 mas não apareceu na listagem");
        return existente!.Id;
    }
}
