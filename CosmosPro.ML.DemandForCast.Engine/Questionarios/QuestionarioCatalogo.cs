namespace CosmosPro.ML.DemandForCast.Engine.Questionarios;

/// <param name="Valor">
/// Posição da opção na escala, quando a pergunta é ordinal. Nulo em pergunta nominal —
/// ver <c>QuestionarioResposta.OpcaoValor</c>, onde nulo significa "não é ordinal" e nunca
/// "grau zero".
/// </param>
/// <param name="PermiteTextoLivre">
/// Se marcar esta opção abre um campo de complemento (o "Outro:"). Declarado na opção, e não
/// na pergunta, porque é a opção específica que pede o texto.
/// </param>
public sealed record OpcaoDef(string Codigo, string Texto, int? Valor = null, bool PermiteTextoLivre = false);

public sealed record PerguntaDef(
    string Codigo,
    string Texto,
    IReadOnlyList<OpcaoDef> Opcoes,
    bool Obrigatoria = true)
{
    public OpcaoDef? Opcao(string codigo) =>
        Opcoes.FirstOrDefault(o => o.Codigo == codigo);
}

/// <summary>Uma seção = um passo do wizard na tela.</summary>
public sealed record SecaoDef(string Titulo, string? Descricao, IReadOnlyList<PerguntaDef> Perguntas);

/// <summary>
/// O instrumento de avaliação do artefacto — Apêndice A da dissertação (MGSI, ISCTE-IUL).
///
/// <para>
/// <b>Texto literal do documento, em português europeu.</b> O resto da aplicação é pt-BR, e a
/// diferença de variante ("artefacto"/"stocks"/"ruturas" contra "artefato"/"estoque"/"rupturas")
/// é visível na tela de propósito: isto é instrumento de pesquisa, e reescrever enunciado
/// invalida a comparação com o que foi submetido. Não "corrija" para pt-BR, e não reordene as
/// perguntas — os códigos (A1–A3, B1–B11) são os do documento e é por eles que a análise casa
/// as respostas. A4 do documento é a única ausente, por decisão registrada onde ela caberia.
/// </para>
///
/// <para>
/// <b>Conteúdo da Versão 6 do Apêndice A</b>, recebida em 07/09/2026. Ela <b>removeu</b> a
/// afirmação sobre "IA e variáveis externas (sazonalidade, clima, epidemias e dados de mercado)",
/// que era a B6 da V5 — a única que perguntava sobre coisas que o artefacto não faz —, e
/// renumerou as seguintes. A Parte B foi de doze afirmações para onze.
/// </para>
///
/// <para>
/// <b>É a SEGUNDA renumeração do instrumento, e agora seis códigos carregam afirmação diferente
/// da versão anterior</b> (B6 a B11). Antes disso, a V5 já havia reaproveitado o código B7: o que
/// era B7 na V2 (uso na operação diária) virou B12 na V5 e agora é B11 na V6. Consequência que não
/// é estilo: <b>a análise tem de agrupar por <c>(VersaoCatalogo, Codigo)</c>, nunca só por
/// código</b> — somar "B7" das três versões mistura três perguntas distintas. Cada resposta guarda
/// o retrato do enunciado exibido, então o registro individual continua correto; o risco é só na
/// agregação, e ele é silencioso.
/// </para>
///
/// <para>
/// <b>Código e não tabela</b>: o questionário é fixo, revisado por quem conduz a pesquisa, e não
/// muda em tempo de execução. Tabelas de perguntas com CRUD seriam a abstração que o requisito
/// não pede. O preço dessa escolha é que o catálogo muda com deploy — pago em
/// <c>QuestionarioResposta</c>, que grava o texto exibido junto com a resposta, para que um
/// ajuste de redação não reescreva retroativamente o que foi perguntado.
/// </para>
///
/// <para>
/// <b>Fonte única de agrupamento, ordem e obrigatoriedade.</b> A tela desenha os passos daqui e o
/// servidor valida a completude daqui, então não há como as duas discordarem sobre o que é
/// obrigatório — que é o jeito clássico de um wizard deixar enviar formulário incompleto.
/// </para>
/// </summary>
public static class QuestionarioCatalogo
{
    /// <summary>
    /// Incrementar <b>à mão</b> a cada mudança de conteúdo. Respostas antigas guardam a versão
    /// sob a qual foram dadas, então subir este número não invalida nada — só separa populações
    /// que não deveriam ser somadas na análise.
    ///
    /// <para>
    /// 2 = instrumento real (Questionário V3). A versão 1 foi o catálogo provisório que existiu
    /// enquanto o documento não estava disponível; nenhuma resposta foi coletada sob ela.
    /// 3 = Questionário V5 (Parte B de B1 a B12). 4 = Questionário V6, que removeu a antiga B6 e
    /// renumerou as seguintes — ver o aviso de renumeração no doc da classe.
    /// </para>
    /// </summary>
    public const int Versao = 4;

    /// <summary>
    /// Apresentação e termo de consentimento, exibidos <b>antes</b> do primeiro passo. Não é
    /// seção do wizard porque não tem pergunta: um passo vazio quebraria a navegação e a
    /// contagem de "passo N de M".
    ///
    /// <para>
    /// <b>Isto é o que o participante consente</b>, e tem de continuar batendo com o que
    /// <c>Questionario</c> de fato grava. Já não bateu: o texto prometia anonimato e a tabela
    /// guarda <c>UsuarioId</c>. Ao mexer em qualquer um dos dois, confira o outro.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Apresentacao { get; } =
    [
        "O presente questionário integra uma investigação desenvolvida no âmbito do Mestrado em " +
        "Gestão de Sistemas de Informação (MGSI) do ISCTE – Instituto Universitário de Lisboa.",

        "O objetivo deste estudo consiste em avaliar um artefacto baseado em Inteligência " +
        "Artificial, desenvolvido para apoiar a previsão da procura e a gestão de stocks no setor " +
        "do retalho farmacêutico.",

        "Após a demonstração do artefacto e da comparação dos resultados obtidos com o sistema " +
        "atualmente utilizado pela organização (ERP), solicita-se a sua colaboração no " +
        "preenchimento deste questionário, respondendo de acordo com a sua perceção profissional.",

        // A frase que prometia anonimato saiu em 05/09/2026, por decisão de quem conduz a
        // pesquisa. Ela dizia "não será recolhida qualquer informação que permita identificar
        // os participantes" enquanto `Questionarios.UsuarioId` grava exatamente quem respondeu:
        // o participante consentia com uma coisa e o sistema fazia outra. A alternativa seria
        // remover a coluna; manteve-se a coluna e corrigiu-se o texto, então o que se promete
        // agora é confidencialidade e uso restrito — não anonimato.
        "A participação é voluntária e confidencial. A sua resposta fica associada ao utilizador " +
        "com que acedeu à aplicação, para que a investigação possa distinguir participantes " +
        "distintos; os dados são utilizados exclusivamente para fins académicos e não são " +
        "divulgados de forma individualizada.",

        "Tempo estimado de resposta: aproximadamente 3 minutos.",
    ];

    /// <summary>Fecho do documento, exibido depois do envio.</summary>
    public const string Agradecimento = "Muito obrigado pela sua participação!";

    /// <summary>
    /// A escala do documento, idêntica nas sete afirmações da Parte B. O número vai no rótulo
    /// junto do significado porque a tabela de escala do documento impresso não existe na tela —
    /// mostrar só "Concordo" perderia a âncora numérica que o participante usa para se situar.
    /// </summary>
    private static readonly IReadOnlyList<OpcaoDef> Likert =
    [
        new OpcaoDef("1", "1 – Discordo Totalmente", Valor: 1),
        new OpcaoDef("2", "2 – Discordo", Valor: 2),
        new OpcaoDef("3", "3 – Nem Concordo nem Discordo", Valor: 3),
        new OpcaoDef("4", "4 – Concordo", Valor: 4),
        new OpcaoDef("5", "5 – Concordo Totalmente", Valor: 5),
    ];

    public static IReadOnlyList<SecaoDef> Secoes { get; } =
    [
        new SecaoDef(
            "Parte A – Caracterização do Participante",
            null,
            [
                new PerguntaDef("A1", "Qual a função que desempenha atualmente na organização?",
                [
                    new OpcaoDef("COMPRADOR", "Comprador"),
                    new OpcaoDef("GESTOR_COMPRAS", "Gestor de Compras"),
                    new OpcaoDef("GESTOR_STOCKS", "Gestor de Stocks"),
                    new OpcaoDef("DIRETOR_COMERCIAL", "Diretor Comercial"),
                    new OpcaoDef("DIRETOR_OPERACOES", "Diretor de Operações"),
                    new OpcaoDef("FARMACEUTICO", "Farmacêutico"),
                    new OpcaoDef("ANALISTA_DADOS", "Analista de Dados"),
                    new OpcaoDef("OUTRO", "Outro", PermiteTextoLivre: true),
                ]),

                // Ordinal: a escala é o próprio intervalo de anos, então a análise pode ordenar.
                new PerguntaDef("A2", "Há quantos anos trabalha na área?",
                [
                    new OpcaoDef("MENOS_DE_2", "Menos de 2 anos", Valor: 1),
                    new OpcaoDef("DE_2_A_5", "Entre 2 e 5 anos", Valor: 2),
                    new OpcaoDef("DE_6_A_10", "Entre 6 e 10 anos", Valor: 3),
                    new OpcaoDef("MAIS_DE_10", "Mais de 10 anos", Valor: 4),
                ]),

                // Nominal, e não ordinal: "Sim"/"Não" não têm ordem, e dar 1/2 a elas produziria
                // média de uma pergunta dicotômica.
                new PerguntaDef("A3",
                    "A sua organização utiliza atualmente algum sistema de previsão da procura ou " +
                    "de apoio à reposição de stocks?",
                [
                    new OpcaoDef("SIM", "Sim"),
                    new OpcaoDef("NAO", "Não"),
                ]),

                // A4 do documento ("Qual o ERP utilizado pela sua organização?") NÃO está aqui, e
                // a ausência é deliberada — não é esquecimento, não a restaure só porque o
                // Apêndice A a lista. O extrator lê exclusivamente o PBS (o mapeamento inteiro do
                // Stage é PBS -> Stage), então a resposta é constante por construção: perguntá-la
                // ao participante gastaria o tempo dele para produzir uma coluna com um único
                // valor. A análise preenche A4 a partir da própria importação. Se algum dia o
                // extrator falar com um segundo ERP, o lugar de gravar isso é o cadastro da rede
                // ou o manifesto do ZIP — não uma pergunta de questionário.
                //
                // REAFIRMADO EM 05/09/2026, ao receber a Versão 5 do Apêndice A: a V5 lista A4
                // como campo livre, e a decisão de quem conduz a pesquisa foi mantê-la fora.
                // A V5 aparecer com A4 não é decisão nova — o documento sempre a listou, e é
                // justamente esta ausência que é a escolha.
            ]),

        new SecaoDef(
            "Parte B – Avaliação da Solução Desenvolvida",
            "As respostas devem ser dadas com base na demonstração realizada e na comparação entre " +
            "os resultados obtidos pelo sistema atualmente utilizado pela organização (ERP) e os " +
            "resultados produzidos pelo artefacto desenvolvido nesta investigação. Assinale o seu " +
            "grau de concordância relativamente a cada uma das afirmações seguintes, utilizando a " +
            "escala apresentada.",
            [
                new PerguntaDef("B1",
                    "O artefacto produz previsões da procura que considero credíveis.", Likert),

                new PerguntaDef("B2",
                    "Considero que o artefacto melhora a precisão das previsões quando comparado " +
                    "com o método atualmente utilizado pela organização.", Likert),

                new PerguntaDef("B3",
                    "O artefacto fornece informações úteis para apoiar a tomada de decisão no " +
                    "processo de reposição de stocks.", Likert),

                new PerguntaDef("B4",
                    "Considero que o artefacto poderá contribuir para reduzir ruturas de stock e " +
                    "melhorar a disponibilidade dos produtos.", Likert),

                new PerguntaDef("B5",
                    "Considero que o artefacto poderá contribuir para reduzir os custos associados " +
                    "à gestão de inventário.", Likert),

                // A afirmacao sobre "IA e variaveis externas (sazonalidade, clima, epidemias)"
                // ERA A B6 DA V5 e SAIU na V6, por decisao de quem conduz a pesquisa: era a unica
                // que perguntava sobre coisas que o artefacto nao faz. Nao "restaure" -- a
                // ausencia e deliberada, como a da A4.
                //
                // ATENCAO AO CODIGO: daqui para baixo todo codigo mudou de dono na V6, e este e o
                // SEGUNDO remanejamento do instrumento. A afirmacao abaixo era B7 na V5 e agora e
                // B6; a de uso diario foi B7 na V2, B12 na V5 e e B11 aqui. Respostas de versoes
                // diferentes NAO podem ser agrupadas por codigo: agrupe por
                // (VersaoCatalogo, Codigo). Cada resposta guarda o retrato do enunciado exibido,
                // entao o registro individual continua correto -- o risco e so na agregacao, e ele
                // nao da erro nenhum.
                new PerguntaDef("B6",
                    "Considero que a utilização de Inteligência Artificial em conjunto com os " +
                    "dados de mercado da IQVIA pode apoiar a identificação de produtos com " +
                    "potencial de venda que ainda não fazem parte do sortimento da organização.",
                    Likert),

                new PerguntaDef("B7",
                    "Considero que a comparação entre os dados de mercado da IQVIA e os dados " +
                    "internos da organização pode ajudar a identificar produtos já " +
                    "comercializados que apresentam potencial para aumentar as vendas.", Likert),

                new PerguntaDef("B8",
                    "Considero que a integração de Inteligência Artificial, dados internos da " +
                    "organização e informações externas de mercado pode tornar as decisões de " +
                    "compra e gestão de stocks mais fundamentadas.", Likert),

                new PerguntaDef("B9",
                    "Considero que as quantidades de compra sugeridas pelo artefacto são " +
                    "adequadas para apoiar as decisões de reposição de stocks.", Likert),

                new PerguntaDef("B10",
                    "As informações apresentadas pelo artefacto permitem compreender e avaliar " +
                    "de forma clara as recomendações de compra geradas pela Inteligência " +
                    "Artificial.", Likert),

                new PerguntaDef("B11",
                    "Considero que este artefacto apresenta potencial para ser utilizado na " +
                    "operação diária da minha organização.", Likert),
            ]),
    ];

    public static IReadOnlyList<PerguntaDef> Perguntas { get; } =
        [.. Secoes.SelectMany(s => s.Perguntas)];

    public static PerguntaDef? Pergunta(string codigo) =>
        Perguntas.FirstOrDefault(p => p.Codigo == codigo);
}
