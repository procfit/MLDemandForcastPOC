using System.Data;
using System.Globalization;
using FluentResults;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Leituras curtas do PBS: o catálogo de sugestões, a contagem de uma sugestão e as
/// lojas do teste de conexão. Separado de <see cref="ExtractionService"/> porque as
/// necessidades de erro são opostas — aqui a espera longa é sempre sintoma, e
/// retentar é seguro porque nada é escrito.
/// <para>
/// As contagens de linhas e lojas são pedidas para UMA sugestão, na seleção. Pedi-las
/// para o catálogo inteiro custava 30 s por lote de 500 ids na instância real
/// (COUNT(DISTINCT FILIAL) não é coberto pelo índice de SUGESTAO_COMPRA, em 124
/// milhões de linhas), o que dava ~20 min para 12 meses — mais do que a conexão até
/// o cliente sobrevive. Ver o spec de 2026-08-04.
/// </para>
/// </summary>
internal sealed class CatalogoService(AppConfig config, ExtratorLog log)
{
    public Result<IReadOnlyList<SugestaoCatalogoCabecalho>> Carregar(
        string connectionString, DateOnly dataInicio, CancellationToken ct)
    {
        var etapa = new Etapa("catálogo de sugestões", "catalogo_sugestoes.sql");

        return ComRetentativa(() => Consultar(connectionString, etapa, TimeoutConsulta, ct,
            comando =>
            {
                comando.Parameters.Add("@dataInicio", SqlDbType.Date).Value = dataInicio.ToDateTime(TimeOnly.MinValue);
            },
            reader =>
            {
                var cabecalhos = new List<SugestaoCatalogoCabecalho>();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    cabecalhos.Add(LerCabecalho(reader));
                }
                return (IReadOnlyList<SugestaoCatalogoCabecalho>)cabecalhos;
            },
            "{{DATA_INICIO}}", "@dataInicio"), ct);
    }

    public Result<SugestaoContagem> Contar(string connectionString, long sugestaoId, CancellationToken ct)
    {
        var etapa = new Etapa("contagem da sugestão", "catalogo_sugestoes_contagens.sql");

        return ComRetentativa(() => Consultar(connectionString, etapa, TimeoutContagem, ct,
            comando =>
            {
                comando.Parameters.Add("@sugestao", SqlDbType.BigInt).Value = sugestaoId;
            },
            reader => LerContagem(sugestaoId, reader),
            "{{SUGESTOES}}", "@sugestao"), ct);
    }

    public Result<SugestaoCatalogoCabecalho> PorId(string connectionString, long sugestaoId, CancellationToken ct)
    {
        var etapa = new Etapa("cabeçalho da sugestão", "sugestao_por_id.sql");

        var lido = ComRetentativa(() => Consultar(connectionString, etapa, TimeoutConsulta, ct,
            comando =>
            {
                comando.Parameters.Add("@sugestaoId", SqlDbType.BigInt).Value = sugestaoId;
            },
            reader => reader.Read() ? LerCabecalho(reader) : null,
            "{{SUGESTAO_ID}}", "@sugestaoId"), ct);

        if (lido.IsFailed) return Result.Fail<SugestaoCatalogoCabecalho>(lido.Errors);

        return lido.Value is { } cabecalho
            ? Result.Ok(cabecalho)
            : Result.Fail<SugestaoCatalogoCabecalho>(new SugestaoNaoEncontradaErro(sugestaoId));
    }

    public Result<IReadOnlyList<LojaOption>> Lojas(string connectionString, CancellationToken ct)
    {
        var etapa = new Etapa("lojas disponíveis", "lojas_disponiveis.sql");

        return ComRetentativa(() => Consultar(connectionString, etapa, TimeoutConsulta, ct,
            _ => { },
            reader =>
            {
                var lojas = new List<LojaOption>();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    lojas.Add(new LojaOption(reader.GetInt32(0), reader.GetString(1)));
                }
                return (IReadOnlyList<LojaOption>)lojas;
            }), ct);
    }

    public Result<IReadOnlyList<LojaDaSugestao>> LojasDaSugestao(
        string connectionString, long sugestaoId, CancellationToken ct)
    {
        var etapa = new Etapa("lojas da sugestão", "lojas_da_sugestao.sql");

        var daSugestao = ComRetentativa(() => Consultar(connectionString, etapa, TimeoutContagem, ct,
            comando => comando.Parameters.Add("@sugestao", SqlDbType.BigInt).Value = sugestaoId,
            reader => (IReadOnlyList<(int, int)>)[.. LerLojasDaSugestao(reader)],
            "{{SUGESTAO}}", "@sugestao"), ct);

        if (daSugestao.IsFailed) return Result.Fail<IReadOnlyList<LojaDaSugestao>>(daSugestao.Errors);

        var cadastro = Lojas(connectionString, ct);
        if (cadastro.IsFailed) return Result.Fail<IReadOnlyList<LojaDaSugestao>>(cadastro.Errors);

        return Result.Ok(Casar(daSugestao.Value, cadastro.Value));
    }

    internal static IEnumerable<(int LojaId, int Itens)> LerLojasDaSugestao(IDataReader reader)
    {
        while (reader.Read()) yield return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Junta as lojas da sugestão com os nomes do cadastro ativo. Loja que a sugestão
    /// cita e o cadastro não tem (desativada, por exemplo) **fica** na lista: sumir
    /// esconderia do comprador uma loja que vai ser exportada se ele não disser nada.
    /// </summary>
    internal static IReadOnlyList<LojaDaSugestao> Casar(
        IReadOnlyList<(int LojaId, int Itens)> daSugestao, IReadOnlyList<LojaOption> cadastro)
    {
        var nomes = cadastro.ToDictionary(l => l.LojaId, l => l.Nome);

        // "Inativa" e não "sem cadastro": lojas_disponiveis.sql filtra ATIVO = 'S', então o
        // caso comum de uma loja não aparecer aqui é ela ter sido desativada no PBS -- não
        // deixar de existir. "Sem cadastro" diria ao comprador algo que normalmente não
        // aconteceu.
        return [.. daSugestao
            .OrderBy(l => l.LojaId)
            .Select(l => new LojaDaSugestao(
                l.LojaId,
                nomes.TryGetValue(l.LojaId, out var nome) ? nome : "(inativa ou sem cadastro)",
                l.Itens))];
    }

    /// <summary>
    /// Ordinais compartilhados por catalogo_sugestoes.sql e sugestao_por_id.sql — as
    /// duas devolvem o mesmo cabeçalho, e ler em dois lugares deixaria uma mudança de
    /// coluna aplicada só em um deles.
    /// </summary>
    internal static SugestaoCatalogoCabecalho LerCabecalho(IDataRecord registro) => new(
        registro.GetInt64(0),
        registro.IsDBNull(1) ? null : registro.GetString(1),
        registro.GetDateTime(2),
        registro.GetByte(3));

    /// <summary>
    /// Zero linhas é resposta legítima: a sugestão pode existir em SUGESTOES_COMPRAS
    /// e não ter nenhuma linha em SUGESTOES_COMPRAS_RESULTADO (id 17658 na instância
    /// real). Quem recusa a extração é LoadEscopoSugestao, não a contagem.
    /// <para>
    /// Cobertura zero na ausência de linhas é o fallback correto, e não um chute: sem item
    /// não há o que cobrir, e a janela derivada dessa cobertura sai inviável — que é
    /// exatamente o desfecho desejado.
    /// </para>
    /// </summary>
    internal static SugestaoContagem LerContagem(long sugestaoId, IDataReader reader) =>
        reader.Read()
            ? new SugestaoContagem(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3))
            : new SugestaoContagem(sugestaoId, 0, 0, 0);

    /// <summary>
    /// Filtro em memória sobre o catálogo já carregado: doze meses são ~19.500
    /// sugestões na instância real, e isso não se navega com scroll. Nenhuma ida
    /// extra ao banco.
    /// </summary>
    internal static IReadOnlyList<SugestaoCatalogoCabecalho> Filtrar(
        IReadOnlyList<SugestaoCatalogoCabecalho> catalogo, string? filtro)
    {
        if (string.IsNullOrWhiteSpace(filtro)) return catalogo;

        var termo = filtro.Trim();
        return [.. catalogo.Where(c =>
            c.SugestaoId.ToString(CultureInfo.InvariantCulture).Contains(termo, StringComparison.Ordinal)
            || (c.Descricao?.Contains(termo, StringComparison.OrdinalIgnoreCase) ?? false))];
    }

    private int TimeoutConsulta => AppConfig.Segundos(config.TimeoutConsultaSegundos, AppConfig.TimeoutConsultaPadrao);

    private int TimeoutContagem => AppConfig.Segundos(config.TimeoutContagemSegundos, AppConfig.TimeoutContagemPadrao);

    private Result<T> ComRetentativa<T>(Func<Result<T>> consulta, CancellationToken ct) =>
        Retentativa.Executar(consulta, Retentativa.TentativasPadrao, log.Escrever, Retentativa.Dormir, ct);

    /// <summary>
    /// O único ponto de tradução de exceção deste arquivo. <c>conexaoJaAberta</c> é o
    /// que separa "não consegui conectar" de "a conexão caiu no meio", que são
    /// conselhos opostos para o operador.
    /// </summary>
    private static Result<T> Consultar<T>(
        string connectionString,
        Etapa etapa,
        int timeoutSegundos,
        CancellationToken ct,
        Action<SqlCommand> parametros,
        Func<SqlDataReader, T> ler,
        string? placeholder = null,
        string? substituto = null)
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var conexaoJaAberta = false;

        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            conexaoJaAberta = true;

            var sql = SqlResources.Load(etapa.QueryFile!);
            if (placeholder is not null) sql = sql.Replace(placeholder, substituto);

            using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSegundos };
            parametros(command);
            using var cancelRegistration = ct.Register(command.Cancel);

            // Um token já cancelado dispara o callback do Register síncrono, contra um
            // Command que nem começou -- Cancel() vira no-op documentado. Sem este
            // ThrowIfCancellationRequested logo antes do ExecuteReader, a consulta
            // inteira roda até o fim como se ninguém tivesse clicado em Cancelar.
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();

            return Result.Ok(ler(reader));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TraduzirFalha<T>(ex, ct, etapa, conexaoJaAberta, cronometro.Elapsed);
        }
    }

    /// <summary>
    /// Cancelar um <c>ExecuteReader</c> sincrono chega como <see cref="SqlException"/>
    /// ("Operation cancelled by user"), nao como <see cref="OperationCanceledException"/>.
    /// Sem esta guarda o cancelamento viraria ConexaoPerdidaErro — que e transitorio, e
    /// portanto seria RETENTADO: clicar em Cancelar rodaria a consulta duas vezes mais.
    /// </summary>
    internal static Result<T> TraduzirFalha<T>(
        Exception ex, CancellationToken ct, Etapa etapa, bool conexaoJaAberta, TimeSpan duracao)
    {
        ct.ThrowIfCancellationRequested();

        return Result.Fail<T>(
            ClassificadorDeFalha.Classificar(FalhaBruta.De(ex, conexaoJaAberta), etapa, duracao));
    }
}
