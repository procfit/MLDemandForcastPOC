using Bogus;

namespace CosmosPro.ML.DemandForCast.Tests.Shared.Fakers;

public sealed record LojaRow(
    int LojaId,
    string Nome,
    string UF,
    string Cidade,
    string? Regiao,
    string? Perfil,
    byte DiasOperacaoSemana,
    DateOnly? DataAbertura,
    bool Ativo,
    string? Cnpj = null);

public sealed class LojaFaker : Faker<LojaRow>
{
    private static readonly string[] UFs = ["SP", "RJ", "MG", "RS", "PR", "BA", "PE", "CE", "DF"];
    private static readonly string[] Perfis = ["rua", "shopping", "popular", "premium"];

    public LojaFaker(int seed = 42)
    {
        UseSeed(seed);
        CustomInstantiator(f =>
        {
            var uf = f.PickRandom(UFs);
            return new LojaRow(
                LojaId: f.IndexFaker + 1,
                Nome: $"Loja {f.Address.City()}",
                UF: uf,
                Cidade: f.Address.City(),
                Regiao: f.Random.Bool(0.7f) ? $"{uf} {f.PickRandom("capital", "interior", "metropolitana")}" : null,
                Perfil: f.Random.Bool(0.8f) ? f.PickRandom(Perfis) : null,
                DiasOperacaoSemana: (byte)f.PickRandom(6, 7),
                DataAbertura: f.Random.Bool(0.9f)
                    ? DateOnly.FromDateTime(f.Date.Past(10, DateTime.UtcNow))
                    : null,
                Ativo: f.Random.Bool(0.95f),
                // Derivado do índice, sem consumir o RNG: sorteio aqui deslocaria a
                // sequência de todos os fakers seedados que rodam depois deste.
                Cnpj: $"{7381852 + f.IndexFaker + 1:D8}0001{(f.IndexFaker * 7) % 100:D2}");
        });
    }
}
