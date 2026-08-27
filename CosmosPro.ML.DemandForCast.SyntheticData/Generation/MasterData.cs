using Bogus;
using CosmosPro.ML.DemandForCast.SyntheticData.Models;

namespace CosmosPro.ML.DemandForCast.SyntheticData.Generation;

internal static class MasterData
{
    // Pool de "moléculas" e categorias farma realistas — fonte para distribuir
    // os SKUs gerados. Não pretende ser exaustivo; cobre OTCs e prescrição comuns.
    private static readonly (string PrincipioAtivo, string Categoria, string Subcategoria, string ClasseTerapeutica)[] Principios =
    [
        ("Dipirona Sódica",      "OTC",            "Analgésicos",       "Analgésico"),
        ("Paracetamol",          "OTC",            "Analgésicos",       "Analgésico"),
        ("Ibuprofeno",           "OTC",            "Anti-inflamatórios","AINE"),
        ("Ácido Acetilsalicílico","OTC",           "Analgésicos",       "Antiagregante"),
        ("Omeprazol",            "OTC",            "Gástricos",         "IBP"),
        ("Ranitidina",           "OTC",            "Gástricos",         "Antagonista H2"),
        ("Losartana Potássica",  "Prescrição",     "Cardiovascular",    "Anti-hipertensivo"),
        ("Enalapril",            "Prescrição",     "Cardiovascular",    "Anti-hipertensivo"),
        ("Sinvastatina",         "Prescrição",     "Cardiovascular",    "Hipolipemiante"),
        ("Atorvastatina",        "Prescrição",     "Cardiovascular",    "Hipolipemiante"),
        ("Metformina",           "Prescrição",     "Endócrino",         "Antidiabético"),
        ("Glibenclamida",        "Prescrição",     "Endócrino",         "Antidiabético"),
        ("Levotiroxina Sódica",  "Prescrição",     "Endócrino",         "Hormônio tireoide"),
        ("Amoxicilina",          "Prescrição",     "Antibióticos",      "Penicilina"),
        ("Azitromicina",         "Prescrição",     "Antibióticos",      "Macrolídeo"),
        ("Ciprofloxacino",       "Prescrição",     "Antibióticos",      "Quinolona"),
        ("Loratadina",           "OTC",            "Antialérgicos",     "Anti-histamínico"),
        ("Cetirizina",           "OTC",            "Antialérgicos",     "Anti-histamínico"),
        ("Fluoxetina",           "Controlado",     "SNC",               "ISRS"),
        ("Sertralina",           "Controlado",     "SNC",               "ISRS"),
        ("Alprazolam",           "Controlado",     "SNC",               "Benzodiazepínico"),
        ("Clonazepam",           "Controlado",     "SNC",               "Benzodiazepínico"),
        ("Salbutamol",           "Prescrição",     "Respiratório",      "Broncodilatador"),
        ("Budesonida",           "Prescrição",     "Respiratório",      "Corticoide inalatório"),
        ("Vitamina D",           "OTC",            "Vitaminas",         "Suplemento"),
        ("Vitamina C",           "OTC",            "Vitaminas",         "Suplemento"),
        ("Complexo B",           "OTC",            "Vitaminas",         "Suplemento"),
    ];

    private static readonly string[] Fabricantes =
        ["EMS", "Eurofarma", "Aché", "Hypera", "Neo Química", "Medley", "Sanofi", "Bayer", "Pfizer", "GSK", "Novartis", "Cimed"];

    private static readonly string[] Apresentacoes =
        ["Cx 30 cps", "Cx 20 cps", "Cx 10 cps", "Fr 100ml", "Fr 200ml", "Cx 60 cps", "Bisn 30g", "Amp 5ml"];

    // 27 UFs.
    private static readonly string[] Ufs =
        ["AC","AL","AP","AM","BA","CE","DF","ES","GO","MA","MT","MS","MG","PA","PB","PR","PE","PI","RJ","RN","RS","RO","RR","SC","SP","SE","TO"];

    private static readonly (string Regiao, string UF)[] UfPorRegiao =
    [
        ("Norte","AC"),("Norte","AM"),("Norte","AP"),("Norte","PA"),("Norte","RO"),("Norte","RR"),("Norte","TO"),
        ("Nordeste","AL"),("Nordeste","BA"),("Nordeste","CE"),("Nordeste","MA"),("Nordeste","PB"),("Nordeste","PE"),("Nordeste","PI"),("Nordeste","RN"),("Nordeste","SE"),
        ("Centro-Oeste","DF"),("Centro-Oeste","GO"),("Centro-Oeste","MS"),("Centro-Oeste","MT"),
        ("Sudeste","ES"),("Sudeste","MG"),("Sudeste","RJ"),("Sudeste","SP"),
        ("Sul","PR"),("Sul","RS"),("Sul","SC"),
    ];

    public static List<LojaRow> BuildLojas(int n, int seed)
    {
        Randomizer.Seed = new Random(seed);
        var faker = new Faker<LojaRow>("pt_BR")
            .CustomInstantiator(f =>
            {
                var (regiao, uf) = f.PickRandom(UfPorRegiao);
                var perfil = f.PickRandom("Bairro", "Shopping", "Centro", "Hospital");
                return new LojaRow(
                    LojaId: 0, // preenchido abaixo
                    Nome: $"Drogaria {f.Name.LastName()}",
                    UF: uf,
                    Cidade: f.Address.City(),
                    Regiao: regiao,
                    Perfil: perfil,
                    DiasOperacaoSemana: (byte)f.Random.Int(6, 7),
                    DataAbertura: f.Random.Bool(0.7f)
                        ? DateOnly.FromDateTime(f.Date.Past(15))
                        : null,
                    Ativo: true,
                    Cnpj: "");
            });

        // CNPJ derivado do id, fora do faker: consumir o RNG global do Bogus aqui
        // deslocaria a sequência de tudo que é gerado depois com o mesmo seed — foi o
        // que quebrou o teste de concentração ABC quando o CNPJ nasceu como sorteio.
        return [.. Enumerable.Range(1, n).Select(id => faker.Generate() with
        {
            LojaId = id,
            Cnpj = $"{7381852 + id:D8}0001{(id * 7) % 100:D2}",
        })];
    }

    public static List<ProdutoRow> BuildProdutos(int n, int seed)
    {
        Randomizer.Seed = new Random(seed);
        var faker = new Faker("pt_BR");

        var produtos = new List<ProdutoRow>(n);
        for (int i = 0; i < n; i++)
        {
            var p = Principios[i % Principios.Length];
            var fabricante = faker.PickRandom(Fabricantes);
            var apresentacao = faker.PickRandom(Apresentacoes);
            var sku = $"SKU{i + 1:D5}";
            var nome = $"{p.PrincipioAtivo} {fabricante} {apresentacao}";

            // ListaControle: só preenchido pra controlados.
            var listaControle = p.Categoria == "Controlado"
                ? faker.PickRandom("B1", "C1", "A3")
                : null;

            produtos.Add(new ProdutoRow(
                Sku: sku,
                Nome: nome,
                Categoria: p.Categoria,
                Subcategoria: p.Subcategoria,
                Fabricante: fabricante,
                PrincipioAtivo: p.PrincipioAtivo,
                Apresentacao: apresentacao,
                Ean: faker.Random.ReplaceNumbers("############"),
                RegistroAnvisa: faker.Random.Bool(0.8f)
                    ? faker.Random.ReplaceNumbers("##########") : null,
                ListaControle: listaControle,
                ClasseTerapeutica: p.ClasseTerapeutica,
                Ativo: true));
        }
        return produtos;
    }
}
