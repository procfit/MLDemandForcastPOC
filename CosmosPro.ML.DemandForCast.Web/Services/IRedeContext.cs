namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Resolve em qual rede a operação atua.
/// <para>
/// Para <c>UsuarioRede</c> vem do claim e é imutável na sessão. Para
/// <c>PowerUser</c> vem de seleção explícita, o que é legítimo porque ele é
/// autorizado em todas as redes.
/// </para>
/// <para>
/// <b>Nenhum caminho lê rede de rota, query string ou campo de formulário.</b> Era
/// assim em F10 (andaime) e é o que esta abstração existe para eliminar: com o valor
/// vindo do request, trocar um número na URL dava acesso ao dado de outro cliente.
/// </para>
/// </summary>
public interface IRedeContext
{
    /// <summary>
    /// Rede em que a operação vai atuar. <b>Lança</b> se não houver rede resolvida —
    /// um default silencioso aqui seria exatamente o vazamento cross-tenant que
    /// F10 e F11 existem para impedir.
    /// </summary>
    Task<int> GetRedeIdAtualAsync();

    Task<bool> EhPowerUserAsync();

    /// <summary>Se o usuário corrente pode operar sobre a rede informada.</summary>
    Task<bool> PodeAcessarAsync(int redeId);

    /// <summary>
    /// Troca a rede ativa. Só tem efeito para PowerUser; para usuário de rede é
    /// no-op, porque o escopo dele vem do claim.
    /// </summary>
    Task SelecionarRedeAsync(int redeId);
}
