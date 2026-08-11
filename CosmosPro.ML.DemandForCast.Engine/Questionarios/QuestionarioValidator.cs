namespace CosmosPro.ML.DemandForCast.Engine.Questionarios;

/// <summary>Uma resposta como ela chega da tela: só códigos, mais o complemento livre.</summary>
public sealed record RespostaInformada(string PerguntaCodigo, string OpcaoCodigo, string? TextoLivre);

/// <summary>
/// Confere respostas contra <see cref="QuestionarioCatalogo"/>.
///
/// <para>
/// Duas checagens <b>separadas</b> de propósito, porque o rascunho e o envio precisam de
/// exigências diferentes: <see cref="Conferir"/> vale nos dois (uma resposta malformada nunca
/// deve ser gravada), e <see cref="ObrigatoriasFaltando"/> vale só no envio — bloquear
/// gravação parcial transformaria "salvar e voltar depois" em perda de tudo.
/// </para>
/// </summary>
public static class QuestionarioValidator
{
    /// <summary>
    /// Problemas nas respostas informadas: pergunta ou opção que não existe no catálogo,
    /// duplicata, e texto livre em opção que não o permite. Lista vazia = pode gravar.
    /// </summary>
    public static IReadOnlyList<string> Conferir(IEnumerable<RespostaInformada> respostas)
    {
        var erros = new List<string>();
        var vistas = new HashSet<string>();

        foreach (var r in respostas)
        {
            if (!vistas.Add(r.PerguntaCodigo))
            {
                erros.Add($"A pergunta '{r.PerguntaCodigo}' foi respondida mais de uma vez.");
                continue;
            }

            if (Problema(r) is { } erro) erros.Add(erro);
        }

        return erros;
    }

    /// <summary>
    /// O que está errado nesta resposta, ou <c>null</c> se nada está. Separado de
    /// <see cref="Conferir"/> para a duplicata — que precisa do estado do laço — não se misturar
    /// com as checagens de conteúdo, que não precisam de nada além da própria resposta.
    /// </summary>
    private static string? Problema(RespostaInformada r)
    {
        if (QuestionarioCatalogo.Pergunta(r.PerguntaCodigo) is not { } pergunta)
        {
            return $"A pergunta '{r.PerguntaCodigo}' não existe no questionário.";
        }

        if (pergunta.Opcao(r.OpcaoCodigo) is not { } opcao)
        {
            return $"A opção '{r.OpcaoCodigo}' não é uma das alternativas de '{r.PerguntaCodigo}'.";
        }

        // A recusa de texto livre não é preciosismo: o campo é digitado pelo participante e vai
        // para a análise. Aceitá-lo numa opção que a tela nunca oferece com campo aberto
        // significaria que ele veio de requisição fabricada, e gravá-lo poria numa coluna de
        // pesquisa um dado que nenhuma pergunta produziu.
        if (!opcao.PermiteTextoLivre && !string.IsNullOrWhiteSpace(r.TextoLivre))
        {
            return $"A opção '{r.OpcaoCodigo}' de '{r.PerguntaCodigo}' não aceita complemento escrito.";
        }

        return null;
    }

    /// <summary>
    /// Códigos das perguntas obrigatórias ainda sem resposta, na ordem do catálogo — é a ordem
    /// em que a tela as mostra, então quem recebe a lista sabe para qual passo voltar.
    /// Lista vazia = pode enviar.
    /// </summary>
    public static IReadOnlyList<string> ObrigatoriasFaltando(IEnumerable<RespostaInformada> respostas)
    {
        var respondidas = respostas.Select(r => r.PerguntaCodigo).ToHashSet();

        return
        [
            .. QuestionarioCatalogo.Perguntas
                .Where(p => p.Obrigatoria && !respondidas.Contains(p.Codigo))
                .Select(p => p.Codigo)
        ];
    }
}
