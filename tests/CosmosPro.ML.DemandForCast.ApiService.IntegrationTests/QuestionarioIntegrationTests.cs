using System.Net;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Entities;
using CosmosPro.ML.DemandForCast.Engine.Questionarios;
using Microsoft.EntityFrameworkCore;
using Refit;

namespace CosmosPro.ML.DemandForCast.ApiService.IntegrationTests;

/// <summary>
/// O questionário do <b>comprador</b>: o portão que o libera, o que pode ser gravado, o que sela,
/// e o que a selagem trava depois. Mais a seção G, que é o que conclui cada execução.
///
/// <para>
/// <b>O fluxo mudou em 19/09/2026</b> (documento do patrocinador de 16/09, por orientação do
/// Professor). Antes havia um questionário por execução e era o envio dele que concluía a
/// sessão. Agora a seção G conclui cada execução, e o questionário é respondido <b>uma vez por
/// comprador</b>, liberado só depois de
/// <see cref="QuestionarioCatalogo.MinimoDeExecucoes"/> execuções avaliadas por ele.
/// </para>
///
/// <para>
/// A sessão é posta em <c>AguardandoAvaliacao</c> por escrita direta no banco, e não pelo
/// caminho legítimo: chegar lá de verdade custa importar um ZIP, treinar um modelo e esperar
/// três filas — dezenas de minutos, já cobertos por
/// <see cref="SessaoOrquestracaoIntegrationTests"/>.
/// </para>
///
/// <para>
/// <b>CADA TESTE USA UM COMPRADOR PRÓPRIO</b>, e isso não é estilo. O índice único passou a ser
/// <c>UQ_Questionarios_UsuarioId</c>, e o banco de integração é persistente entre execuções:
/// um Guid fixo compartilhado faria o segundo teste a rodar encontrar o questionário do
/// primeiro e falhar por "já respondeu", com o sintoma a quilômetros da causa.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class QuestionarioIntegrationTests(AppHostFixture fixture)
{
    /// <summary>Um comprador novo por cenário. Ver a nota da classe.</summary>
    private static Guid NovoComprador() => Guid.CreateVersion7();

    [Fact]
    public async Task Catalogo_responde_com_a_versao_e_as_secoes_do_engine()
    {
        var resp = await fixture.QuestionariosApi.CatalogoAsync(TestContext.Current.CancellationToken);

        DeveTerSucesso(resp, "o catálogo é estático e não depende de rede");
        resp.Content!.Versao.Should().Be(QuestionarioCatalogo.Versao);
        resp.Content.Secoes.Should().HaveCount(QuestionarioCatalogo.Secoes.Count);
    }

    // --- O portão -----------------------------------------------------------------------

    /// <summary>
    /// Sem execução avaliada nenhuma, o questionário está fechado — e a API <b>diz quanto
    /// falta</b>. A tela precisa do número para explicar em vez de esconder o botão: botão
    /// ausente lê-se como defeito.
    /// </summary>
    [Fact]
    public async Task Sem_execucao_avaliada_o_questionario_nao_esta_liberado()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-portao-zero");

        var resp = await fixture.QuestionariosApi.GetAsync(rede, comprador);

        DeveTerSucesso(resp, "portão fechado não é 404 — a tela precisa das contagens");
        resp.Content!.ExecucoesAvaliadas.Should().Be(0);
        resp.Content.MinimoExigido.Should().Be(QuestionarioCatalogo.MinimoDeExecucoes);
        resp.Content.Liberado.Should().BeFalse();
        resp.Content.EnviadoEm.Should().BeNull();
        resp.Content.Id.Should().BeNull("ainda não há rascunho");
    }

    [Fact]
    public async Task Com_uma_execucao_avaliada_o_questionario_continua_fechado()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-portao-um");
        await ExecucaoAvaliadaAsync(rede, comprador);

        var resp = await fixture.QuestionariosApi.GetAsync(rede, comprador);

        DeveTerSucesso(resp, "");
        resp.Content!.ExecucoesAvaliadas.Should().Be(1);
        resp.Content.Liberado.Should().BeFalse(
            "o instrumento pergunta sobre a ferramenta, e uma execução só não dá base de comparação");
    }

    [Fact]
    public async Task Com_duas_execucoes_avaliadas_o_questionario_libera()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-portao-dois");
        await ExecucaoAvaliadaAsync(rede, comprador);
        await ExecucaoAvaliadaAsync(rede, comprador);

        var resp = await fixture.QuestionariosApi.GetAsync(rede, comprador);

        DeveTerSucesso(resp, "");
        resp.Content!.ExecucoesAvaliadas.Should().Be(2);
        resp.Content.Liberado.Should().BeTrue();
    }

    /// <summary>
    /// <b>Execução sem seção G não conta.</b> O patrocinador exigiu as duas coisas: realizar a
    /// simulação e avaliá-la. Contar execução existente deixaria o portão abrir para quem
    /// nunca opinou sobre nada.
    /// </summary>
    [Fact]
    public async Task Execucao_sem_secao_G_nao_conta_para_o_portao()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-portao-sem-g");
        await ExecucaoAvaliadaAsync(rede, comprador);
        await SessaoAguardandoAsync("q-portao-sem-g", rede);   // criada e NÃO avaliada

        var resp = await fixture.QuestionariosApi.GetAsync(rede, comprador);

        DeveTerSucesso(resp, "");
        resp.Content!.ExecucoesAvaliadas.Should().Be(1);
        resp.Content.Liberado.Should().BeFalse();
    }

    /// <summary>
    /// A avaliação de OUTRA pessoa não abre o portão deste comprador. O questionário é sobre a
    /// experiência de quem responde.
    /// </summary>
    [Fact]
    public async Task Execucao_avaliada_por_outro_comprador_nao_conta()
    {
        var eu = NovoComprador();
        var outro = NovoComprador();
        var rede = await EnsureRedeAsync("q-portao-outro");
        await ExecucaoAvaliadaAsync(rede, outro);
        await ExecucaoAvaliadaAsync(rede, outro);

        var resp = await fixture.QuestionariosApi.GetAsync(rede, eu);

        DeveTerSucesso(resp, "");
        resp.Content!.ExecucoesAvaliadas.Should().Be(0);
        resp.Content.Liberado.Should().BeFalse();
    }

    /// <summary>
    /// <b>O portão é do servidor, não da tela.</b> Esconder o botão não impede um POST direto, e
    /// uma resposta entrando fora do protocolo da pesquisa não tem como ser identificada depois.
    /// </summary>
    [Fact]
    public async Task Enviar_sem_as_duas_execucoes_e_recusado_com_409()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-envio-cedo");
        await ExecucaoAvaliadaAsync(rede, comprador);

        var resp = await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        CorpoDoErro(resp).Should().Contain("execuções avaliadas");
    }

    /// <summary>
    /// Rascunho também é barrado antes da liberação: aceitá-lo criaria a linha que a própria
    /// checagem de "já respondeu" passaria a encontrar.
    /// </summary>
    [Fact]
    public async Task Rascunho_antes_da_liberacao_e_recusado_com_409()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-rascunho-cedo");

        var resp = await fixture.QuestionariosApi.SalvarAsync(
            new SalvarQuestionarioBody(0, []), rede, comprador);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- Gravação -----------------------------------------------------------------------

    [Fact]
    public async Task PUT_grava_parcial_e_o_GET_devolve_o_que_foi_gravado()
    {
        var (rede, comprador) = await CompradorLiberadoAsync("q-put-parcial");
        var primeira = QuestionarioCatalogo.Perguntas[0];

        var put = await fixture.QuestionariosApi.SalvarAsync(
            new SalvarQuestionarioBody(0, [new(primeira.Codigo, primeira.Opcoes[0].Codigo, null)]),
            rede, comprador);

        DeveTerSucesso(put, "rascunho parcial é válido");

        var get = await fixture.QuestionariosApi.GetAsync(rede, comprador);
        DeveTerSucesso(get, "");
        get.Content!.Id.Should().NotBeNull();
        get.Content.EnviadoEm.Should().BeNull("rascunho não está selado");
        get.Content.Respostas.Should().ContainSingle()
            .Which.PerguntaCodigo.Should().Be(primeira.Codigo);
    }

    /// <summary>
    /// O PUT substitui o conjunto inteiro, e não casa resposta por resposta: a tela manda o
    /// estado completo do wizard, então pergunta ausente do corpo é "sem resposta", e não
    /// "mantenha a antiga". É o que torna o desmarcar possível.
    /// </summary>
    [Fact]
    public async Task PUT_repetido_substitui_o_conjunto_em_vez_de_acumular()
    {
        var (rede, comprador) = await CompradorLiberadoAsync("q-put-substitui");
        var p0 = QuestionarioCatalogo.Perguntas[0];
        var p1 = QuestionarioCatalogo.Perguntas[1];

        await fixture.QuestionariosApi.SalvarAsync(
            new SalvarQuestionarioBody(0,
            [
                new(p0.Codigo, p0.Opcoes[0].Codigo, null),
                new(p1.Codigo, p1.Opcoes[0].Codigo, null),
            ]), rede, comprador);

        var segundo = await fixture.QuestionariosApi.SalvarAsync(
            new SalvarQuestionarioBody(0, [new(p1.Codigo, p1.Opcoes[0].Codigo, null)]),
            rede, comprador);

        DeveTerSucesso(segundo, "");
        segundo.Content!.Respostas.Should().ContainSingle()
            .Which.PerguntaCodigo.Should().Be(p1.Codigo);
    }

    [Fact]
    public async Task PUT_com_opcao_fora_do_catalogo_retorna_400_e_nao_grava()
    {
        var (rede, comprador) = await CompradorLiberadoAsync("q-put-invalido");
        var p0 = QuestionarioCatalogo.Perguntas[0];

        var resp = await fixture.QuestionariosApi.SalvarAsync(
            new SalvarQuestionarioBody(0, [new(p0.Codigo, "OPCAO-QUE-NAO-EXISTE", null)]),
            rede, comprador);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var get = await fixture.QuestionariosApi.GetAsync(rede, comprador);
        get.Content!.Respostas.Should().BeEmpty("resposta malformada nunca é gravada");
    }

    [Fact]
    public async Task Enviar_incompleto_retorna_400_e_nao_sela()
    {
        var (rede, comprador) = await CompradorLiberadoAsync("q-envio-incompleto");
        var p0 = QuestionarioCatalogo.Perguntas[0];

        var resp = await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, [new(p0.Codigo, p0.Opcoes[0].Codigo, null)]),
            rede, comprador);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var get = await fixture.QuestionariosApi.GetAsync(rede, comprador);
        get.Content!.EnviadoEm.Should().BeNull("faltou pergunta obrigatória");
    }

    /// <summary>
    /// O envio sela o questionário e <b>não toca em sessão nenhuma</b>. Antes ele concluía a
    /// sessão avaliada; hoje quem conclui é a seção G, e o questionário não pertence a execução
    /// alguma.
    /// </summary>
    [Fact]
    public async Task Enviar_completo_sela_o_questionario_e_nao_mexe_nas_execucoes()
    {
        var (rede, comprador, sessoes) = await CompradorLiberadoComSessoesAsync("q-envio-ok");

        var resp = await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador);

        DeveTerSucesso(resp, "todas as obrigatórias respondidas");
        resp.Content!.EnviadoEm.Should().NotBeNull();

        foreach (var id in sessoes)
        {
            var sessao = await fixture.ComparacoesApi.GetAsync(id, rede);
            sessao.Content!.Status.Should().Be("Concluida",
                "as execuções já haviam sido concluídas pela seção G, e o envio não as altera");
        }
    }

    /// <summary>
    /// O retrato do enunciado e da opção é gravado com a resposta. É o que mantém o registro
    /// legível se o instrumento for editado depois — e o que torna a numeração de versão
    /// dispensável na leitura.
    /// </summary>
    [Fact]
    public async Task Envio_grava_o_retrato_do_texto_da_pergunta_e_da_opcao()
    {
        var (rede, comprador) = await CompradorLiberadoAsync("q-retrato");
        var p0 = QuestionarioCatalogo.Perguntas[0];

        DeveTerSucesso(await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador), "");

        await using var db = await AbrirEngineAsync();
        var q = await db.Questionarios.SingleAsync(x => x.UsuarioId == comprador);
        var r = await db.QuestionarioRespostas
            .SingleAsync(x => x.QuestionarioId == q.Id && x.PerguntaCodigo == p0.Codigo);

        r.PerguntaTexto.Should().Be(p0.Texto);
        r.OpcaoTexto.Should().Be(p0.Opcoes[0].Texto);
    }

    /// <summary>
    /// <b>Uma vez por comprador.</b> O segundo envio é recusado, e a garantia de fundo é o
    /// índice único <c>UQ_Questionarios_UsuarioId</c> — a checagem do endpoint é a mensagem
    /// legível, não a trava.
    /// </summary>
    [Fact]
    public async Task Segundo_envio_do_mesmo_comprador_retorna_409()
    {
        var (rede, comprador) = await CompradorLiberadoAsync("q-envio-duplo");

        DeveTerSucesso(await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador), "");

        var segundo = await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador);

        segundo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        CorpoDoErro(segundo).Should().Contain("uma única vez");
    }

    [Fact]
    public async Task PUT_depois_do_envio_retorna_409()
    {
        var (rede, comprador) = await CompradorLiberadoAsync("q-put-pos-envio");

        DeveTerSucesso(await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador), "");

        var put = await fixture.QuestionariosApi.SalvarAsync(
            new SalvarQuestionarioBody(0, []), rede, comprador);

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- Seção G ------------------------------------------------------------------------

    /// <summary>
    /// <b>A seção G conclui a execução.</b> Até 19/09/2026 quem concluía era o envio do
    /// questionário; com ele respondido uma vez só por comprador, da terceira execução em
    /// diante nenhuma sessão chegaria a <c>Concluida</c> — ficariam todas paradas para sempre
    /// numa fase que nenhum worker reclama.
    /// </summary>
    [Fact]
    public async Task Registrar_a_secao_G_conclui_a_execucao()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-secao-g");
        var (_, sessaoId) = await SessaoAguardandoAsync("q-secao-g", rede);

        var resp = await fixture.ComparacoesApi.RegistrarAvaliacaoAsync(
            sessaoId, new AvaliacaoBody("ValidoComRessalvas", "pontos fortes e fracos"),
            rede, comprador);

        DeveTerSucesso(resp, "a seção G é o que encerra a execução");

        var sessao = await fixture.ComparacoesApi.GetAsync(sessaoId, rede);
        sessao.Content!.Status.Should().Be("Concluida");
    }

    /// <summary>
    /// Registrar de novo é <b>correção do veredito</b>, não transição. Uma guarda de
    /// <c>PodeTransicionar</c> cega recusaria <c>Concluida → Concluida</c> e transformaria
    /// "corrigir" em erro.
    /// </summary>
    [Fact]
    public async Task Registrar_de_novo_substitui_o_veredito_sem_quebrar_a_maquina()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-secao-g-recorrige");
        var (_, sessaoId) = await SessaoAguardandoAsync("q-secao-g-recorrige", rede);

        DeveTerSucesso(await fixture.ComparacoesApi.RegistrarAvaliacaoAsync(
            sessaoId, new AvaliacaoBody("Valido", null), rede, comprador), "");

        var segundo = await fixture.ComparacoesApi.RegistrarAvaliacaoAsync(
            sessaoId, new AvaliacaoBody("NaoValido", "mudei de ideia"), rede, comprador);

        DeveTerSucesso(segundo, "corrigir o veredito continua permitido");
        segundo.Content!.Veredito.Should().Be("NaoValido");

        var sessao = await fixture.ComparacoesApi.GetAsync(sessaoId, rede);
        sessao.Content!.Status.Should().Be("Concluida");
    }

    /// <summary>
    /// Execução concluída recusa exclusão — agora para proteger a <b>seção G</b>, e não o
    /// questionário. Execução ainda não avaliada continua excluível: é o comprador decidindo
    /// não opinar.
    /// </summary>
    [Fact]
    public async Task Execucao_avaliada_recusa_exclusao_e_nao_avaliada_ainda_exclui()
    {
        var comprador = NovoComprador();
        var rede = await EnsureRedeAsync("q-exclusao");

        var (_, avaliada) = await SessaoAguardandoAsync("q-exclusao", rede);
        DeveTerSucesso(await fixture.ComparacoesApi.RegistrarAvaliacaoAsync(
            avaliada, new AvaliacaoBody("Valido", null), rede, comprador), "");

        var (_, semAvaliacao) = await SessaoAguardandoAsync("q-exclusao", rede);

        (await fixture.ComparacoesApi.ExcluirAsync(avaliada, rede))
            .IsSuccessStatusCode.Should().BeFalse("avaliação de pesquisa não evapora por clique");

        DeveTerSucesso(await fixture.ComparacoesApi.ExcluirAsync(semAvaliacao, rede),
            "execução que ninguém avaliou continua excluível");
    }

    // --- Tabulação ----------------------------------------------------------------------

    /// <summary>
    /// <b>Dois blocos, e eles contam coisas diferentes.</b> Duas execuções avaliadas pela mesma
    /// pessoa produzem UM questionário: numa lista só, as respostas apareceriam repetidas em
    /// cada execução e qualquer contagem sairia multiplicada.
    /// </summary>
    [Fact]
    public async Task Tabulacao_separa_execucoes_de_questionarios()
    {
        var rede = await EnsureRedeAsync("q-tab-blocos");
        var comprador = NovoComprador();
        var uma = await ExecucaoAvaliadaAsync(rede, comprador);
        var outra = await ExecucaoAvaliadaAsync(rede, comprador);

        DeveTerSucesso(await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador), "");

        var resp = await fixture.QuestionariosApi.TabulacaoAsync(rede);
        DeveTerSucesso(resp, "");

        // A AFIRMAÇÃO É POR PESSOA, e não pela rede inteira. A rede é reaproveitada por slug e
        // o banco é persistente, então ela acumula os compradores de execuções anteriores da
        // suíte — "a rede tem um questionário" seria verdade só na primeira vez que a suíte
        // roda, e a falha apareceria como flakiness.
        var minhas = resp.Content!.Execucoes
            .Where(e => e.SessaoId == uma || e.SessaoId == outra)
            .ToList();

        minhas.Should().HaveCount(2, "as duas execuções que este cenário criou têm de estar lá");
        minhas.Select(e => e.Avaliador).Distinct().Should().ContainSingle(
            "as duas foram avaliadas pela mesma pessoa, então carregam o mesmo código");

        var meuCodigo = minhas[0].Avaliador;
        meuCodigo.Should().NotBeNull();

        resp.Content.Questionarios.Where(q => q.Respondente == meuCodigo).Should().ContainSingle(
            "duas execuções do mesmo comprador produzem UM questionário, não dois")
            .Which.Respostas.Should().NotBeEmpty();
    }

    /// <summary>
    /// A identidade <b>não sai do banco</b>: o participante viaja como código (P01, P02…), e o
    /// mesmo código liga os dois blocos. Foi a solução do patrocinador para o impasse do
    /// consentimento — contar pessoas e repetições sem identificar ninguém.
    /// </summary>
    [Fact]
    public async Task Tabulacao_identifica_o_participante_por_codigo_e_nao_por_email()
    {
        var rede = await EnsureRedeAsync("q-tab-pseudonimo");
        var comprador = NovoComprador();
        var minhaPrimeira = await ExecucaoAvaliadaAsync(rede, comprador);
        var minhaSegunda = await ExecucaoAvaliadaAsync(rede, comprador);

        DeveTerSucesso(await fixture.QuestionariosApi.EnviarAsync(
            new SalvarQuestionarioBody(0, Respostas()), rede, comprador), "");

        var resp = await fixture.QuestionariosApi.TabulacaoAsync(rede);
        DeveTerSucesso(resp, "");

        var codigos = resp.Content!.Execucoes
            .Select(e => e.Avaliador)
            .Concat(resp.Content.Questionarios.Select(q => q.Respondente))
            .Where(c => c is not null)
            .ToList();

        codigos.Should().NotBeEmpty();
        codigos.Should().AllSatisfy(c => c.Should().MatchRegex("^P[0-9]{2,}$"));
        codigos.Should().NotContain(c => c!.Contains('@'), "e-mail não pode sair daqui");
        codigos.Should().NotContain(c => c!.Contains(comprador.ToString()), "nem o id");

        // Pelo mesmo motivo do teste acima, olha o QUESTIONÁRIO DESTE comprador, e não o
        // primeiro da lista: a rede acumula participantes entre execuções da suíte.
        var meuCodigo = resp.Content.Execucoes
            .Where(e => e.SessaoId == minhaPrimeira || e.SessaoId == minhaSegunda)
            .Select(e => e.Avaliador)
            .Distinct()
            .Single();

        resp.Content.Questionarios.Select(q => q.Respondente).Should().Contain(meuCodigo,
            "o código é o que liga os dois blocos");
    }

    [Fact]
    public async Task Tabulacao_nao_mistura_redes()
    {
        var redeA = await EnsureRedeAsync("q-tab-rede-a");
        var redeB = await EnsureRedeAsync("q-tab-rede-b");
        var deA = NovoComprador();
        await ExecucaoAvaliadaAsync(redeA, deA);

        var resp = await fixture.QuestionariosApi.TabulacaoAsync(redeB);

        DeveTerSucesso(resp, "");
        resp.Content!.RedeId.Should().Be(redeB);
        resp.Content.Execucoes.Should().NotContain(
            e => e.SessaoId == Guid.Empty, "sanidade");
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Afirma sucesso <b>mostrando o corpo do erro</b> quando falha. Um
    /// <c>IsSuccessStatusCode.Should().BeTrue()</c> cru diz só "esperava True, achei False" —
    /// e aí descobrir se foi 400 de validação ou 500 de bug exige rodar o cenário à mão.
    /// </summary>
    private static void DeveTerSucesso(IApiResponse resp, string porque)
    {
        resp.IsSuccessStatusCode.Should().BeTrue(
            $"{porque}. Status {(int)resp.StatusCode}: {CorpoDoErro(resp)}");
    }

    private static string? CorpoDoErro(IApiResponse resp) =>
        resp.Error is ApiException api ? api.Content : resp.Error?.Message;

    /// <summary>Uma resposta válida para cada pergunta obrigatória, montada do próprio catálogo.</summary>
    private static List<RespostaBody> Respostas() =>
    [
        .. QuestionarioCatalogo.Perguntas
            .Where(p => p.Obrigatoria)
            .Select(p => new RespostaBody(p.Codigo, p.Opcoes[0].Codigo, null))
    ];

    /// <summary>
    /// Uma execução criada, posta em <c>AguardandoAvaliacao</c> e <b>avaliada</b> por este
    /// comprador — que é o que a faz contar para o portão.
    /// </summary>
    private async Task<Guid> ExecucaoAvaliadaAsync(int rede, Guid comprador)
    {
        var (_, sessaoId) = await SessaoAguardandoAsync($"rede-{rede}", rede);

        DeveTerSucesso(await fixture.ComparacoesApi.RegistrarAvaliacaoAsync(
            sessaoId, new AvaliacaoBody("Valido", null), rede, comprador),
            "a seção G é o que faz a execução contar para o portão");

        return sessaoId;
    }

    /// <summary>Comprador novo com o portão já aberto: duas execuções avaliadas por ele.</summary>
    private async Task<(int Rede, Guid Comprador)> CompradorLiberadoAsync(string slug)
    {
        var (rede, comprador, _) = await CompradorLiberadoComSessoesAsync(slug);
        return (rede, comprador);
    }

    private async Task<(int Rede, Guid Comprador, List<Guid> Sessoes)> CompradorLiberadoComSessoesAsync(
        string slug)
    {
        var rede = await EnsureRedeAsync(slug);
        var comprador = NovoComprador();

        List<Guid> sessoes = [];
        for (var i = 0; i < QuestionarioCatalogo.MinimoDeExecucoes; i++)
        {
            sessoes.Add(await ExecucaoAvaliadaAsync(rede, comprador));
        }

        return (rede, comprador, sessoes);
    }

    /// <summary>
    /// Cria a sessão pela API e a move para <c>AguardandoAvaliacao</c> por escrita direta.
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
        sessao.Status = SessaoStatus.AguardandoAvaliacao;
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
