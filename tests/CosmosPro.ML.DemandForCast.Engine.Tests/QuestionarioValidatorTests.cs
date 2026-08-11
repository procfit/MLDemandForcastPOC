using CosmosPro.ML.DemandForCast.Engine.Questionarios;
using FluentAssertions;
using Xunit;

namespace CosmosPro.ML.DemandForCast.Engine.Tests;

public sealed class QuestionarioValidatorTests
{
    /// <summary>
    /// Uma resposta válida para cada pergunta obrigatória, montada a partir do próprio catálogo
    /// em vez de códigos escritos à mão: assim estes testes continuam valendo quando o
    /// instrumento real substituir o placeholder.
    /// </summary>
    private static List<RespostaInformada> Completo() =>
    [
        .. QuestionarioCatalogo.Perguntas
            .Where(p => p.Obrigatoria)
            .Select(p => new RespostaInformada(p.Codigo, p.Opcoes[0].Codigo, null))
    ];

    [Fact]
    public void Conjunto_valido_nao_gera_erro_nem_falta()
    {
        var respostas = Completo();

        QuestionarioValidator.Conferir(respostas).Should().BeEmpty();
        QuestionarioValidator.ObrigatoriasFaltando(respostas).Should().BeEmpty();
    }

    [Fact]
    public void Pergunta_fora_do_catalogo_e_recusada()
    {
        var erros = QuestionarioValidator.Conferir(
            [new RespostaInformada("NAO_EXISTE", "QUALQUER", null)]);

        erros.Should().ContainSingle().Which.Should().Contain("NAO_EXISTE");
    }

    [Fact]
    public void Opcao_de_outra_pergunta_e_recusada()
    {
        var pergunta = QuestionarioCatalogo.Perguntas[0];
        var alheia = QuestionarioCatalogo.Perguntas
            .First(p => p.Codigo != pergunta.Codigo)
            .Opcoes[0];

        var erros = QuestionarioValidator.Conferir(
            [new RespostaInformada(pergunta.Codigo, alheia.Codigo, null)]);

        erros.Should().ContainSingle().Which.Should().Contain(alheia.Codigo);
    }

    [Fact]
    public void Pergunta_respondida_duas_vezes_e_recusada()
    {
        var p = QuestionarioCatalogo.Perguntas[0];

        var erros = QuestionarioValidator.Conferir(
        [
            new RespostaInformada(p.Codigo, p.Opcoes[0].Codigo, null),
            new RespostaInformada(p.Codigo, p.Opcoes[1].Codigo, null),
        ]);

        erros.Should().ContainSingle().Which.Should().Contain("mais de uma vez");
    }

    /// <summary>
    /// A tela só abre o campo nas opções marcadas, então texto numa opção que não o permite vem
    /// de requisição fabricada — e gravá-lo poria na coluna de pesquisa um dado que nenhuma
    /// pergunta produziu.
    /// </summary>
    [Fact]
    public void Texto_livre_em_opcao_que_nao_permite_e_recusado()
    {
        var (pergunta, opcao) = QuestionarioCatalogo.Perguntas
            .SelectMany(p => p.Opcoes.Select(o => (Pergunta: p, Opcao: o)))
            .First(x => !x.Opcao.PermiteTextoLivre);

        var erros = QuestionarioValidator.Conferir(
            [new RespostaInformada(pergunta.Codigo, opcao.Codigo, "complemento indevido")]);

        erros.Should().ContainSingle().Which.Should().Contain("complemento escrito");
    }

    [Fact]
    public void Texto_livre_em_opcao_que_permite_passa()
    {
        var (pergunta, opcao) = QuestionarioCatalogo.Perguntas
            .SelectMany(p => p.Opcoes.Select(o => (Pergunta: p, Opcao: o)))
            .First(x => x.Opcao.PermiteTextoLivre);

        QuestionarioValidator.Conferir(
            [new RespostaInformada(pergunta.Codigo, opcao.Codigo, "porque sim")])
            .Should().BeEmpty();
    }

    /// <summary>
    /// O que separa rascunho de envio: gravar parcial é o recurso que permite "salvo, volto
    /// depois". Se <c>Conferir</c> também exigisse completude, sair no meio do wizard perderia
    /// tudo — e aí o dado da pesquisa simplesmente não existiria.
    /// </summary>
    [Fact]
    public void Conjunto_incompleto_grava_mas_nao_envia()
    {
        var obrigatorias = QuestionarioCatalogo.Perguntas.Where(p => p.Obrigatoria).ToList();
        obrigatorias.Should().HaveCountGreaterThan(1, "o cenário precisa de algo a faltar");

        var parcial = Completo();
        var removida = parcial[^1];
        parcial.RemoveAt(parcial.Count - 1);

        QuestionarioValidator.Conferir(parcial).Should().BeEmpty();
        QuestionarioValidator.ObrigatoriasFaltando(parcial).Should().Equal(removida.PerguntaCodigo);
    }

    /// <summary>
    /// O instrumento (Apêndice A) não marca nenhuma pergunta como opcional, então o caminho
    /// <c>Obrigatoria = false</c> não é exercitado pelo catálogo real. Este teste afirma isso em
    /// vez de fingir cobri-lo: um `if (opcional is null) return;` passaria calado e diria que a
    /// regra de opcionalidade está testada quando não está. Se uma pergunta opcional entrar, ele
    /// falha e obriga a escrever a cobertura de verdade.
    /// </summary>
    [Fact]
    public void Instrumento_atual_nao_tem_pergunta_opcional()
        => QuestionarioCatalogo.Perguntas.Should().OnlyContain(p => p.Obrigatoria);

    /// <summary>
    /// A ordem é a do catálogo, que é a ordem dos passos na tela: é o que permite a quem recebe
    /// a lista dizer para qual passo voltar em vez de só "faltou algo".
    /// </summary>
    [Fact]
    public void Faltantes_saem_na_ordem_do_catalogo()
    {
        var esperado = QuestionarioCatalogo.Perguntas
            .Where(p => p.Obrigatoria)
            .Select(p => p.Codigo)
            .ToList();

        QuestionarioValidator.ObrigatoriasFaltando([]).Should().Equal(esperado);
    }
}
