using System.Data;
using System.Globalization;
using System.Reflection;
using FluentResults;
using Microsoft.Data.SqlClient;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// Executa as queries de extração e grava o ZIP no formato que o import do
/// projeto espera. Síncrono de propósito: roda inteiro numa thread de fundo e
/// o cancelamento é checado a cada bloco de linhas.
/// <para>
/// F14: a extração é sempre escopada a UMA sugestão de compra do PBS — lojas e
/// produtos vêm dela, não de uma seleção manual (ver <see cref="LoadEscopoSugestao"/>).
/// </para>
/// </summary>
internal sealed class ExtractionService
{
    // Vendas e estoque varrem dezenas de milhões de linhas; timeout fixo só
    // atrapalharia. O cancelamento é feito pelo usuário, via CancellationToken.
    private const int CommandTimeoutSeconds = 0;
    private const int ProgressRowInterval = 25_000;

    public Result<ExtractionResult> Run(ExtractionRequest request, IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var zipPath = string.Empty;
        var zipBytes = 0L;

        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        try
        {
            Directory.CreateDirectory(request.OutputDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            zipPath = Path.Combine(request.OutputDirectory, $"extracao-pbs_{stamp}.zip");

            using (var output = File.Create(zipPath))
            using (var zip = new CsvZipWriter(output))
            using (var connection = new SqlConnection(request.ConnectionString))
            {
                connection.Open();

                var (lojaIds, skusDaSugestao) = LoadEscopoSugestao(connection, request.SugestaoId, ct);
                AvisarDivergenciaEmpresaFilial(connection, request.SugestaoId, warnings, ct);

                var total = StageContract.WriteOrder.Length;

                rows[StageContract.Lojas] = CopyQuery(connection, "lojas.sql", StageContract.Lojas, zip, lojaIds, request.DataInicial, request.DataFinal, 1, total, progress, ct, InspectLoja(warnings));
                var (produtosRowCount, skusFabricados) = CopyProdutosGarantindoUniao(connection, zip, skusDaSugestao, 2, total, progress, ct, warnings);
                rows[StageContract.Produtos] = produtosRowCount;
                rows[StageContract.Vendas] = CopyQuery(connection, "vendas.sql", StageContract.Vendas, zip, lojaIds, request.DataInicial, request.DataFinal, 3, total, progress, ct);
                rows[StageContract.EstoquesDiarios] = CopyEstoques(connection, zip, lojaIds, request.DataInicial, request.DataFinal, 4, total, progress, ct);
                rows[StageContract.Compras] = CopyQuery(connection, "compras.sql", StageContract.Compras, zip, lojaIds, request.DataInicial, request.DataFinal, 5, total, progress, ct);
                rows[StageContract.Promocoes] = CopyQuery(connection, "promocoes.sql", StageContract.Promocoes, zip, lojaIds, request.DataInicial, request.DataFinal, 6, total, progress, ct);

                // Sem fonte no ERP: o IQVIA é dado de mercado externo. O arquivo
                // precisa existir porque o validador do import exige os sete CSVs.
                using (zip.CreateEntry(StageContract.MercadoIqvia, StageContract.Headers[StageContract.MercadoIqvia])) { }
                rows[StageContract.MercadoIqvia] = 0;
                progress.Report(new ExtractionProgress(StageContract.MercadoIqvia, 7, total, 0));

                var cabecalho = CopySugestaoHeader(connection, zip, request.SugestaoId, 8, total, progress, ct)
                    ?? throw new FalhaDeDominioException(new SugestaoNaoEncontradaErro(request.SugestaoId));
                rows[StageContract.SugestoesCompra] = 1;
                rows[StageContract.SugestoesCompraItens] = CopyQuery(connection, "sugestoes_compra_itens.sql", StageContract.SugestoesCompraItens, zip, request.SugestaoId, 9, total, progress, ct);

                zip.WriteText(ZipManifest.EntryName, ZipManifest.Escrever(new ZipManifest(
                    request.SugestaoId,
                    cabecalho.Descricao,
                    cabecalho.DataHora,
                    cabecalho.TipoCalculo,
                    request.DataInicial,
                    request.DataFinal,
                    ZipManifest.VersaoAtual(),
                    skusFabricados)));
            }

            // Dentro do try de propósito: uma falha aqui (antivírus travou o arquivo
            // recém-fechado, por exemplo) é tão IOException quanto qualquer escrita
            // deste método, e tem que passar pelo mesmo ponto único de tradução —
            // não escapar crua e quebrar o contrato de que Run só devolve Result.
            zipBytes = new FileInfo(zipPath).Length;
        }
        catch (OperationCanceledException)
        {
            // Cancelamento não é falha, mas o ZIP parcial tem de morrer igual: ele
            // passa na validação de header do import e entraria no Stage como se
            // estivesse completo. Antes da fronteira Result isto vinha de um catch
            // pelado; os dois catches abaixo não o pegam de propósito, porque
            // cancelamento sobe como exceção.
            TryDelete(zipPath);
            throw;
        }
        catch (FalhaDeDominioException falha)
        {
            // ZIP parcial é pior que nenhum: ele passa na validação de header do
            // import e entraria no Stage como se estivesse completo.
            TryDelete(zipPath);
            return Result.Fail<ExtractionResult>(falha.Erro);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(zipPath);

            // Cancelar um ExecuteReader síncrono chega aqui como SqlException, não
            // como OperationCanceledException — sem esta guarda o cancelamento do
            // usuário seria classificado como ConexaoPerdidaErro (transitório).
            ct.ThrowIfCancellationRequested();

            var etapa = ex is EtapaFalhouException etapaFalhou ? etapaFalhou.Etapa : new Etapa("extração", null);
            return Result.Fail<ExtractionResult>(
                ClassificadorDeFalha.Classificar(FalhaBruta.De(ex, conexaoJaAberta: true), etapa, cronometro.Elapsed));
        }

        if (rows[StageContract.Vendas] == 0)
        {
            warnings.Add("Nenhuma venda na janela de dados derivada da sugestão — confira se a sugestão escolhida faz sentido.");
        }
        if (rows[StageContract.EstoquesDiarios] == 0)
        {
            warnings.Add("Nenhum estoque no período — o histórico de ESTOQUE_LANCAMENTOS costuma cobrir apenas os últimos meses.");
        }

        return Result.Ok(new ExtractionResult(zipPath, zipBytes, rows, warnings));
    }

    /// <summary>
    /// Lojas e SKUs citados pela sugestão, para escopar as demais tabelas e
    /// garantir a união dos produtos (ver <see cref="CopyProdutosGarantindoUniao"/>).
    /// Uma query só, mais barata que bufferizar as 17 colunas dos itens.
    /// </summary>
    private static (IReadOnlyList<int> LojaIds, IReadOnlySet<string> Skus) LoadEscopoSugestao(
        SqlConnection connection, long sugestaoId, CancellationToken ct)
    {
        var (lojaIds, skus) = Step(new Etapa("escopo da sugestão", "escopo_sugestao.sql"), () =>
        {
            using var command = CreateSugestaoCommand(connection, SqlResources.Load("escopo_sugestao.sql"), sugestaoId);
            using var cancelRegistration = ct.Register(command.Cancel);
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();

            var ids = new HashSet<int>();
            var achados = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                ids.Add(reader.GetInt32(0));
                achados.Add(reader.GetString(1));
            }
            return (ids, achados);
        });

        if (lojaIds.Count == 0)
        {
            throw new FalhaDeDominioException(new SugestaoSemItensErro(sugestaoId));
        }

        return ([.. lojaIds.Order()], skus);
    }

    /// <summary>
    /// Conta linhas com EMPRESA != FILIAL (ver Queries/sugestoes_compra_diagnostico.sql):
    /// sinal de que LojaId = FILIAL pode não valer nesta instalação do PBS.
    /// </summary>
    private static void AvisarDivergenciaEmpresaFilial(SqlConnection connection, long sugestaoId, List<string> warnings, CancellationToken ct) =>
        Step(new Etapa("diagnóstico EMPRESA vs FILIAL", "sugestoes_compra_diagnostico.sql"), () =>
        {
            using var command = CreateSugestaoCommand(connection, SqlResources.Load("sugestoes_compra_diagnostico.sql"), sugestaoId);
            using var cancelRegistration = ct.Register(command.Cancel);
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
            {
                var divergencias = reader.GetInt32(0);
                if (divergencias > 0)
                {
                    warnings.Add(
                        $"{divergencias} linha(s) desta sugestão têm EMPRESA diferente de FILIAL — " +
                        "a suposição LojaId = FILIAL pode estar incorreta nesta instalação do PBS.");
                }
            }
        });

    /// <summary>Escreve sugestoes_compra.csv (uma linha) e devolve os campos que o manifesto precisa.</summary>
    private static SugestaoCabecalho? CopySugestaoHeader(
        SqlConnection connection, CsvZipWriter zip, long sugestaoId,
        int fileIndex, int fileCount, IProgress<ExtractionProgress> progress, CancellationToken ct) =>
        Step(new Etapa(StageContract.SugestoesCompra, "sugestoes_compra.sql"), () =>
        {
            var header = StageContract.Headers[StageContract.SugestoesCompra];
            using var entry = zip.CreateEntry(StageContract.SugestoesCompra, header);
            using var command = CreateSugestaoCommand(connection, SqlResources.Load("sugestoes_compra.sql"), sugestaoId);
            using var cancelRegistration = ct.Register(command.Cancel);
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();

            EnsureShape(reader, header, StageContract.SugestoesCompra);

            SugestaoCabecalho? cabecalho = null;
            if (reader.Read())
            {
                // Ordinais na ordem de StageContract.Headers[SugestoesCompra]:
                // 0 SugestaoId, 1 Descricao, 2 DataHora, 3 TipoCalculo.
                cabecalho = new SugestaoCabecalho(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetDateTime(2),
                    reader.GetByte(3));
                entry.WriteRow(reader);
            }

            progress.Report(new ExtractionProgress(StageContract.SugestoesCompra, fileIndex, fileCount, entry.RowCount));
            return cabecalho;
        });

    private sealed record SugestaoCabecalho(string? Descricao, DateTime DataHora, byte TipoCalculo);

    /// <summary>
    /// Exporta o mestre de produtos inteiro (produtos.sql não filtra por loja) e
    /// preenche com placeholders os SKUs citados pela sugestão que não estejam
    /// cadastrados em PRODUTOS. Sem isso o SqlBulkCopy do Worker estoura violação
    /// de FK composta (RedeId, Sku) — ver comentário em SugestoesCompraItens.sql.
    /// </summary>
    private static (long RowCount, int SkusFabricados) CopyProdutosGarantindoUniao(
        SqlConnection connection, CsvZipWriter zip, IReadOnlySet<string> skusDaSugestao,
        int fileIndex, int fileCount, IProgress<ExtractionProgress> progress, CancellationToken ct,
        List<string> warnings) =>
        Step(new Etapa(StageContract.Produtos, "produtos.sql"), () =>
        {
            var header = StageContract.Headers[StageContract.Produtos];
            using var entry = zip.CreateEntry(StageContract.Produtos, header);
            using var command = new SqlCommand(SqlResources.Load("produtos.sql"), connection) { CommandTimeout = CommandTimeoutSeconds };
            using var cancelRegistration = ct.Register(command.Cancel);
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();

            EnsureShape(reader, header, StageContract.Produtos);

            var vistos = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                vistos.Add(reader.GetString(0)); // Sku é a primeira coluna do header
                entry.WriteRow(reader);
                if (entry.RowCount % ProgressRowInterval == 0)
                {
                    progress.Report(new ExtractionProgress(StageContract.Produtos, fileIndex, fileCount, entry.RowCount));
                }
            }

            var faltantes = SkusSemCadastro(skusDaSugestao, vistos);
            if (faltantes.Count > 0)
            {
                foreach (var sku in faltantes)
                {
                    // Categoria com sentinela (não NULL) para a fabricação ficar visível e
                    // filtrável nos drill-downs e na tabela comparativa por item que o
                    // comprador usa — sem isto o SKU órfão passa por dado real perdido no
                    // bucket NULL de categoria.
                    entry.WriteRow(sku, $"(SKU {sku} não encontrado no cadastro do PBS)",
                        "(não cadastrado)", null, null, null, null, null, null, null, null, false);
                }
                warnings.Add(
                    $"{faltantes.Count} SKU(s) citados pela sugestão não estão cadastrados em PRODUTOS no PBS; " +
                    $"foram incluídos em produtos.csv com nome genérico para o import não travar na FK: {string.Join(", ", faltantes)}.");
            }

            progress.Report(new ExtractionProgress(StageContract.Produtos, fileIndex, fileCount, entry.RowCount));
            return (entry.RowCount, faltantes.Count);
        });

    /// <summary>
    /// SKUs citados pela sugestão que não apareceram no cadastro (PRODUTOS) e por
    /// isso precisam de linha placeholder em produtos.csv — sem ela o SqlBulkCopy do
    /// Worker viola a FK composta (RedeId, Sku) ao inserir SugestoesCompraItens.
    /// <para>
    /// Comparação ORDINAL, deliberadamente sem normalização (zeros à esquerda,
    /// caixa, etc). produtos.sql e a query de escopo da sugestão convertem PRODUTO
    /// com o mesmo <c>CONVERT(varchar(30), ...)</c>, então na prática o texto já sai
    /// igual — mas o motivo de exigir exatidão aqui não é essa coincidência, é a
    /// própria FK: o Worker compara a string literal gravada em
    /// sugestoes_compra_itens.csv contra a string literal gravada em produtos.csv.
    /// Se "0123" e "123" fossem tratados como o mesmo SKU e o placeholder para
    /// "0123" fosse pulado por já existir "123", a FK quebraria no import — o SQL
    /// Server não considera essas strings iguais. Tratar os dois como SKUs
    /// distintos não é uma limitação da comparação, é o comportamento exigido pelo
    /// Stage.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> SkusSemCadastro(IEnumerable<string> skusDaSugestao, IEnumerable<string> skusCadastrados)
    {
        var cadastrados = new HashSet<string>(skusCadastrados, StringComparer.Ordinal);
        return skusDaSugestao
            .Where(sku => !cadastrados.Contains(sku))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static long CopyQuery(
        SqlConnection connection,
        string queryFile,
        string entryName,
        CsvZipWriter zip,
        IReadOnlyList<int> lojaIds,
        DateOnly dataInicial,
        DateOnly dataFinal,
        int fileIndex,
        int fileCount,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct,
        Action<IDataRecord>? inspect = null)
    {
        using var command = CreateJanelaCommand(connection, SqlResources.Load(queryFile), lojaIds, dataInicial, dataFinal);
        return CopyQueryCore(entryName, queryFile, zip, command, fileIndex, fileCount, progress, ct, inspect);
    }

    private static long CopyQuery(
        SqlConnection connection,
        string queryFile,
        string entryName,
        CsvZipWriter zip,
        long sugestaoId,
        int fileIndex,
        int fileCount,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
    {
        using var command = CreateSugestaoCommand(connection, SqlResources.Load(queryFile), sugestaoId);
        return CopyQueryCore(entryName, queryFile, zip, command, fileIndex, fileCount, progress, ct, inspect: null);
    }

    private static long CopyQueryCore(
        string entryName,
        string queryFile,
        CsvZipWriter zip,
        SqlCommand command,
        int fileIndex,
        int fileCount,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct,
        Action<IDataRecord>? inspect) =>
        Step(new Etapa(entryName, queryFile), () =>
        {
            var header = StageContract.Headers[entryName];
            using var entry = zip.CreateEntry(entryName, header);
            using var cancelRegistration = ct.Register(command.Cancel);
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();

            EnsureShape(reader, header, entryName);

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                inspect?.Invoke(reader);
                entry.WriteRow(reader);
                if (entry.RowCount % ProgressRowInterval == 0)
                {
                    progress.Report(new ExtractionProgress(entryName, fileIndex, fileCount, entry.RowCount));
                }
            }

            progress.Report(new ExtractionProgress(entryName, fileIndex, fileCount, entry.RowCount));
            return entry.RowCount;
        });

    private static long CopyEstoques(
        SqlConnection connection,
        CsvZipWriter zip,
        IReadOnlyList<int> lojaIds,
        DateOnly dataInicial,
        DateOnly dataFinal,
        int fileIndex,
        int fileCount,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct) =>
        Step(new Etapa(StageContract.EstoquesDiarios, "estoques_movimentos.sql"), () =>
        {
            var header = StageContract.Headers[StageContract.EstoquesDiarios];
            using var entry = zip.CreateEntry(StageContract.EstoquesDiarios, header);
            using var command = CreateJanelaCommand(connection, SqlResources.Load("estoques_movimentos.sql"), lojaIds, dataInicial, dataFinal);
            using var cancelRegistration = ct.Register(command.Cancel);
            ct.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();

            foreach (var linha in StockCarryForward.Densify(ReadMovements(reader, ct), dataFinal))
            {
                ct.ThrowIfCancellationRequested();
                entry.WriteRow(linha.Data, linha.LojaId, linha.Sku, linha.Saldo);
                if (entry.RowCount % ProgressRowInterval == 0)
                {
                    progress.Report(new ExtractionProgress(StageContract.EstoquesDiarios, fileIndex, fileCount, entry.RowCount));
                }
            }

            progress.Report(new ExtractionProgress(StageContract.EstoquesDiarios, fileIndex, fileCount, entry.RowCount));
            return entry.RowCount;
        });

    private static IEnumerable<StockMovement> ReadMovements(SqlDataReader reader, CancellationToken ct)
    {
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            // Cinto de segurança: a query já descarta saldo nulo, mas sem isto um
            // NULL derrubaria a extração inteira no meio do streaming.
            if (reader.IsDBNull(3)) continue;

            yield return new StockMovement(
                reader.GetInt32(0),
                reader.GetString(1),
                DateOnly.FromDateTime(reader.GetDateTime(2)),
                reader.GetDecimal(3));
        }
    }

    private static Action<IDataRecord> InspectLoja(List<string> warnings) => record =>
    {
        if (record.GetValue(2) as string == "NI")
        {
            warnings.Add($"Loja {record.GetValue(0)} está sem endereço cadastrado — UF/Cidade saíram como 'NI'.");
        }
    };

    private static SqlCommand CreateJanelaCommand(
        SqlConnection connection, string sql, IReadOnlyList<int> lojaIds, DateOnly dataInicial, DateOnly dataFinal)
    {
        var placeholders = lojaIds
            .Select((_, i) => "@loja" + i.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var command = new SqlCommand(sql.Replace("{{LOJAS}}", string.Join(',', placeholders)), connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };

        for (var i = 0; i < lojaIds.Count; i++)
        {
            command.Parameters.Add(placeholders[i], SqlDbType.Int).Value = lojaIds[i];
        }
        command.Parameters.Add("@dataInicial", SqlDbType.Date).Value = dataInicial.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@dataFinal", SqlDbType.Date).Value = dataFinal.ToDateTime(TimeOnly.MinValue);
        return command;
    }

    /// <summary>
    /// {{SUGESTAO}} vira um parâmetro real (@sugestao), não concatenação de texto —
    /// o valor nasce de uma lista que o próprio PBS devolveu (catalogo_sugestoes.sql),
    /// mas usar parâmetro custa nada e elimina qualquer risco de injeção.
    /// </summary>
    private static SqlCommand CreateSugestaoCommand(SqlConnection connection, string sql, long sugestaoId)
    {
        var command = new SqlCommand(sql.Replace("{{SUGESTAO}}", "@sugestao"), connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        command.Parameters.Add("@sugestao", SqlDbType.BigInt).Value = sugestaoId;
        return command;
    }

    /// <summary>
    /// Marca a etapa em curso para que uma falha diga onde quebrou. Sem isto o
    /// operador recebe só a mensagem do driver — "Unable to cast object of type
    /// 'System.Decimal' to type 'System.Int32'" — que não nomeia query nem coluna,
    /// e a única saída é adivinhar entre uma dúzia de consultas.
    /// <para>
    /// Cancelamento passa sem embrulho: quem cancelou não teve falha, e o modo
    /// linha de comando distingue os dois pelo código de saída.
    /// </para>
    /// </summary>
    internal static T Step<T>(Etapa etapa, Func<T> acao)
    {
        try
        {
            return acao();
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                      and not EtapaFalhouException
                                      and not FalhaDeDominioException)
        {
            throw new EtapaFalhouException(etapa, ex);
        }
    }

    internal static void Step(Etapa etapa, Action acao) =>
        Step<object?>(etapa, () =>
        {
            acao();
            return null;
        });

    /// <summary>
    /// Falha cedo se a query divergir do header do Stage — sem isso um erro de
    /// ordem de coluna só apareceria no import, com o dado já embaralhado.
    /// </summary>
    private static void EnsureShape(IDataReader reader, IReadOnlyList<string> header, string entryName)
    {
        if (reader.FieldCount != header.Count)
        {
            throw new FalhaDeDominioException(new ContratoErro(entryName,
                $"query devolveu {reader.FieldCount} colunas, esperado {header.Count}"));
        }

        for (var i = 0; i < header.Count; i++)
        {
            if (!string.Equals(reader.GetName(i), header[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new FalhaDeDominioException(new ContratoErro(entryName,
                    $"coluna {i + 1} é '{reader.GetName(i)}', esperado '{header[i]}'"));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Arquivo travado por antivírus/indexador: o erro original é o que importa.
        }
    }
}

/// <summary>
/// Falha dentro de uma etapa nomeada. Nunca sai desta classe: o único catch de
/// <see cref="ExtractionService.Run"/> a traduz em <c>Result.Fail</c>. Existe porque
/// a extração tem uma dúzia de etapas encadeadas, e devolver Result de cada método
/// privado espalharia verificação sem acrescentar informação.
/// </summary>
internal sealed class EtapaFalhouException(Etapa etapa, Exception causa)
    : InvalidOperationException($"Falha na etapa '{etapa}': {causa.Message}", causa)
{
    public Etapa Etapa { get; } = etapa;
}

/// <summary>Falha de domínio já classificada, a caminho do catch único do Run.</summary>
internal sealed class FalhaDeDominioException(ExtratorErro erro)
    : InvalidOperationException(erro.Message)
{
    public ExtratorErro Erro { get; } = erro;
}

internal static class SqlResources
{
    public static string Load(string fileName)
    {
        var resourceName = $"CosmosPro.ML.DemandForCast.Extractor.Queries.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Query embarcada não encontrada: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
