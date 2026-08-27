using System.IO.Compression;
using System.Text;
using CosmosPro.ML.DemandForCast.SyntheticData.Generation;
using CosmosPro.ML.DemandForCast.SyntheticData.Models;

namespace CosmosPro.ML.DemandForCast.SyntheticData;

/// <summary>
/// Gera um dataset farma sintético com regras de domínio realistas (ABC,
/// sazonalidade, promoções, ruptura) e empacota em um ZIP no formato esperado
/// pelo pipeline de import. Roda em memória; consumidor pode pegar os bytes e
/// mandar pro fluxo normal de upload.
/// </summary>
public static class SyntheticDatasetGenerator
{
    /// <summary>
    /// Resultado da geração: bytes do ZIP + estatísticas para logging/feedback ao usuário.
    /// </summary>
    public sealed record Result(byte[] ZipBytes, GenerationStats Stats);

    public sealed record GenerationStats(
        int Lojas,
        int Produtos,
        long Vendas,
        long Estoques,
        long Compras,
        int Promocoes,
        int SinaisExternos,
        TimeSpan Duration);

    public static Result Generate(SyntheticDatasetOptions options)
    {
        if (options.DataFim < options.DataInicio)
            throw new ArgumentException("DataFim deve ser >= DataInicio.", nameof(options));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var lojas = MasterData.BuildLojas(options.NumLojas, options.Seed);
        var produtos = MasterData.BuildProdutos(options.NumSkus, options.Seed + 1);
        var ufs = lojas.Select(l => l.UF).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var demand = new DemandModel(options.NumSkus, options.NumLojas, options.DataInicio, options.DataFim, options, options.Seed + 2, ufs);
        var rng = new Random(options.Seed + 3);

        var output = new MemoryStream();
        long vendasCount, estoquesCount, comprasCount;
        int promosCount, sinaisCount;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteLojas(zip, lojas);
            WriteProdutos(zip, produtos);
            (vendasCount, estoquesCount) = WriteVendasEEstoques(zip, lojas, produtos, demand, rng);
            comprasCount = WriteCompras(zip, lojas, produtos, demand, rng);
            promosCount = WritePromocoes(zip, produtos, demand);
            sinaisCount = WriteSinaisExternos(zip, demand, ufs);
            // ZipArchive.Dispose escreve o central directory — ToArray PRECISA vir depois.
        }
        sw.Stop();
        return new Result(
            output.ToArray(),
            new GenerationStats(lojas.Count, produtos.Count, vendasCount, estoquesCount, comprasCount, promosCount, sinaisCount, sw.Elapsed));
    }

    private static StreamWriter OpenCsv(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        var stream = entry.Open();
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private static void WriteLojas(ZipArchive zip, List<LojaRow> lojas)
    {
        using var w = OpenCsv(zip, "lojas.csv");
        w.WriteLine("LojaId,Nome,UF,Cidade,Regiao,Perfil,DiasOperacaoSemana,DataAbertura,Ativo,Cnpj");
        foreach (var l in lojas)
        {
            w.WriteLine(new CsvRowBuilder()
                .Add(l.LojaId).Add(l.Nome).Add(l.UF).Add(l.Cidade)
                .Add(l.Regiao).Add(l.Perfil).Add(l.DiasOperacaoSemana)
                .Add(l.DataAbertura).Add(l.Ativo).Add(l.Cnpj).Build());
        }
    }

    private static void WriteProdutos(ZipArchive zip, List<ProdutoRow> produtos)
    {
        using var w = OpenCsv(zip, "produtos.csv");
        w.WriteLine("Sku,Nome,Categoria,Subcategoria,Fabricante,PrincipioAtivo,Apresentacao,Ean,RegistroAnvisa,ListaControle,ClasseTerapeutica,Ativo");
        foreach (var p in produtos)
        {
            w.WriteLine(new CsvRowBuilder()
                .Add(p.Sku).Add(p.Nome).Add(p.Categoria).Add(p.Subcategoria)
                .Add(p.Fabricante).Add(p.PrincipioAtivo).Add(p.Apresentacao)
                .Add(p.Ean).Add(p.RegistroAnvisa).Add(p.ListaControle)
                .Add(p.ClasseTerapeutica).Add(p.Ativo).Build());
        }
    }

    private static (long Vendas, long Estoques) WriteVendasEEstoques(
        ZipArchive zip, List<LojaRow> lojas, List<ProdutoRow> produtos, DemandModel demand, Random rng)
    {
        // ZipArchive em Create mode aceita apenas UMA entry aberta por vez.
        // Como o algoritmo gera vendas+estoques em loop único (estado de estoque
        // depende das vendas anteriores), bufferizamos em StringBuilders e
        // escrevemos sequencialmente no final.
        var sbVendas = new StringBuilder("Data,LojaId,Sku,Quantidade,PrecoUnitario,ValorTotal\n");
        var sbEstoque = new StringBuilder("Data,LojaId,Sku,QuantidadeEmEstoque\n");

        long vendasCount = 0, estoquesCount = 0;

        // Preço base por SKU (rank-dependente): top sellers tendem a ter
        // preço médio menor (giro alto, margem fina); cauda preço mais alto.
        // Acrescenta variação por loja (±15%).
        var precoBase = new decimal[produtos.Count];
        for (int i = 0; i < produtos.Count; i++)
        {
            var rank = i + 1;
            // R$5 a R$80 dependendo do rank.
            precoBase[i] = 5m + (decimal)(75.0 * Math.Min(1.0, rank / (double)produtos.Count));
        }

        for (int s = 0; s < produtos.Count; s++)
        {
            var preco = precoBase[s];
            for (int l = 0; l < lojas.Count; l++)
            {
                var lojaId = lojas[l].LojaId;
                var sku = produtos[s].Sku;
                // Estoque vai sendo "esgotado" pelas vendas e periodicamente reposto.
                // Estado simples: estoqueAtual decremento por venda, ressuprimento
                // se cair abaixo de 5 com probabilidade 40% no dia (reflete giro).
                var estoqueAtual = 20m + (decimal)(rng.NextDouble() * 50);

                for (var dia = demand.Inicio; dia <= demand.Fim; dia = dia.AddDays(1))
                {
                    bool ruptura = demand.EmRuptura(s, dia, rng) || estoqueAtual <= 0;
                    decimal qtd = 0;
                    if (!ruptura)
                    {
                        qtd = demand.DemandaDia(s, dia, lojas[l].UF, produtos[s].Subcategoria);
                        if (qtd > estoqueAtual) qtd = estoqueAtual;
                        estoqueAtual -= qtd;
                    }

                    if (qtd > 0)
                    {
                        // Preço efetivo: aplica desconto se em promoção.
                        var precoEfetivo = preco;
                        if (demand.EmPromocao(s, dia))
                        {
                            var promo = demand.PromocaoDoSku(s);
                            if (promo is not null)
                                precoEfetivo = preco * (1m - (decimal)(promo.Value.Desconto / 100.0));
                        }
                        sbVendas.AppendLine(new CsvRowBuilder()
                            .Add(dia).Add(lojaId).Add(sku)
                            .Add(qtd).Add(precoEfetivo).Add(qtd * precoEfetivo)
                            .Build());
                        vendasCount++;
                    }

                    // Estoque registrado todo dia (mesmo 0) — engine usa pra detectar
                    // ruptura. Para reduzir volume, registra só dias com mudança ou
                    // baseline a cada 7 dias.
                    var diasFromStart = dia.DayNumber - demand.Inicio.DayNumber;
                    bool snapshotDia = diasFromStart % 7 == 0 || qtd > 0 || ruptura;
                    if (snapshotDia)
                    {
                        sbEstoque.AppendLine(new CsvRowBuilder()
                            .Add(dia).Add(lojaId).Add(sku).Add(estoqueAtual).Build());
                        estoquesCount++;
                    }

                    // Ressuprimento: se estoque baixo, repor com chance.
                    if (estoqueAtual <= 5 && rng.NextDouble() < 0.4)
                    {
                        estoqueAtual += 20m + (decimal)(rng.NextDouble() * 80);
                    }
                }
            }
        }

        // Flush sequencial — uma entry por vez (ZipArchive Create constraint).
        using (var w = OpenCsv(zip, "vendas.csv")) w.Write(sbVendas);
        using (var w = OpenCsv(zip, "estoques_diarios.csv")) w.Write(sbEstoque);
        return (vendasCount, estoquesCount);
    }

    private static long WriteCompras(
        ZipArchive zip, List<LojaRow> lojas, List<ProdutoRow> produtos, DemandModel demand, Random rng)
    {
        using var w = OpenCsv(zip, "compras.csv");
        w.WriteLine("DataPedido,DataRecebimento,LojaId,Sku,Quantidade,Fornecedor");

        long count = 0;
        // ~1 compra por SKU-top × loja × mês. Cauda: menos frequente.
        var fornecedores = new[] { "Distribuidora Profarma", "Santa Cruz", "Panpharma", "BR Pharma", "Onofre Distribuidora" };
        var meses = ((demand.Fim.Year - demand.Inicio.Year) * 12 + demand.Fim.Month - demand.Inicio.Month) + 1;

        // Só os top 20% têm compras explícitas registradas — cauda usa ressuprimento implícito.
        int topN = Math.Max(1, produtos.Count / 5);
        for (int s = 0; s < topN; s++)
        {
            for (int l = 0; l < lojas.Count; l++)
            {
                for (int m = 0; m < meses; m++)
                {
                    if (rng.NextDouble() > 0.7) continue;
                    var diaPedido = demand.Inicio.AddMonths(m).AddDays(rng.Next(28));
                    if (diaPedido > demand.Fim) continue;
                    var diaRecebimento = diaPedido.AddDays(rng.Next(1, 6));
                    if (diaRecebimento > demand.Fim) diaRecebimento = demand.Fim;
                    var qtd = 30m + (decimal)(rng.NextDouble() * 270);
                    w.WriteLine(new CsvRowBuilder()
                        .Add(diaPedido).Add(diaRecebimento).Add(lojas[l].LojaId).Add(produtos[s].Sku)
                        .Add(qtd).Add(fornecedores[rng.Next(fornecedores.Length)])
                        .Build());
                    count++;
                }
            }
        }
        return count;
    }

    private static int WritePromocoes(ZipArchive zip, List<ProdutoRow> produtos, DemandModel demand)
    {
        using var w = OpenCsv(zip, "promocoes.csv");
        w.WriteLine("DataInicio,DataFim,Sku,LojaId,Tipo,DescontoPct");

        int count = 0;
        for (int s = 0; s < produtos.Count; s++)
        {
            var promo = demand.PromocaoDoSku(s);
            if (promo is null) continue;
            // LojaId vazio = promoção em todas as lojas.
            w.WriteLine(new CsvRowBuilder()
                .Add(promo.Value.Inicio).Add(promo.Value.Fim).Add(produtos[s].Sku)
                .Add((int?)null).Add("Desconto").Add((decimal)promo.Value.Desconto)
                .Build());
            count++;
        }
        return count;
    }

    /// <summary>
    /// Emite os sinais exógenos regionais diários (clima e gripe) por UF — formato
    /// longo (Data, Geografia, Tipo, Valor), uma linha por (UF × dia × tipo).
    /// </summary>
    private static int WriteSinaisExternos(ZipArchive zip, DemandModel demand, List<string> ufs)
    {
        using var w = OpenCsv(zip, "sinais_externos.csv");
        w.WriteLine("Data,Geografia,Tipo,Valor");

        int count = 0;
        foreach (var uf in ufs)
        {
            for (var dia = demand.Inicio; dia <= demand.Fim; dia = dia.AddDays(1))
            {
                w.WriteLine(new CsvRowBuilder().Add(dia).Add(uf).Add("Clima").Add((decimal)demand.ClimaTempC(uf, dia)).Build());
                w.WriteLine(new CsvRowBuilder().Add(dia).Add(uf).Add("Gripe").Add((decimal)demand.GripeIndice(uf, dia)).Build());
                count += 2;
            }
        }
        return count;
    }
}
