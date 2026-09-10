using System.Text.Json;

namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// O extrator é um **asset desta imagem**, não um arquivo publicado à parte. O CI baixa o
/// par `extrator.exe` + `manifesto.json` do job que os constrói no Windows e os deixa em
/// <c>Assets/extrator/</c> antes de a imagem ser montada, então o executável que o comprador
/// baixa vem sempre do mesmo commit que o backend que o atende.
///
/// Isso substituiu um bucket no MinIO alimentado por um endpoint autenticado por token, e o
/// que se ganhou não foi economia de código: era possível o extrator estar à frente ou atrás
/// do backend, e toda a máquina que existia — portão de versão, guarda de bump, token,
/// variável de ambiente nos dois lados — servia para tornar essa defasagem administrável.
/// Sendo o mesmo artefato, a defasagem deixa de existir e nada precisa administrá-la.
///
/// <para>
/// O arquivo mora **fora de wwwroot** de propósito: <c>MapStaticAssets</c> serviria os
/// ~118 MB sem autenticação a quem descobrisse a URL, e o download é de comprador logado
/// (ver <see cref="ExtratorEndpoints"/>).
/// </para>
/// </summary>
internal sealed class ExtratorEmbutido
{
    internal const string PastaRelativa = "Assets/extrator";
    internal const string NomeExecutavel = "extrator.exe";
    internal const string NomeManifesto = "manifesto.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ExtratorEmbutido(IWebHostEnvironment ambiente, ILogger<ExtratorEmbutido> logger)
    {
        var pasta = Path.Combine(ambiente.ContentRootPath, PastaRelativa);
        CaminhoDoExecutavel = Path.Combine(pasta, NomeExecutavel);

        // Lido uma vez, no startup, porque este serviço é singleton e o conteúdo é imutável
        // dentro da imagem. No F5 a pasta normalmente não existe (quem a preenche é o CI),
        // e aí `Versao` fica nula e a tela diz que não há extrator disponível — o mesmo
        // estado que um ambiente novo tinha antes de alguém publicar. Para exercitar o
        // caminho localmente, publique o extrator à mão nessa pasta e reinicie.
        Versao = LerManifesto(Path.Combine(pasta, NomeManifesto), logger);

        if (Versao is null || !File.Exists(CaminhoDoExecutavel))
        {
            logger.LogWarning(
                "Extrator não embutido nesta imagem: esperava {Exe} e {Manifesto}. O download ficará indisponível.",
                CaminhoDoExecutavel, Path.Combine(pasta, NomeManifesto));
        }
        else
        {
            logger.LogInformation("Extrator embutido: versao={Versao} sha256={Sha}",
                Versao.Versao, Versao.Sha256);
        }
    }

    public string CaminhoDoExecutavel { get; }

    /// <summary>
    /// Nulo quando o extrator não está embutido — estado normal do F5. Nunca significa
    /// "falha ao consultar": não há consulta remota aqui, e é por isso que a tela perdeu a
    /// distinção entre "não publicado" e "não consegui perguntar" que existia quando este
    /// dado vinha da apiservice pela rede.
    /// </summary>
    public ExtratorVersaoView? Versao { get; }

    /// <summary>
    /// Confere o arquivo, e não só o manifesto: os dois são copiados pelo mesmo passo do CI,
    /// mas quem responde ao download é o executável, e prometer um download que responde 404
    /// é pior do que dizer que não há extrator.
    /// </summary>
    public bool Disponivel => Versao is not null && File.Exists(CaminhoDoExecutavel);

    private static ExtratorVersaoView? LerManifesto(string caminho, ILogger logger)
    {
        if (!File.Exists(caminho)) return null;

        try
        {
            // `ReadAllText` e nao `ReadAllBytes`: ele detecta e descarta o BOM, e a sobrecarga
            // de `Deserialize` sobre bytes NAO pula BOM -- ela estoura com "input does not
            // begin with a valid JSON token". O manifesto e escrito por `Set-Content -Encoding
            // utf8` do pwsh, que hoje nao emite BOM, mas um manifesto com BOM sumiria em
            // silencio: o catch abaixo o trataria como ausente e a tela diria ao comprador que
            // esta instalacao nao traz o extrator.
            var manifesto = JsonSerializer.Deserialize<ExtratorVersaoView>(
                File.ReadAllText(caminho), JsonOptions);

            if (manifesto is null
                || string.IsNullOrWhiteSpace(manifesto.Versao)
                || string.IsNullOrWhiteSpace(manifesto.Sha256))
            {
                logger.LogError("{Manifesto} não declara versao/sha256; o extrator será tratado como ausente.", caminho);
                return null;
            }

            return manifesto;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Tratar como ausente, e não estourar o startup: um manifesto ilegível tira o
            // download do ar, mas não pode derrubar a aplicação inteira — o resto dela não
            // depende do extrator.
            logger.LogError(ex, "Falha ao ler {Manifesto}; o extrator será tratado como ausente.", caminho);
            return null;
        }
    }
}

/// <summary>
/// Espelha o <c>manifesto.json</c> que o CI escreve ao lado do executável.
/// <c>GeradoEm</c> é o instante do build, e é o nome honesto: quando o executável era
/// enviado à mão para um bucket, o servidor sobrescrevia o campo com o instante da
/// publicação — hoje não há publicação separada do build.
/// </summary>
internal sealed record ExtratorVersaoView(string Versao, string Sha256, DateTimeOffset GeradoEm);
