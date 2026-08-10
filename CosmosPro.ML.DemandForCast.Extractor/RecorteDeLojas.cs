using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>Par (loja, SKU) como <c>escopo_sugestao.sql</c> devolve.</summary>
internal sealed record ParLojaSku(int LojaId, string Sku);

/// <summary>
/// O escopo depois do recorte. <see cref="LojasNaSugestao"/> é o denominador que o
/// manifesto e a tela declaram ("3 de 98") e não muda com a escolha.
/// </summary>
internal sealed record EscopoRecortado(
    IReadOnlyList<int> LojaIds, IReadOnlySet<string> Skus, int LojasNaSugestao);

/// <summary>
/// Recorta o escopo da sugestão às lojas escolhidas.
/// <para>
/// Os SKUs são recalculados a partir dos pares que sobraram, e não filtrados do
/// conjunto original: um SKU que só existia numa loja descartada tem de sair, senão
/// <c>@skus</c> continuaria pedindo o histórico dele — dado de uma loja que o
/// comprador acabou de excluir.
/// </para>
/// </summary>
internal static class RecorteDeLojas
{
    public static Result<EscopoRecortado> Aplicar(
        IReadOnlyCollection<ParLojaSku> paresDaSugestao, IReadOnlyList<int>? escolhidas)
    {
        var daSugestao = paresDaSugestao.Select(p => p.LojaId).ToHashSet();
        if (daSugestao.Count == 0) return Result.Fail<EscopoRecortado>(new LojasNaoSelecionadasErro());

        // null = todas (default do modo linha de comando). Lista vazia é engano de
        // digitação, não intenção -- e tratá-la como "todas" exportaria o oposto do pedido.
        if (escolhidas is null)
        {
            return Result.Ok(Montar(paresDaSugestao, daSugestao, daSugestao.Count));
        }

        if (escolhidas.Count == 0) return Result.Fail<EscopoRecortado>(new LojasNaoSelecionadasErro());

        var forasteiras = escolhidas.Distinct().Where(id => !daSugestao.Contains(id)).Order().ToArray();
        if (forasteiras.Length > 0)
        {
            return Result.Fail<EscopoRecortado>(new LojaForaDaSugestaoErro(forasteiras));
        }

        return Result.Ok(Montar(paresDaSugestao, escolhidas.ToHashSet(), daSugestao.Count));
    }

    private static EscopoRecortado Montar(
        IReadOnlyCollection<ParLojaSku> pares, HashSet<int> manter, int lojasNaSugestao)
    {
        var sobreviventes = pares.Where(p => manter.Contains(p.LojaId)).ToArray();

        return new EscopoRecortado(
            [.. sobreviventes.Select(p => p.LojaId).Distinct().Order()],
            sobreviventes.Select(p => p.Sku).ToHashSet(StringComparer.Ordinal),
            lojasNaSugestao);
    }
}
