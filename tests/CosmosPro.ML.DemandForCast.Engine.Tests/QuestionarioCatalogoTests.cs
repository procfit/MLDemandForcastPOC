using CosmosPro.ML.DemandForCast.Engine.Questionarios;
using FluentAssertions;
using Xunit;

namespace CosmosPro.ML.DemandForCast.Engine.Tests;

/// <summary>
/// Integridade do catálogo. Vale a pena testar dado que está em código porque o catálogo é
/// digitado à mão a partir de um documento externo: os erros que estes testes pegam — código
/// repetido, escala meio preenchida, seção vazia — só apareceriam depois, como resposta
/// impossível de tabular, e aí o dado já foi coletado e não volta.
/// </summary>
public sealed class QuestionarioCatalogoTests
{
    [Fact]
    public void Codigo_de_pergunta_e_unico_no_catalogo_inteiro()
    {
        var codigos = QuestionarioCatalogo.Perguntas.Select(p => p.Codigo).ToList();

        codigos.Should().OnlyHaveUniqueItems(
            "o código é a chave da resposta (PK de QuestionarioRespostas): repetido, uma pergunta " +
            "sobrescreveria a resposta da outra");
    }

    [Fact]
    public void Codigo_de_opcao_e_unico_dentro_da_pergunta()
    {
        foreach (var pergunta in QuestionarioCatalogo.Perguntas)
        {
            pergunta.Opcoes.Select(o => o.Codigo).Should().OnlyHaveUniqueItems(
                $"as opções de '{pergunta.Codigo}' precisam ser distinguíveis entre si");
        }
    }

    [Fact]
    public void Nenhuma_secao_ou_pergunta_fica_vazia()
    {
        QuestionarioCatalogo.Secoes.Should().NotBeEmpty();

        foreach (var secao in QuestionarioCatalogo.Secoes)
        {
            secao.Perguntas.Should().NotBeEmpty(
                $"a seção '{secao.Titulo}' viraria um passo em branco no wizard");
        }

        foreach (var pergunta in QuestionarioCatalogo.Perguntas)
        {
            pergunta.Opcoes.Should().HaveCountGreaterThan(1,
                $"'{pergunta.Codigo}' é de múltipla escolha: com uma opção só não há escolha");
        }
    }

    /// <summary>
    /// Escala pela metade não tabula: a análise trata <c>OpcaoValor</c> nulo como "esta pergunta
    /// não é ordinal", então uma pergunta com valor em algumas opções e não em outras produziria
    /// média sobre parte das respostas e silêncio sobre o resto.
    /// </summary>
    [Fact]
    public void Pergunta_ordinal_declara_valor_em_todas_as_opcoes_ou_em_nenhuma()
    {
        foreach (var pergunta in QuestionarioCatalogo.Perguntas)
        {
            var comValor = pergunta.Opcoes.Count(o => o.Valor is not null);

            comValor.Should().Match(n => n == 0 || n == pergunta.Opcoes.Count,
                $"'{pergunta.Codigo}' tem escala parcial: {comValor} de {pergunta.Opcoes.Count} " +
                "opções com valor");
        }
    }

    [Fact]
    public void Escala_de_pergunta_ordinal_nao_repete_posicao()
    {
        foreach (var pergunta in QuestionarioCatalogo.Perguntas)
        {
            var valores = pergunta.Opcoes.Select(o => o.Valor).OfType<int>().ToList();
            if (valores.Count == 0) continue;

            valores.Should().OnlyHaveUniqueItems(
                $"duas opções de '{pergunta.Codigo}' na mesma posição da escala são indistinguíveis " +
                "na análise");
        }
    }

    /// <summary>
    /// Os limites são os das colunas em <c>EngineDbContext</c>: o texto é gravado junto com a
    /// resposta, e um enunciado maior que a coluna estouraria na escrita — no envio do
    /// comprador, depois de ele preencher tudo.
    /// </summary>
    [Fact]
    public void Textos_cabem_nas_colunas_do_snapshot()
    {
        foreach (var pergunta in QuestionarioCatalogo.Perguntas)
        {
            pergunta.Codigo.Length.Should().BeLessThanOrEqualTo(40, $"código '{pergunta.Codigo}'");
            pergunta.Texto.Length.Should().BeLessThanOrEqualTo(500, $"enunciado de '{pergunta.Codigo}'");

            foreach (var opcao in pergunta.Opcoes)
            {
                opcao.Codigo.Length.Should().BeLessThanOrEqualTo(40, $"código '{opcao.Codigo}'");
                opcao.Texto.Length.Should().BeLessThanOrEqualTo(300, $"texto da opção '{opcao.Codigo}'");
            }
        }
    }

    [Fact]
    public void Pergunta_resolve_por_codigo_e_opcao_resolve_dentro_dela()
    {
        var qualquer = QuestionarioCatalogo.Perguntas[0];

        QuestionarioCatalogo.Pergunta(qualquer.Codigo).Should().BeSameAs(qualquer);
        QuestionarioCatalogo.Pergunta("NAO_EXISTE").Should().BeNull();

        qualquer.Opcao(qualquer.Opcoes[0].Codigo).Should().BeSameAs(qualquer.Opcoes[0]);
        qualquer.Opcao("NAO_EXISTE").Should().BeNull();
    }
}
