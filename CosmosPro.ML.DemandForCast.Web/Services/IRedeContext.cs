namespace CosmosPro.ML.DemandForCast.Web.Services;

/// <summary>
/// Resolve em qual rede a operação atua.
/// <para>
/// Para <c>UsuarioRede</c> vem do cadastro e é imutável na sessão. Para
/// <c>PowerUser</c> vem de escolha explícita, o que é legítimo porque ele é
/// autorizado em todas as redes.
/// </para>
/// <para>
/// A escolha do PowerUser é gravada em <b>claim do cookie de autenticação</b> por
/// <c>POST /api/auth/rede</c>, não por método desta abstração: em Blazor Server ela é
/// <c>scoped</c> (por circuito), e trocar de rede exige recarregar a página — um campo
/// no serviço seria destruído junto com o circuito que o guardava.
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

    /// <summary>
    /// Id do usuário autenticado no circuito atual. Usado para preencher campos de
    /// auditoria (ex.: <c>CargaStage.UsuarioId</c>) — nunca para decidir escopo, que
    /// é sempre <see cref="GetRedeIdAtualAsync"/>.
    /// </summary>
    Task<Guid> GetUsuarioIdAtualAsync();

    Task<bool> EhPowerUserAsync();

    /// <summary>Se o usuário corrente pode operar sobre a rede informada.</summary>
    Task<bool> PodeAcessarAsync(int redeId);
}
