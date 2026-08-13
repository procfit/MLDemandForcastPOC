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
/// perguntas — os códigos (A1–A3, B1–B7) são os do documento e é por eles que a análise casa as
/// respostas. A4 do documento é a única ausente, por decisão registrada onde ela caberia.
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
    /// </para>
    /// </summary>
    public const int Versao = 2;

    /// <summary>
    /// Apresentação e termo de consentimento, exibidos <b>antes</b> do primeiro passo. Não é
    /// seção do wizard porque não tem pergunta: um passo vazio quebraria a navegação e a
    /// contagem de "passo N de M".
    ///
    /// <para>
    /// <b>Isto é o que o participante consente.</b> A terceira frase afirma que não se recolhe
    /// informação identificadora — confira contra o que <c>Questionario</c> de fato grava antes
    /// de mudar qualquer um dos dois.
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

        "A participação é voluntária, anónima e confidencial. Não será recolhida qualquer " +
        "informação que permita identificar os participantes, sendo os dados utilizados " +
        "exclusivamente para fins académicos.",

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

                new PerguntaDef("B6",
                    "Considero que a utilização de Inteligência Artificial e de variáveis externas " +
                    "(por exemplo, sazonalidade, clima, epidemias e dados de mercado) representa " +
                    "uma mais-valia para melhorar a previsão da procura.", Likert),

                new PerguntaDef("B7",
                    "Considero que este artefacto apresenta potencial para ser utilizado na " +
                    "operação diária da minha organização.", Likert),
            ]),
    ];

    public static IReadOnlyList<PerguntaDef> Perguntas { get; } =
        [.. Secoes.SelectMany(s => s.Perguntas)];

    public static PerguntaDef? Pergunta(string codigo) =>
        Perguntas.FirstOrDefault(p => p.Codigo == codigo);
}
