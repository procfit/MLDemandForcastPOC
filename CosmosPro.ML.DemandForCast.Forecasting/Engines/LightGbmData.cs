using CosmosPro.ML.DemandForCast.Features.Models;

namespace CosmosPro.ML.DemandForCast.Forecasting.Engines;

/// <summary>
/// Linha de entrada do pipeline ML.NET. Propriedades viram colunas: float = numérica,
/// string = categórica (one-hot no pipeline). Mantida explícita (CLAUDE.md §3 —
/// evitar DataView anônimo). Booleanos viram 0/1 float para entrar no Concatenate.
/// </summary>
public sealed class LightGbmInput
{
    public float Label { get; set; }

    /// <summary>
    /// Rótulo do portão do <see cref="HurdleForecastEngine"/>: houve venda no
    /// dia-alvo.
    ///
    /// <para>
    /// <b>É rótulo, nunca feature.</b> Está deliberadamente fora de
    /// <see cref="NumericColumns"/>: entrar ali entregaria a resposta ao modelo — o
    /// regressor leria "vendeu" para prever quanto vendeu, e o WAPE despencaria de
    /// forma que nenhum teste de erro acusaria como leakage. É a mesma classe de
    /// armadilha do preço realizado descrita no FeatureBuilder.
    /// </para>
    /// </summary>
    public bool Vendeu { get; set; }

    // Numéricas
    public float Lag7 { get; set; }
    public float Lag14 { get; set; }
    public float Lag21 { get; set; }
    public float Lag28 { get; set; }
    public float RollMean7 { get; set; }
    public float RollMean28 { get; set; }
    public float RollStd28 { get; set; }
    public float RollMax28 { get; set; }
    public float DiaDaSemana { get; set; }
    public float DiaDoMes { get; set; }
    public float Mes { get; set; }
    public float FimDeSemana { get; set; }
    public float Feriado { get; set; }
    public float EmPromocao { get; set; }
    public float DiasDesdeUltimaPromo { get; set; }
    public float PrecoUnitario { get; set; }
    public float PrecoRelativoMedia { get; set; }

    // Categóricas (one-hot no pipeline)
    public string Sku { get; set; } = "";
    public string Categoria { get; set; } = "";
    public string PrincipioAtivo { get; set; } = "";
    public string ClasseAbc { get; set; } = "";
    public string UF { get; set; } = "";
    public string Regiao { get; set; } = "";
    public string PerfilLoja { get; set; } = "";

    public static readonly string[] NumericColumns =
    [
        nameof(Lag7), nameof(Lag14), nameof(Lag21), nameof(Lag28),
        nameof(RollMean7), nameof(RollMean28), nameof(RollStd28), nameof(RollMax28),
        nameof(DiaDaSemana), nameof(DiaDoMes), nameof(Mes),
        nameof(FimDeSemana), nameof(Feriado),
        nameof(EmPromocao), nameof(DiasDesdeUltimaPromo),
        nameof(PrecoUnitario), nameof(PrecoRelativoMedia),
    ];

    public static readonly string[] CategoricalColumns =
    [
        nameof(Sku), nameof(Categoria), nameof(PrincipioAtivo),
        nameof(ClasseAbc), nameof(UF), nameof(Regiao), nameof(PerfilLoja),
    ];

    public static LightGbmInput From(FeatureVector f) => new()
    {
        Label = (float)f.Target,
        Vendeu = f.Target > 0m,
        Lag7 = (float)f.Lag7,
        Lag14 = (float)f.Lag14,
        Lag21 = (float)f.Lag21,
        Lag28 = (float)f.Lag28,
        RollMean7 = (float)f.RollMean7,
        RollMean28 = (float)f.RollMean28,
        RollStd28 = (float)f.RollStd28,
        RollMax28 = (float)f.RollMax28,
        DiaDaSemana = f.DiaDaSemana,
        DiaDoMes = f.DiaDoMes,
        Mes = f.Mes,
        FimDeSemana = f.FimDeSemana ? 1f : 0f,
        Feriado = f.Feriado ? 1f : 0f,
        EmPromocao = f.EmPromocao ? 1f : 0f,
        DiasDesdeUltimaPromo = f.DiasDesdeUltimaPromo,
        PrecoUnitario = (float)f.PrecoUnitario,
        PrecoRelativoMedia = (float)f.PrecoRelativoMedia,
        Sku = f.Sku,
        Categoria = f.Categoria,
        PrincipioAtivo = f.PrincipioAtivo,
        ClasseAbc = f.ClasseAbc,
        UF = f.UF,
        Regiao = f.Regiao,
        PerfilLoja = f.PerfilLoja,
    };
}

/// <summary>Saída do modelo de regressão ML.NET (a coluna prevista é "Score").</summary>
public sealed class LightGbmOutput
{
    public float Score { get; set; }
}
