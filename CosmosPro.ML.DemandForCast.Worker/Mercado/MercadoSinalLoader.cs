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
    /// <param name="janelaInicio">
    /// Primeiro dia do histórico importado. Mês da IQVIA anterior a ele não tem snapshot de
    /// estoque, e a ruptura sai como <b>não apurada</b> em vez de zero.
    /// </param>
    public async Task<IReadOnlyDictionary<(int LojaId, string Sku), SinalDoItem>> CarregarAsync(
        int redeId,
        DateOnly diaDaSugestao,
        DateOnly janelaInicio,
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
            .Select(o => new { o.Brick, o.Bandeira, o.Ean, o.Unidades })
            .ToListAsync(ct);

        var porBrickEan = new Dictionary<(string Brick, string Ean), (decimal Rede, decimal Conc)>();
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

            if (NormalizarEan(o.Ean) is not { } ean) continue;

            var chave = (o.Brick, ean);
            var atual = porBrickEan.GetValueOrDefault(chave);
            porBrickEan[chave] = ehConcorrente
                ? (atual.Rede, atual.Conc + o.Unidades)
                : (atual.Rede + o.Unidades, atual.Conc);
        }

        var fatiaPorBrick = totalPorBrick.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Total > 0m ? kv.Value.Rede / kv.Value.Total : 0m,
            StringComparer.Ordinal);

        // --- ruptura no mês comparado (regra B3) ---------------------------------------
        var rupturas = await RupturaNoMesAsync(conn, redeId, mes, janelaInicio, escopo, ct);

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

            var calculado = MercadoAlertaCalculador.Calcular(new SinalBruto(
                UnidadesRede: medida.Rede,
                UnidadesConcorrentes: medida.Conc,
                FatiaAgregadaDaRede: fatiaAgregada,
                DiasSemEstoque: diasSemEstoque));

            if (calculado is not { } c) continue;

            sinais[(lojaId, sku)] = new SinalDoItem(
                mes, brick, medida.Rede, medida.Conc, c.Indice, diasSemEstoque, c.Alerta);
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
            if (NormalizarEan(rd.GetString(1)) is { } ean)
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
    /// <c>null</c> quando o mês comparado é anterior ao histórico importado — não há
    /// snapshot para contar, e zero afirmaria que havia estoque todos os dias. Dicionário
    /// sem a chave significa o mesmo para aquele par: <c>NaoApurado</c>, não
    /// <c>SemCausa</c>.
    /// </returns>
    private static async Task<Dictionary<(int LojaId, string Sku), int>?> RupturaNoMesAsync(
        SqlConnection conn, int redeId, DateOnly mes, DateOnly janelaInicio,
        EscopoDeSkus escopo, CancellationToken ct)
    {
        // O mês inteiro tem de caber no histórico. Mês parcialmente coberto subcontaria os
        // dias sem estoque e transformaria NaoApurado em SemCausa por acidente.
        if (mes < janelaInicio) return null;

        var fim = mes.AddMonths(1).AddDays(-1);
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
    /// Só dígitos, sem zeros à esquerda. É o que faz o EAN de 14 do PBS casar com o de 13
    /// da IQVIA — ver a nota da classe. Devolve <c>null</c> para código vazio ou todo zero,
    /// que não identifica produto nenhum.
    /// </summary>
    private static string? NormalizarEan(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;

        var digitos = new string([.. bruto.Where(char.IsAsciiDigit)]).TrimStart('0');
        return digitos.Length == 0 ? null : digitos;
    }
}
