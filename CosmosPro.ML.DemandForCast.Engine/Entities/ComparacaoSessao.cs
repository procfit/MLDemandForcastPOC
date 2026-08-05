namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Uma comparação entre a sugestão de compra do ERP e a que o ML faria, ancorada a
/// UMA sugestão do PBS.
/// <para>
/// Nasce sem <see cref="SugestaoId"/>: a sugestão é escolhida no Extractor, que é o
/// único com acesso ao PBS, e o ZIP declara qual foi. É assim que o ovo-e-galinha se
/// resolve — a web não pode pedir a sugestão antes de ter os dados.
/// </para>
/// </summary>
public sealed class ComparacaoSessao
{
    public Guid Id { get; set; }
    public int RedeId { get; set; }
    public string? Nome { get; set; }

    public SessaoStatus Status { get; set; }
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }

    public long? SugestaoId { get; set; }
    /// <summary>Retrato da sugestão, para o painel ser legível sem consultar o Stage.</summary>
    public string? SugestaoDescricao { get; set; }
    public DateTime? SugestaoDataHora { get; set; }
    public byte? SugestaoTipoCalculo { get; set; }

    /// <summary>
    /// Quantos SKUs da sugestão o extrator não encontrou no cadastro de produtos do PBS,
    /// copiado do manifesto do ZIP.
    ///
    /// <para>
    /// Fica na sessão porque é a única ponte entre o manifesto e o resultado: o manifesto
    /// vive no diretório temporário do import, que é apagado no fim dele, e a
    /// materialização acontece três fases depois. Sem isto o comprador descobriria a
    /// ausência como célula vazia na tabela em vez de "N itens sem cadastro".
    /// </para>
    ///
    /// <para>
    /// Anulável, e não zero por default: sessões anteriores a esta coluna e envios sem
    /// declaração não sabem quantos foram, e afirmar zero nesses casos diria "nenhum item
    /// ficou de fora" sem ter verificado.
    /// </para>
    /// </summary>
    public int? SkusSemCadastro { get; set; }

    public Guid? CargaStageId { get; set; }
    public Guid? TreinoJobId { get; set; }
    public Guid? ComparacaoPbsId { get; set; }

    /// <summary>Agregados da manchete. O detalhe por item vive em ComparacaoSessaoItens.</summary>
    public string? ResultadoJson { get; set; }

    public string? MotivoInviabilidade { get; set; }
    public string? MensagemErro { get; set; }

    private static readonly Dictionary<SessaoStatus, SessaoStatus[]> Permitidas = new()
    {
        [SessaoStatus.AguardandoDados] = [SessaoStatus.ProcessandoDados, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.ProcessandoDados] = [SessaoStatus.Treinando, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Treinando] = [SessaoStatus.Comparando, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Comparando] = [SessaoStatus.Concluida, SessaoStatus.Inviavel, SessaoStatus.Falha],
        [SessaoStatus.Concluida] = [],
        [SessaoStatus.Inviavel] = [SessaoStatus.AguardandoDados],  // reenviar outro ZIP
        [SessaoStatus.Falha] = [SessaoStatus.AguardandoDados],
    };

    public static bool PodeTransicionar(SessaoStatus de, SessaoStatus para) =>
        Permitidas.TryGetValue(de, out var destinos) && destinos.Contains(para);

    /// <summary>
    /// Fases em andamento: a sessão tem job de outra fila trabalhando por ela agora.
    /// Excluir aqui deixaria o job avançando sozinho e, na volta, um worker tentando
    /// escrever o resultado de uma sessão que não existe mais.
    /// </summary>
    private static readonly SessaoStatus[] EmAndamento =
        [SessaoStatus.ProcessandoDados, SessaoStatus.Treinando, SessaoStatus.Comparando];

    /// <summary>
    /// Se a sessão pode ser excluída. Vale fora das fases em andamento — inclusive em
    /// <see cref="SessaoStatus.AguardandoDados"/>, que é o caso de quem criou por engano e
    /// nunca enviou nada.
    ///
    /// <para>
    /// <b>Não há escape por "sessão abandonada"</b>, e isso é deliberado. Seria tentador
    /// permitir excluir uma fase parada há mais de <see cref="LimiteDeFaseSemProgresso"/>,
    /// mas isso criaria um segundo conceito de morte ao lado do que já existe: passado
    /// aquele limite, quem observa a fase encerra a sessão com um motivo, ela cai em
    /// <see cref="SessaoStatus.Falha"/> e fica excluível por esta mesma regra. Um cenário em
    /// que isso nunca acontece exige o Worker permanentemente morto — e aí nada funciona,
    /// não é a exclusão que está travada.
    /// </para>
    ///
    /// <para>
    /// Predicado sobre o status e nada mais, sem relógio: é o que permite à tela decidir se
    /// oferece o botão usando o mesmo critério do servidor, sem precisar do horário do
    /// servidor nem confiar no do navegador. A autoridade continua sendo o endpoint, que
    /// repete a condição no <c>WHERE</c> do próprio <c>DELETE</c> — desabilitar botão é
    /// cosmético, como no menu.
    /// </para>
    /// </summary>
    public static bool PodeExcluir(SessaoStatus status) => !EmAndamento.Contains(status);

    /// <summary>
    /// Tempo que uma fase pode passar sem sinal de progresso antes de ser tratada como
    /// abandonada.
    ///
    /// <para>
    /// Existe porque nenhuma das três filas tem <i>lease</i> ou <i>heartbeat</i>: elas
    /// reclamam com <c>WHERE Status = 'Pendente'</c> e gravam <c>Processando</c>, então um
    /// processo que morre no meio deixa o job em <c>Processando</c> para sempre e a sessão
    /// esperando por ele para sempre. Passado este limite, quem observa a fase termina a
    /// sessão com um motivo em vez de girar em silêncio.
    /// </para>
    ///
    /// <para>
    /// <b>Duas horas</b> porque o custo dos dois erros é assimétrico. Cortar cedo mataria um
    /// treino vivo dizendo ao comprador que "o processamento foi interrompido" — mentira, e
    /// ele reenviaria um arquivo que estava certo. Cortar tarde só adiada a mensagem de uma
    /// sessão que já está morta. Os pontos de referência que este repositório tem são os
    /// orçamentos dos testes de integração — 8 min para um treino de 2 SKUs sobre um ano
    /// (<c>TreinoCorteIntegrationTests</c>) e 15 min para o ciclo sintético inteiro
    /// (<c>SessaoOrquestracaoIntegrationTests</c>) —, e o orçamento de SKUs da sessão vai a
    /// no máximo <c>SessaoJobs.TetoDeSkusDoTreino</c>: duas horas deixam uma ordem de
    /// grandeza de folga sobre isso. Acima disso a explicação provável deixa de ser "LightGBM
    /// lento" e passa a ser "o worker caiu", porque um worker que reinicia volta em segundos
    /// e o job que ele abandonou nunca retorna a <c>Pendente</c>.
    /// </para>
    ///
    /// <para>
    /// O mesmo número decide, no envio de dados, se outra sessão da rede ainda está viva
    /// (<c>ComparacoesEndpoints</c>): sem isso, um worker que morresse trancaria a rede para
    /// sempre — o bloqueio precisa cicatrizar pelo mesmo relógio que mata a sessão.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LimiteDeFaseSemProgresso = TimeSpan.FromMinutes(120);
}

public enum SessaoStatus
{
    AguardandoDados = 0,
    ProcessandoDados = 1,
    Treinando = 2,
    Comparando = 3,
    Concluida = 4,
    Inviavel = 5,
    Falha = 6,
}
