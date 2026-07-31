using System.Globalization;
using System.Text.Json;

namespace CosmosPro.ML.DemandForCast.Worker.Sessoes;

/// <summary>
/// Declaração que o extrator escreve na raiz do ZIP dizendo qual sugestão do PBS ele
/// trouxe e qual período a acompanha.
///
/// <para>
/// <b>É uma cópia deliberada</b> do <c>ZipManifest</c> do projeto Extractor. O extrator
/// roda na máquina do cliente e o Worker não pode depender dele — mesma razão pela qual o
/// contrato de colunas do Stage também é duplicado (ver <c>TableSchemas</c> e o
/// <c>InternalsVisibleTo</c> do .csproj). O que impede as duas formas de divergirem em
/// silêncio é o teste de contrato em <c>ManifestoContratoTests</c>, que compara os campos
/// um a um e ainda faz o leitor daqui ler o que o serializador de lá escreve.
/// </para>
/// </summary>
internal sealed record ManifestoDaSugestao(
    long SugestaoId,
    string? SugestaoDescricao,
    DateTime SugestaoDataHora,
    byte SugestaoTipoCalculo,
    DateOnly JanelaInicio,
    DateOnly JanelaFim,
    string VersaoExtractor,
    int SkusSemCadastro);

/// <summary>
/// Ou o retrato da sugestão, ou o motivo pelo qual o envio não sustenta comparação
/// nenhuma — nunca os dois. O motivo é texto de comprador, não de log: ele vai direto para
/// <c>ComparacaoSessao.MotivoInviabilidade</c> e aparece na tela.
/// </summary>
internal sealed record LeituraDoManifesto(ManifestoDaSugestao? Manifesto, string? MotivoInviabilidade);

/// <summary>
/// Lê a declaração da sugestão no ZIP já extraído.
///
/// <para>
/// Toda recusa daqui é <b>inviabilidade, não falha</b>: nada quebrou do nosso lado, faltou
/// pré-condição no que foi enviado, e o remédio é sempre o mesmo — gerar os dados de novo
/// pelo extrator. Por isso nenhum caminho aqui lança exceção: exceção viraria
/// <c>Falha</c> e mandaria o comprador "tentar de novo" um arquivo que nunca vai servir.
/// </para>
/// </summary>
internal static class ManifestoLeitor
{
    public const string NomeArquivo = "manifesto.json";

    /// <summary>
    /// Sem política de nomes e sem tolerância a casing: o lado que escreve serializa com os
    /// nomes dos próprios membros, e divergir disso é justamente o que o teste de contrato
    /// existe para pegar.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new();

    public static LeituraDoManifesto Ler(string diretorio)
    {
        var caminho = Path.Combine(diretorio, NomeArquivo);
        if (!File.Exists(caminho))
        {
            return Inviavel(SemDeclaracao);
        }

        ManifestoDaSugestao? manifesto;
        try
        {
            manifesto = JsonSerializer.Deserialize<ManifestoDaSugestao>(File.ReadAllText(caminho), Json);
        }
        catch (JsonException)
        {
            return Inviavel(Danificada);
        }

        // Id ausente ou zerado é indistinguível de conteúdo corrompido para quem vai agir:
        // a declaração está lá, mas não aponta para sugestão nenhuma.
        if (manifesto is null || manifesto.SugestaoId <= 0)
        {
            return Inviavel(Danificada);
        }

        var diaDaSugestao = DateOnly.FromDateTime(manifesto.SugestaoDataHora);

        // Os dois extremos estruturais, e só eles: sem nenhum dia a partir da sugestão não
        // existe gabarito, e sem nenhum dia antes dela não existe histórico. A cobertura
        // suficiente para pontuar CADA item depende de DiasEstoque item a item, que só
        // existe depois do import — a comparação a reporta por item, e não é decidível aqui.
        if (manifesto.JanelaFim < diaDaSugestao)
        {
            return Inviavel(SemGabarito(diaDaSugestao, manifesto.JanelaFim));
        }

        if (manifesto.JanelaInicio >= diaDaSugestao)
        {
            return Inviavel(SemHistorico(diaDaSugestao, manifesto.JanelaInicio));
        }

        return new LeituraDoManifesto(manifesto, null);
    }

    private static LeituraDoManifesto Inviavel(string motivo) => new(null, motivo);

    private const string SemDeclaracao =
        "Este envio não identifica qual sugestão de compra do seu ERP deve ser avaliada, então não há o que " +
        "comparar. Baixe o extrator, escolha a sugestão que você quer comparar e envie o arquivo que ele gerar, " +
        "sem alterar o conteúdo.";

    private const string Danificada =
        "A identificação da sugestão de compra veio danificada e não foi possível saber qual sugestão você " +
        "escolheu. Gere os dados novamente pelo extrator, escolhendo a mesma sugestão, e envie sem abrir nem " +
        "editar o que ele produzir.";

    private static string SemGabarito(DateOnly diaDaSugestao, DateOnly fim) => string.Format(
        CultureInfo.InvariantCulture,
        "Os dados enviados terminam em {0:dd/MM/yyyy}, antes do dia da sugestão ({1:dd/MM/yyyy}). Sem as vendas " +
        "que aconteceram depois da compra não há como saber quem acertou. Gere os dados novamente pelo extrator, " +
        "que monta o período certo em torno da sugestão escolhida.",
        fim, diaDaSugestao);

    private static string SemHistorico(DateOnly diaDaSugestao, DateOnly inicio) => string.Format(
        CultureInfo.InvariantCulture,
        "Os dados enviados começam em {0:dd/MM/yyyy}, no dia da sugestão ({1:dd/MM/yyyy}) ou depois, e sem " +
        "histórico de vendas anterior a ela não há como aprender o padrão de venda das suas lojas. Gere os dados " +
        "novamente pelo extrator, que monta o período certo em torno da sugestão escolhida.",
        inicio, diaDaSugestao);
}
