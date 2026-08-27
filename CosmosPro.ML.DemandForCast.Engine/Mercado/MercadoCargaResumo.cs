namespace CosmosPro.ML.DemandForCast.Engine.Mercado;

/// <summary>
/// Forma do JSON gravado em <c>MercadoCarga.ResumoJson</c>. Vive no Engine porque as
/// duas pontas o usam: o Worker escreve ao processar o XLSX e a API o devolve para a
/// tela dizer o que aquele envio cobriu.
///
/// <para>
/// A cobertura vem das colunas presentes no arquivo, não do filtro da consulta IQVIA —
/// o relatório real traz bricks pedidos no filtro que não geraram coluna nenhuma.
/// Dentro de um (mês, brick) coberto, ausência de linha em <c>MercadoObservacoes</c>
/// significa venda zero; fora da cobertura, significa não enviado.
/// </para>
/// </summary>
public sealed record MercadoCargaResumo(
    IReadOnlyList<DateOnly> Meses,
    IReadOnlyList<string> Bricks,
    IReadOnlyList<string> Bandeiras,
    int LinhasDoArquivo,
    int LinhasSemEan,
    long CelulasZeradas);
