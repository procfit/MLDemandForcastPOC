using System.Data;
using CosmosPro.ML.DemandForCast.Engine;
using CosmosPro.ML.DemandForCast.Engine.Mercado;
using CosmosPro.ML.DemandForCast.Worker.Training;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.Worker.Mercado;

/// <summary>Sinal de mercado de um item da sessão, pronto para gravar.</summary>
internal sealed record SinalDoItem(
    DateOnly Mes,
    string Brick,
    decimal UnidadesRede,
    decimal UnidadesConcorrentes,
    // Valor ao consumidor sob a metodologia da IQVIA, somado no mesmo recorte das unidades.
    //
    // CORRECAO DE UM RACIOCINIO MEU QUE A MEDICAO DESMENTIU. O comentario anterior dizia que
    // comparar os dois lados da IQVIA entre si era legitimo e compara-los com preco praticado
    // nao era. E o contrario: `valor / unidades` devolve o preco de REFERENCIA que a IQVIA
    // normaliza entre os participantes, entao os dois lados dao SEMPRE o mesmo numero -- 37.410
    // pares medidos em agosto, zero diferenca, e foi o patrocinador quem viu na tela em
    // 09/09/2026. Comparar um com o outro nao e legitimo, e uma tautologia.
    //
    // A comparacao util e contra `PrecoPraticadoRede`, abaixo. Ela mistura duas naturezas de
    // numero -- referencia normalizada contra preco de balcao -- e essa ressalva tem de aparecer
    // na tela; mas ela pelo menos INFORMA, e o patrocinador aprovou seguir por ela (Opcao B).
    decimal ValorRede,
    decimal ValorConcorrentes,
    // Preco medio que a REDE de fato praticou no mes comparado, ponderado pela quantidade:
    // SUM(ValorTotal) / SUM(Quantidade) de `Stage.Vendas`. Nulo quando a rede nao vendeu o item
    // naquele mes -- e nulo NAO e zero: zero afirmaria que ela deu o produto.
    decimal? PrecoPraticadoRede,
    decimal Indice,
    int? DiasSemEstoque,
    string Alerta);

/// <summary>
/// Fecha as duas pontes entre a sessão e o dado de mercado da IQVIA: <b>loja → brick</b>
/// por CNPJ e <b>SKU → EAN</b> por código de barras. Item que não atravessa alguma delas
/// simplesmente não entra no dicionário, e quem materializa grava nulo nas sete colunas.
///
/// <para>
/// <b>A normalização do EAN não é detalhe.</b> O PBS grava o código com 14 caracteres e
/// zero à esquerda (<c>07896094928060</c>); a IQVIA grava 13 (<c>7891721201806</c>).
/// Comparação exata casa <b>zero</b> — medido em 2026-08-30 contra o cadastro real da
/// Retiro, 28.987 produtos com EAN. Sem tirar os zeros dos dois lados, este loader
/// devolveria dicionário vazio em toda sessão, sem erro nenhum, sem log e sem nada na tela
/// denunciando.
/// </para>
///
/// <para>
/// <b>EAN que a IQVIA não reportou não é falha de join.</b> A lista de EANs do relatório é
/// <i>o que teve movimento nos bricks pedidos</i>, não um catálogo. Ausência num brick e mês
/// cobertos significa que o mercado daqueles bairros não vendeu o item — informação, e não
/// erro. Na medição de 2026-08-30, 21 dos 43 SKUs de uma sugestão real casaram.
/// </para>
/// </summary>
internal sealed class MercadoSinalLoader(
    string stageConnectionString,
    IServiceProvider services,
    // ILogger e não ILogger<T>: quem constrói é o materializador, que passa o próprio
    // logger. Resolver um tipado exigiria um scope só para isso.
    ILogger logger)
{
    /// <summary>Bandeira reservada da IQVIA para o agregado anônimo de concorrentes.</summary>
    private const string BandeiraConcorrentes = "CONCORRENTES";

    /// <summary>CNPJ do agregado de concorrentes no painel; nunca casa com loja da rede.</summary>
    private const string CnpjAgregado = "00000000000000";

    /// <param name="diaDaSugestao">Dia da sugestão do ERP. Define o mês de corte.</param>
    public async Task<IReadOnlyDictionary<(int LojaId, string Sku), SinalDoItem>> CarregarAsync(
        int redeId,
        DateOnly diaDaSugestao,
        IReadOnlyCollection<(int LojaId, string Sku)> itens,
        CancellationToken ct)
    {
        var vazio = new Dictionary<(int LojaId, string Sku), SinalDoItem>();
        if (itens.Count == 0) return vazio;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EngineDbContext>();

        // Meses cobertos, pela mesma definição do endpoint de cobertura
        // (MercadoEndpoints.CoberturaQuery): um (mês, brick) está coberto quando existe
        // observação não-zerada nele. Célula zerada não gera linha, então esta é a única
        // fonte que separa "vendeu zero" de "nunca foi enviado".
        var mesesCobertos = await db.MercadoObservacoes.AsNoTracking()
            .Where(o => o.RedeId == redeId)
            .Select(o => o.Mes)
            .Distinct()
            .ToListAsync(ct);

        if (MercadoMesResolver.Resolver(mesesCobertos, diaDaSugestao) is not { } mes)
        {
            logger.LogInformation(
                "Rede {RedeId}: nenhum mês de mercado coberto antes de {Mes:yyyy-MM}; " +
                "a sessão fica sem sinal de mercado.",
                redeId, diaDaSugestao.ToDateTime(TimeOnly.MinValue));
            return vazio;
        }

        // Painel de PDVs: CNPJ -> brick. O agregado de concorrentes fica fora, porque ele
        // não é loja e nunca casaria com o cadastro.
        var brickPorCnpj = await db.MercadoBrickPdvs.AsNoTracking()
            .Where(p => p.RedeId == redeId && p.Cnpj != CnpjAgregado)
            .Select(p => new { p.Cnpj, p.Brick })
            .ToDictionaryAsync(p => p.Cnpj, p => p.Brick, ct);

        if (brickPorCnpj.Count == 0)
        {
            logger.LogInformation(
                "Rede {RedeId}: painel de PDVs da IQVIA vazio; sem ponte loja → brick.", redeId);
            return vazio;
        }

        // --- lado do Stage: as duas colunas que fecham as pontes ------------------------
        await using var conn = new SqlConnection(stageConnectionString);
        await conn.OpenAsync(ct);

        var brickPorLoja = await BrickPorLojaAsync(conn, redeId, brickPorCnpj, ct);
        if (brickPorLoja.Count == 0)
        {
            logger.LogInformation(
                "Rede {RedeId}: nenhuma loja com CNPJ que exista no painel da IQVIA. " +
                "ZIP de extrator anterior à F16 não traz CNPJ.", redeId);
            return vazio;
        }

        var escopo = await EscopoDeSkus.MaterializarAsync(
            conn, itens.Select(i => i.Sku), ct);

        var eanPorSku = await EanPorSkuAsync(conn, redeId, escopo, ct);
        if (eanPorSku.Count == 0)
        {
            logger.LogInformation("Rede {RedeId}: nenhum SKU da sugestão tem EAN no cadastro.", redeId);
            return vazio;
        }

        var bricks = brickPorLoja.Values.ToHashSet(StringComparer.Ordinal);

        // --- medidas da IQVIA no mês escolhido -----------------------------------------
        // Puxa o mês e os bricks inteiros, e agrega em memória. Filtrar por EAN no servidor
        // exigiria um IN com um parâmetro por EAN, e o SQL Server aceita 2100 por comando --
        // a mesma armadilha que EscopoDeSkus existe para evitar. E o total agregado do brick
        // precisa de TODOS os EANs, não só dos itens da sugestão.
        var observacoes = await db.MercadoObservacoes.AsNoTracking()
            .Where(o => o.RedeId == redeId && o.Mes == mes && bricks.Contains(o.Brick))
            .Select(o => new { o.Brick, o.Bandeira, o.Ean, o.Unidades, o.ValorCpp })
            .ToListAsync(ct);

        var porBrickEan = new Dictionary<
            (string Brick, string Ean),
            (decimal Rede, decimal Conc, decimal ValorRede, decimal ValorConc)>();
        var totalPorBrick = new Dictionary<string, (decimal Rede, decimal Total)>();

        foreach (var o in observacoes)
        {
            // Qualquer bandeira diferente de CONCORRENTES é própria. É assim que FARMA ONE
            // entraria como rede se a IQVIA passar a publicá-la como bandeira separada --
            // sem mudança de código aqui.
            var ehConcorrente = string.Equals(o.Bandeira, BandeiraConcorrentes, StringComparison.OrdinalIgnoreCase);

            var totais = totalPorBrick.GetValueOrDefault(o.Brick);
            totalPorBrick[o.Brick] = (
                totais.Rede + (ehConcorrente ? 0m : o.Unidades),
                totais.Total + o.Unidades);

            if (Ean.Normalizar(o.Ean) is not { } ean) continue;

            var chave = (o.Brick, ean);
            var atual = porBrickEan.GetValueOrDefault(chave);
            porBrickEan[chave] = ehConcorrente
                ? (atual.Rede, atual.Conc + o.Unidades, atual.ValorRede, atual.ValorConc + o.ValorCpp)
                : (atual.Rede + o.Unidades, atual.Conc, atual.ValorRede + o.ValorCpp, atual.ValorConc);
        }

        var fatiaPorBrick = totalPorBrick.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Total > 0m ? kv.Value.Rede / kv.Value.Total : 0m,
            StringComparer.Ordinal);

        // --- ruptura no mês comparado (regra B3) ---------------------------------------
        var rupturas = await RupturaNoMesAsync(conn, redeId, mes, escopo, ct);

        // --- preço praticado pela rede no MESMO mês (Opção B) --------------------------
        // O mesmo mês do preço de referência de propósito: comparar o praticado de um mês com a
        // referência de outro mediria a passagem do tempo, não o posicionamento de preço.
        var precosPraticados = await PrecoPraticadoNoMesAsync(conn, redeId, mes, escopo, ct);

        // --- montagem ------------------------------------------------------------------
        var sinais = new Dictionary<(int LojaId, string Sku), SinalDoItem>();

        foreach (var (lojaId, sku) in itens)
        {
            if (!brickPorLoja.TryGetValue(lojaId, out var brick)) continue;
            if (!eanPorSku.TryGetValue(sku, out var ean)) continue;
            if (!porBrickEan.TryGetValue((brick, ean), out var medida)) continue;
            if (!fatiaPorBrick.TryGetValue(brick, out var fatiaAgregada)) continue;

            var diasSemEstoque = rupturas is null
                ? null
                : rupturas.TryGetValue((lojaId, sku), out var d) ? d : (int?)null;

            var precoPraticado = precosPraticados.TryGetValue((lojaId, sku), out var pp)
                ? pp
                : (decimal?)null;

            var calculado = MercadoAlertaCalculador.Calcular(new SinalBruto(
                UnidadesRede: medida.Rede,
                UnidadesConcorrentes: medida.Conc,
                FatiaAgregadaDaRede: fatiaAgregada,
                DiasSemEstoque: diasSemEstoque));

            if (calculado is not { } c) continue;

            sinais[(lojaId, sku)] = new SinalDoItem(
                mes, brick, medida.Rede, medida.Conc,
                medida.ValorRede, medida.ValorConc,
                precoPraticado,
                c.Indice, diasSemEstoque, c.Alerta);
        }

        logger.LogInformation(
            "Rede {RedeId}: sinal de mercado de {Mes:yyyy-MM} para {ComSinal} de {Total} item(ns) " +
            "({Lojas} loja(s) com brick, {Skus} SKU(s) com EAN).",
            redeId, mes.ToDateTime(TimeOnly.MinValue), sinais.Count, itens.Count,
            brickPorLoja.Count, eanPorSku.Count);

        return sinais;
    }

    /// <summary>
    /// <c>LojaId → Brick</c>, cruzando <c>Stage.Lojas.Cnpj</c> com o painel da IQVIA. Loja
    /// sem CNPJ (ZIP anterior à F16, ou cadastro do PBS sem CGC) fica fora, e o item dela
    /// recebe nulo nas colunas de mercado.
    /// </summary>
    private static async Task<Dictionary<int, string>> BrickPorLojaAsync(
        SqlConnection conn, int redeId, Dictionary<string, string> brickPorCnpj, CancellationToken ct)
    {
        var mapa = new Dictionary<int, string>();

        await using var cmd = new SqlCommand(
            "SELECT LojaId, Cnpj FROM dbo.Lojas WHERE RedeId = @rede AND Cnpj IS NOT NULL;", conn);
        cmd.Parameters.AddWithValue("@rede", redeId);
        cmd.CommandTimeout = 120;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var cnpj = rd.GetString(1).Trim();
            if (brickPorCnpj.TryGetValue(cnpj, out var brick))
            {
                mapa[rd.GetInt32(0)] = brick;
            }
        }

        return mapa;
    }

    /// <summary>
    /// <c>Sku → EAN normalizado</c>, escopado aos SKUs da sugestão por tabela temporária.
    /// SKU sem EAN no cadastro fica fora.
    /// </summary>
    private static async Task<Dictionary<string, string>> EanPorSkuAsync(
        SqlConnection conn, int redeId, EscopoDeSkus escopo, CancellationToken ct)
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand($"""
            SELECT p.Sku, p.Ean
            FROM dbo.Produtos p
            {escopo.Join("p")}
            WHERE p.RedeId = @rede AND p.Ean IS NOT NULL AND LTRIM(RTRIM(p.Ean)) <> '';
            """, conn);
        cmd.Parameters.AddWithValue("@rede", redeId);
        cmd.CommandTimeout = 120;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            if (Ean.Normalizar(rd.GetString(1)) is { } ean)
            {
                mapa[rd.GetString(0)] = ean;
            }
        }

        return mapa;
    }

    /// <summary>
    /// Dias sem estoque por <c>(loja, sku)</c> dentro do mês comparado.
    /// </summary>
    /// <returns>
    /// <c>null</c> quando o mês comparado não cabe inteiro no histórico de estoque
    /// importado — não há snapshot para contar, e zero afirmaria que havia estoque todos os
    /// dias. Dicionário sem a chave significa o mesmo para aquele par: <c>NaoApurado</c>,
    /// não <c>SemCausa</c>.
    /// </returns>
    private static async Task<Dictionary<(int LojaId, string Sku), int>?> RupturaNoMesAsync(
        SqlConnection conn, int redeId, DateOnly mes, EscopoDeSkus escopo, CancellationToken ct)
    {
        var fim = mes.AddMonths(1).AddDays(-1);

        // O mês inteiro tem de caber no histórico. Mês parcialmente coberto subcontaria os
        // dias sem estoque e transformaria NaoApurado em SemCausa por acidente.
        //
        // A COBERTURA É MEDIDA NO PRÓPRIO ESTOQUE, e isso é o conserto de um defeito que
        // zerava a regra B3 inteira. Antes o método recebia um `janelaInicio` documentado
        // como "primeiro dia do histórico importado", mas quem chamava passava
        // `ComparacaoPbs.JanelaInicio` -- que `SessaoJobs.Comparacao` grava como o DIA DA
        // SUGESTÃO, um significado diferente com o mesmo nome. Como `MercadoMesResolver`
        // garante que o mês comparado é estritamente anterior ao mês da sugestão, a guarda
        // `mes < janelaInicio` era verdadeira SEMPRE: a ruptura saía nula para todo item de
        // toda sessão, e a tela mostrava "estoque não apurado" em 100% das linhas (8.221
        // itens na execução que o patrocinador reportou). Um dado ausente parecia dado
        // faltando na extração, e não código.
        //
        // Medir aqui também é mais verdadeiro que ler a janela declarada no manifesto: o
        // manifesto diz o que o extrator PEDIU, e o que decide se dá para contar dia sem
        // estoque é o que de fato chegou em EstoquesDiarios.
        if (await CoberturaDeEstoqueAsync(conn, redeId, ct) is not { } cobertura) return null;
        if (!MercadoMesResolver.CabeNoHistorico(mes, cobertura.Primeiro, cobertura.Ultimo)) return null;

        var mapa = new Dictionary<(int, string), int>();

        await using var cmd = new SqlCommand($"""
            SELECT e.LojaId, e.Sku, SUM(CASE WHEN e.QuantidadeEmEstoque <= 0 THEN 1 ELSE 0 END)
            FROM dbo.EstoquesDiarios e
            {escopo.Join("e")}
            WHERE e.RedeId = @rede AND e.Data >= @ini AND e.Data <= @fim
            GROUP BY e.LojaId, e.Sku;
            """, conn);
        cmd.Parameters.AddWithValue("@rede", redeId);
        cmd.Parameters.Add("@ini", SqlDbType.Date).Value = mes.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@fim", SqlDbType.Date).Value = fim.ToDateTime(TimeOnly.MinValue);
        cmd.CommandTimeout = 300;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            mapa[(rd.GetInt32(0), rd.GetString(1))] = rd.GetInt32(2);
        }

        return mapa;
    }

    /// <summary>
    /// Primeiro e último dia com snapshot de estoque da rede, ou <c>null</c> quando não há
    /// nenhum.
    ///
    /// <para>
    /// <b>Sem escopo de SKU de propósito.</b> A pergunta é sobre a janela de datas que o
    /// import cobriu, não sobre quais itens ele trouxe: restringir aos SKUs da sugestão
    /// encurtaria a janela pelo item que entrou no cadastro mais tarde, e um mês coberto
    /// passaria a sair como não apurado por causa de um item.
    /// </para>
    /// </summary>
    private static async Task<(DateOnly Primeiro, DateOnly Ultimo)?> CoberturaDeEstoqueAsync(
        SqlConnection conn, int redeId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT MIN(Data), MAX(Data) FROM dbo.EstoquesDiarios WHERE RedeId = @rede;", conn);
        cmd.Parameters.AddWithValue("@rede", redeId);
        cmd.CommandTimeout = 300;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || await rd.IsDBNullAsync(0, ct)) return null;

        return (DateOnly.FromDateTime(rd.GetDateTime(0)), DateOnly.FromDateTime(rd.GetDateTime(1)));
    }

    /// <summary>
    /// Preço médio que a rede <b>de fato praticou</b> por <c>(loja, sku)</c> no mês comparado,
    /// ponderado pela quantidade.
    ///
    /// <para>
    /// <b>Média ponderada, e não média das médias.</b> <c>SUM(ValorTotal) / SUM(Quantidade)</c>
    /// dá o preço médio do que saiu; <c>AVG(PrecoUnitario)</c> daria peso igual ao dia que
    /// vendeu uma caixa e ao que vendeu duzentas, e num item com remarcação no meio do mês os
    /// dois números divergem de verdade.
    /// </para>
    ///
    /// <para>
    /// Chave ausente significa que a rede <b>não vendeu</b> o item naquele mês, e quem lê grava
    /// nulo. Zero afirmaria que ela vendeu de graça — e esta é uma coluna pela qual o comprador
    /// vai ordenar.
    /// </para>
    ///
    /// <para>
    /// <b>Não depende da janela de estoque</b>, ao contrário da ruptura: venda é registrada no
    /// próprio dia e não precisa de reconstrução para trás. Um mês pode ter preço praticado e
    /// ruptura "não apurada" ao mesmo tempo, e isso é correto.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<(int LojaId, string Sku), decimal>> PrecoPraticadoNoMesAsync(
        SqlConnection conn, int redeId, DateOnly mes, EscopoDeSkus escopo, CancellationToken ct)
    {
        var fim = mes.AddMonths(1).AddDays(-1);
        var mapa = new Dictionary<(int, string), decimal>();

        await using var cmd = new SqlCommand($"""
            SELECT v.LojaId, v.Sku, SUM(v.ValorTotal) / SUM(v.Quantidade)
            FROM dbo.Vendas v
            {escopo.Join("v")}
            WHERE v.RedeId = @rede AND v.Data >= @ini AND v.Data <= @fim
            GROUP BY v.LojaId, v.Sku
            HAVING SUM(v.Quantidade) > 0;
            """, conn);
        cmd.Parameters.AddWithValue("@rede", redeId);
        cmd.Parameters.Add("@ini", SqlDbType.Date).Value = mes.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@fim", SqlDbType.Date).Value = fim.ToDateTime(TimeOnly.MinValue);
        cmd.CommandTimeout = 300;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            mapa[(rd.GetInt32(0), rd.GetString(1))] = rd.GetDecimal(2);
        }

        return mapa;
    }
}
