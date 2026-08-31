using CosmosPro.ML.DemandForCast.Engine;
using Microsoft.EntityFrameworkCore;

namespace CosmosPro.ML.DemandForCast.ApiService.Mercado;

/// <summary>Uma linha da lista de oportunidades: um EAN que o bairro vende e a rede não tem.</summary>
/// <param name="Descricao">
/// Nome no catálogo da <b>IQVIA</b>, não no da rede — por definição a rede não tem cadastro
/// deste produto. Nulo quando o relatório trouxe medida sem a linha de dimensão; a tela
/// mostra o próprio EAN, nunca célula vazia.
/// </param>
internal sealed record OportunidadeDeSortimento(
    string Ean,
    string Brick,
    string? Descricao,
    string? Laboratorio,
    string? AreaFarmacia,
    string? Classe4,
    decimal UnidadesConcorrentes,
    decimal ValorCpp);

/// <param name="EansNoCatalogo">
/// Tamanho do catálogo da rede. <b>Zero significa "o comprador não enviou o arquivo", nunca
/// "a rede não tem produto nenhum"</b> — e é por isso que a consulta devolve lista vazia
/// nesse caso, em vez do mercado inteiro. A tela usa este número para explicar.
/// </param>
/// <param name="Mes">
/// Mês da IQVIA usado. O <b>mais recente coberto</b>, e não o anterior à sugestão como no
/// grupo B: aqui a pergunta é "o que devo incluir agora", não "o que o comprador poderia ter
/// sabido então", e sem sugestão pontuada não há vazamento possível.
/// </param>
internal sealed record OportunidadesPagina(
    IReadOnlyList<OportunidadeDeSortimento> Itens,
    int Total,
    DateOnly? Mes,
    int EansNoCatalogo);

/// <summary>
/// Regras A1 e A2 do documento de controle da IQVIA: o que o mercado vende no brick e não
/// existe no cadastro da rede, filtrado por relevância comercial.
/// </summary>
internal static class MercadoOportunidadesQuery
{
    /// <summary>
    /// Unidades mínimas do agregado de concorrentes no brick, no mês, para o item entrar na
    /// lista. Calibrado em junho/2026: sem corte a regra A1 devolve <b>44.874</b> avisos e a
    /// tela é abandonada; com 200, sobram 156 avisos e 116 produtos.
    ///
    /// <para>
    /// <b>É corte em unidades absolutas, e não por loja concorrente</b>, porque o contador de
    /// PDVs do painel não chegou ao banco — ele vive numa área de tabela dinâmica que o
    /// <c>IqviaXlsxParser</c> ignora. A distorção conhecida: o corte absoluto favorece o
    /// brick com mais lojas concorrentes. Quando o contador existir, o parâmetro volta a ser
    /// por loja e a distorção sai.
    /// </para>
    /// </summary>
    public const decimal CorteMinimoPadrao = 200m;

    private const string BandeiraConcorrentes = "CONCORRENTES";

    /// <summary>
    /// Decisão de corte, separada da consulta para ser afirmada sem banco.
    /// </summary>
    /// <remarks>
    /// Zero unidade nunca passa, mesmo com <paramref name="corteMinimo"/> zero: o mercado não
    /// vendeu nada do item no bairro, e oferecê-lo seria sugerir cadastrar o que ninguém
    /// compra ali.
    /// </remarks>
    public static bool PassaNoCorte(decimal unidades, decimal corteMinimo) =>
        unidades > 0m && unidades >= corteMinimo;

    public static async Task<OportunidadesPagina> ConsultarAsync(
        EngineDbContext db,
        int redeId,
        decimal corteMinimo,
        string? brick,
        string? areaFarmacia,
        int skip,
        int take,
        CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        var eansNoCatalogo = await db.RedeCatalogoEans
            .AsNoTracking()
            .CountAsync(c => c.RedeId == redeId, ct);

        // Catálogo vazio: o comprador não enviou o arquivo. Devolver o mercado inteiro como
        // oportunidade seria a tela mentindo em 100% das linhas — a única resposta honesta é
        // nada, e a tela explica o que falta.
        if (eansNoCatalogo == 0)
        {
            return new OportunidadesPagina([], 0, null, 0);
        }

        var mes = await db.MercadoObservacoes
            .AsNoTracking()
            .Where(o => o.RedeId == redeId)
            .MaxAsync(o => (DateOnly?)o.Mes, ct);

        if (mes is null)
        {
            return new OportunidadesPagina([], 0, null, eansNoCatalogo);
        }

        // Só CONCORRENTES. Somar a bandeira própria faria a tela oferecer de volta o
        // sortimento que a rede já tem — e o item nem apareceria como ausente do cadastro se
        // a rede o vendesse, então a soma só serviria para inflar o corte.
        var doMercado = db.MercadoObservacoes
            .AsNoTracking()
            .Where(o => o.RedeId == redeId
                     && o.Mes == mes
                     && o.Bandeira == BandeiraConcorrentes);

        if (!string.IsNullOrWhiteSpace(brick))
        {
            doMercado = doMercado.Where(o => o.Brick == brick);
        }

        // Anti-join por LEFT JOIN ... IS NULL, e não NOT IN: com NOT IN, um único EAN nulo do
        // lado direito faz o predicado devolver UNKNOWN para todas as linhas e a lista sai
        // vazia sem erro nenhum.
        var candidatos =
            from o in doMercado
            join p in db.MercadoProdutos.AsNoTracking().Where(p => p.RedeId == redeId)
                on o.Ean equals p.Ean into dimensao
            from p in dimensao.DefaultIfEmpty()
            join c in db.RedeCatalogoEans.AsNoTracking().Where(c => c.RedeId == redeId)
                on o.Ean equals c.Ean into cadastro
            from c in cadastro.DefaultIfEmpty()
            where c == null
            select new
            {
                o.Ean,
                o.Brick,
                o.Unidades,
                o.ValorCpp,
                p.DescricaoLonga,
                p.Laboratorio,
                p.AreaFarmacia,
                p.Classe4,
            };

        if (!string.IsNullOrWhiteSpace(areaFarmacia))
        {
            candidatos = candidatos.Where(x => x.AreaFarmacia == areaFarmacia);
        }

        // O corte no servidor repete a regra de PassaNoCorte porque o EF não traduz chamada
        // de método. Os dois testes que cercam isso são o que impede a divergência: um afirma
        // a fronteira sem banco, outro afirma a mesma fronteira contra o SQL Server.
        var filtrados = candidatos.Where(x => x.Unidades > 0m && x.Unidades >= corteMinimo);

        var total = await filtrados.CountAsync(ct);

        var itens = await filtrados
            .OrderByDescending(x => x.Unidades)
            .ThenBy(x => x.Ean)
            .Skip(skip)
            .Take(take)
            .Select(x => new OportunidadeDeSortimento(
                x.Ean,
                x.Brick,
                x.DescricaoLonga,
                x.Laboratorio,
                x.AreaFarmacia,
                x.Classe4,
                x.Unidades,
                x.ValorCpp))
            .ToListAsync(ct);

        return new OportunidadesPagina(itens, total, mes, eansNoCatalogo);
    }
}
