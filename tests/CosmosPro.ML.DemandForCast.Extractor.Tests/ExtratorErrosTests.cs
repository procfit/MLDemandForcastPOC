using System.ComponentModel;
using CosmosPro.ML.DemandForCast.Extractor;
using FluentResults;

namespace CosmosPro.ML.DemandForCast.Extractor.Tests;

/// <summary>
/// A classificação de falha é o que decide o que o operador lê e o que o script
/// que chama o CLI recebe. Ela é testada sobre <see cref="FalhaBruta"/> e não
/// sobre <c>SqlException</c> porque essa exceção não tem construtor público.
/// </summary>
public sealed class ExtratorErrosTests
{
    private static readonly Etapa Qualquer = new("contagens do catálogo", "catalogo_sugestoes_contagens.sql");

    private static FalhaBruta Sql(int numero, bool conexaoJaAberta = true) =>
        new(typeof(InvalidOperationException), $"erro sql {numero}", numero, conexaoJaAberta, "detalhe completo");

    private static ExtratorErro Classificar(FalhaBruta falha) =>
        ClassificadorDeFalha.Classificar(falha, Qualquer, TimeSpan.FromSeconds(129));

    [Fact]
    public void Logon_trigger_tem_erro_proprio()
    {
        Classificar(Sql(17892, conexaoJaAberta: false)).Should().BeOfType<LogonTriggerErro>();
    }

    [Fact]
    public void Logon_trigger_cita_o_application_name_porque_a_mensagem_do_sql_server_nao_cita()
    {
        Classificar(Sql(17892, conexaoJaAberta: false)).Message.Should().Contain("ApplicationName");
    }

    [Fact]
    public void Timeout_de_comando_vira_tempo_excedido()
    {
        Classificar(Sql(-2)).Should().BeOfType<TempoExcedidoErro>();
    }

    [Fact]
    public void Queda_no_meio_da_consulta_nao_manda_conferir_servidor_e_porta()
    {
        // O erro de transporte visto na Natus em 2026-08-04: servidor e porta
        // estavam certos, a conexão caiu depois de 2min09. Mandar conferir os dois
        // joga o operador na direção errada.
        var erro = Classificar(Sql(-1, conexaoJaAberta: true));

        erro.Should().BeOfType<ConexaoPerdidaErro>();
        erro.Message.Should().NotContain("porta");
        erro.Transitorio.Should().BeTrue();
    }

    [Fact]
    public void Falha_ao_abrir_manda_conferir_servidor_porta_e_banco()
    {
        // 1433 vs 1435: algo responde na porta errada e o logon falha com uma
        // mensagem que não menciona porta nenhuma.
        var erro = Classificar(Sql(-1, conexaoJaAberta: false));

        erro.Should().BeOfType<ConexaoErro>();
        erro.Message.Should().Contain("porta");
    }

    [Theory]
    [InlineData(18456)]
    [InlineData(4060)]
    public void Credencial_e_banco_inacessivel_sao_falha_de_conexao_mesmo_com_conexao_aberta(int numero)
    {
        Classificar(Sql(numero, conexaoJaAberta: true)).Should().BeOfType<ConexaoErro>();
    }

    [Fact]
    public void Deadlock_e_transitorio()
    {
        var erro = Classificar(Sql(1205));

        erro.Should().BeOfType<ConcorrenciaErro>();
        erro.Transitorio.Should().BeTrue();
    }

    [Fact]
    public void Conversao_de_tipo_aponta_a_query_e_a_falta_do_convert()
    {
        // "Unable to cast object of type 'System.Decimal' to type 'System.Int32'"
        // não nomeia query nem coluna, e todo numérico do PBS é numeric(p,s).
        var falha = new FalhaBruta(typeof(InvalidCastException), "cast inválido", null, true, "detalhe");

        var erro = Classificar(falha);

        erro.Should().BeOfType<EtapaErro>();
        erro.Message.Should().Contain("catalogo_sugestoes_contagens.sql");
        erro.Message.Should().Contain("CONVERT");
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Falha_de_io_numa_etapa_de_query_vira_etapa_erro_e_nao_erro_de_escrita(Type tipo)
    {
        // Qualquer (contagem do catálogo) tem QueryFile: é leitura de rede, não
        // escrita em disco. "Confira espaço em disco" é conselho errado aqui.
        var falha = new FalhaBruta(tipo, "conexão caiu no meio da leitura", null, false, "detalhe");

        Classificar(falha).Should().BeOfType<EtapaErro>();
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Falha_de_io_numa_etapa_sem_query_file_vira_erro_de_escrita(Type tipo)
    {
        // Etapa sem QueryFile é o ZIP (ver ExtractionService.Run) -- aqui sim
        // "confira espaço em disco" é o conselho certo.
        var etapaDoZip = new Etapa("extração", null);
        var falha = new FalhaBruta(tipo, "disco", null, false, "detalhe");

        ClassificadorDeFalha.Classificar(falha, etapaDoZip, TimeSpan.FromSeconds(1))
            .Should().BeOfType<EscritaErro>();
    }

    [Fact]
    public void Falha_de_transporte_sem_sqlexception_nenhuma_vira_erro_de_conexao()
    {
        // Queda tão bruta (host inalcançável, porta fechada) que nem chega a virar
        // SqlException -- o driver embrulha em Win32Exception puro, e isso não pode
        // cair no balde genérico de InesperadoErro.
        var falha = new FalhaBruta(typeof(Win32Exception), "No such host is known", null, false, "detalhe");

        Classificar(falha).Should().BeOfType<ConexaoErro>();
    }

    [Fact]
    public void Falha_com_tipo_de_transporte_e_numero_sql_classifica_pelo_numero_nao_pelo_tipo()
    {
        // Caso real: Tipo vem do inner (Win32Exception/IOException do transporte),
        // SqlNumber vem do SqlException que fica no topo da cadeia -- os dois nascem
        // de exceções diferentes agora, e a classificação não pode se confundir com
        // isso: ela decide pelo número, não pelo tipo.
        var falha = new FalhaBruta(typeof(Win32Exception), "An existing connection was forcibly closed", -1, ConexaoJaAberta: true, "detalhe");

        var erro = Classificar(falha);

        erro.Should().BeOfType<ConexaoPerdidaErro>();
        erro.Transitorio.Should().BeTrue();
    }

    [Fact]
    public void Falha_desconhecida_vira_inesperado_e_nao_engole_o_tipo()
    {
        var falha = new FalhaBruta(typeof(FormatException), "algo", null, false, "detalhe");

        var erro = Classificar(falha);

        erro.Should().BeOfType<InesperadoErro>();
        erro.Message.Should().Contain(nameof(FormatException));
    }

    [Fact]
    public void Todo_erro_classificado_carrega_etapa_query_e_duracao()
    {
        var erro = Classificar(Sql(-1));

        erro.Metadata[ExtratorErro.ChaveEtapa].Should().Be("contagens do catálogo");
        erro.Metadata[ExtratorErro.ChaveQuery].Should().Be("catalogo_sugestoes_contagens.sql");
        erro.Metadata[ExtratorErro.ChaveDuracao].Should().Be(129d);
        erro.Metadata[ExtratorErro.ChaveDetalhe].Should().Be("detalhe completo");
    }

    [Fact]
    public void Numero_sql_vai_para_a_metadata_quando_existe()
    {
        Classificar(Sql(1205)).Metadata[ExtratorErro.ChaveSqlNumber].Should().Be(1205);
    }

    [Fact]
    public void Falha_bruta_de_excecao_sem_sql_nao_inventa_numero()
    {
        var falha = FalhaBruta.De(new FormatException("x"), conexaoJaAberta: false);

        falha.SqlNumber.Should().BeNull();
        falha.Tipo.Should().Be(typeof(FormatException));
        falha.DetalheCompleto.Should().Contain(nameof(FormatException));
    }

    [Fact]
    public void Falha_bruta_desembrulha_a_causa_para_o_tipo_nao_ser_sempre_o_invólucro()
    {
        var falha = FalhaBruta.De(
            new InvalidOperationException("embrulho", new InvalidCastException("causa")),
            conexaoJaAberta: false);

        falha.Tipo.Should().Be(typeof(InvalidCastException));
    }

    [Fact]
    public void Falha_bruta_nao_inventa_numero_a_partir_do_tipo_do_inner_quando_nao_ha_sqlexception()
    {
        // Não dá para construir um SqlException real aqui (sem construtor público) --
        // o caminho em que o número sobrevive ao unwrap só é exercitado pelo CLI contra
        // o PBS real (ver relatório). O que se pina é a outra metade do contrato: uma
        // cadeia sem SqlException nenhuma, direta ou como inner, nunca fabrica número.
        var falha = FalhaBruta.De(
            new InvalidOperationException("embrulho", new IOException("io")),
            conexaoJaAberta: true);

        falha.SqlNumber.Should().BeNull();
    }

    [Fact]
    public void Erros_de_dominio_nao_sao_transitorios()
    {
        new SugestaoNaoEncontradaErro(4242).Transitorio.Should().BeFalse();
        new ContratoErro("vendas.csv", "coluna 3 é 'X', esperado 'Y'").Transitorio.Should().BeFalse();
        new JanelaInviavelErro("motivo").Transitorio.Should().BeFalse();
        new SugestaoSemItensErro(4242).Transitorio.Should().BeFalse();
        new EmpresaDivergeDeFilialErro(3).Transitorio.Should().BeFalse();
    }

    /// <summary>
    /// O recorte de lojas filtra por FILIAL (ver lojas_da_sugestao.sql e escopo_sugestao.sql),
    /// mas vendas/compras/promoções/estoque filtram por EMPRESA (ver
    /// AvisarOuRecusarDivergenciaEmpresaFilial em ExtractionService). Sem esta mensagem
    /// explicando a causa, o operador não tem como entender por que um recorte que parecia
    /// óbvio foi recusado.
    /// </summary>
    [Fact]
    public void Empresa_diverge_de_filial_explica_a_causa_e_a_saida()
    {
        var erro = new EmpresaDivergeDeFilialErro(7);

        erro.Message.Should().Contain("7");
        erro.Message.Should().Contain("FILIAL");
        erro.Message.Should().Contain("EMPRESA");
        erro.Message.Should().Contain("sem escolher");
    }

    [Fact]
    public void Sugestao_nao_encontrada_diz_o_id_e_como_conferir()
    {
        var erro = new SugestaoNaoEncontradaErro(4242);

        erro.Message.Should().Contain("4242");
        erro.Message.Should().Contain("--list");
    }

    [Fact]
    public void Erro_ou_fallback_devolve_o_erro_tipado_quando_existe()
    {
        var resultado = Result.Fail<int>(new SugestaoNaoEncontradaErro(9));

        resultado.ErroOuFallback().Should().BeOfType<SugestaoNaoEncontradaErro>();
    }

    [Fact]
    public void Erro_ou_fallback_nao_estoura_quando_o_erro_nao_e_tipado()
    {
        // Um IError de outra origem (fora do que os serviços deste projeto produzem)
        // não pode fazer .First() estourar InvalidOperationException num handler
        // async void, na frente do operador.
        var resultado = Result.Fail<int>(new Error("erro genérico de outra origem"));

        var erro = resultado.ErroOuFallback();

        erro.Should().BeOfType<InesperadoErro>();
        erro.Message.Should().Contain("erro genérico de outra origem");
    }
}
