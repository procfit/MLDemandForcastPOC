using Microsoft.AspNetCore.Identity;

namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Usuário do sistema.
/// <para>
/// <see cref="RedeId"/> nulo ⇒ <see cref="Papeis.PowerUser"/>, acesso global.
/// <see cref="RedeId"/> preenchido ⇒ usuário operacional, restrito à sua rede.
/// </para>
/// <para>
/// A coerência entre papel e RedeId é validada na aplicação, não no banco: papéis
/// vivem numa tabela de junção do Identity, fora do alcance de um CHECK constraint.
/// </para>
/// </summary>
public sealed class Usuario : IdentityUser<Guid>
{
    public int? RedeId { get; set; }

    public required string NomeCompleto { get; set; }

    /// <summary>
    /// Desativado não consegue logar. Preferido a excluir, para preservar a
    /// autoria das cargas e jobs que o usuário disparou.
    /// </summary>
    public bool Ativo { get; set; } = true;

    public DateTimeOffset CriadoEm { get; set; }
}

public static class Papeis
{
    public const string PowerUser = "PowerUser";
    public const string UsuarioRede = "UsuarioRede";

    public static readonly string[] Todos = [PowerUser, UsuarioRede];
}
