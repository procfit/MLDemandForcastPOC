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

    /// <summary>
    /// A forma do instrumento V6, travada de proposito.
    ///
    /// <para>
    /// O catalogo ja foi renumerado <b>duas vezes</b> (V2 → V5 → V6) e cada renumeracao troca o
    /// dono de um codigo sem produzir erro nenhum: a tela renderiza, o envio grava, e so a
    /// tabulacao no Excel sai errada — meses depois, quando o dado ja foi coletado. Este teste nao
    /// impede a proxima renumeracao, ele obriga a ser deliberada: quem mudar o instrumento tem de
    /// mudar aqui tambem, e ao fazer isso ve o aviso de subir a Versao.
    /// </para>
    /// </summary>
    [Fact]
    public void Parte_B_da_V6_tem_onze_afirmacoes_nos_codigos_do_documento()
    {
        QuestionarioCatalogo.Versao.Should().Be(4, "V6 do Apendice A");

        var parteB = QuestionarioCatalogo.Perguntas
            .Where(p => p.Codigo.StartsWith('B'))
            .Select(p => p.Codigo)
            .ToList();

        parteB.Should().Equal(
            ["B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B11"],
            "os codigos e a ordem sao os do documento submetido, e a analise casa por eles");

        QuestionarioCatalogo.Perguntas
            .Where(p => p.Codigo.StartsWith('A'))
            .Select(p => p.Codigo)
            .Should().Equal(["A1", "A2", "A3"], "a A4 do documento esta deliberadamente ausente");
    }

    /// <summary>
    /// Duas ausencias e uma presenca que valem asserção porque foram decisoes de quem conduz a
    /// pesquisa, e as tres seriam "corrigidas" por alguem lendo o documento antigo.
    /// </summary>
    [Fact]
    public void A_V6_nao_pergunta_sobre_clima_e_epidemias_e_fecha_com_o_uso_diario()
    {
        var textos = QuestionarioCatalogo.Perguntas.Select(p => p.Texto).ToList();

        textos.Should().NotContain(t => t.Contains("epidemias", StringComparison.OrdinalIgnoreCase),
            "a afirmacao sobre variaveis externas (sazonalidade, clima, epidemias) era a B6 da V5 e "
            + "saiu na V6: era a unica que perguntava sobre coisas que o artefacto nao faz");

        QuestionarioCatalogo.Pergunta("B11")!.Texto.Should().Contain("operação diária",
            "esta afirmacao foi B7 na V2, B12 na V5 e e B11 na V6 — e o caso concreto de codigo "
            + "reaproveitado que impede agrupar respostas por codigo sozinho");
    }

    /// <summary>
    /// O termo de consentimento nao promete anonimato, e isso e deliberado: <c>Questionario</c>
    /// grava <c>UsuarioId</c>, e o patrocinador pede a identificacao do respondente na exportacao.
    /// O documento V6 que ele enviou traz a frase antiga de volta; a decisao de 07/09/2026 foi
    /// manter o texto corrigido aqui. Se a frase reaparecer, o participante volta a consentir com
    /// uma coisa enquanto o sistema faz outra.
    /// </summary>
    [Fact]
    public void Apresentacao_nao_promete_anonimato()
    {
        var apresentacao = string.Join(" ", QuestionarioCatalogo.Apresentacao);

        apresentacao.Should().NotContain("anónima")
            .And.NotContain("anônima")
            .And.NotContain("permita identificar");
        apresentacao.Should().Contain("confidencial", "o que se promete e confidencialidade");
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
