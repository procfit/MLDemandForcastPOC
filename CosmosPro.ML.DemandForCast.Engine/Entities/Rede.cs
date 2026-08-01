namespace CosmosPro.ML.DemandForCast.Engine.Entities;

/// <summary>
/// Inquilino do sistema (rede de farmácia). Registro criado no onboarding, antes
/// de qualquer import.
/// <para>
/// Esta é a <b>fonte de verdade</b> do RedeId. A tabela <c>Stage.dbo.Redes</c> é
/// projeção desta, sincronizada pelo Worker no início de cada import — FK entre
/// bancos não existe no SQL Server, e o Stage precisa de uma âncora local para
/// as FKs das tabelas de dado.
/// </para>
/// </summary>
public sealed class Rede
{
    public int Id { get; set; }

    public required string Nome { get; set; }

    /// <summary>Identificador curto e estável. Usado em prefixo de blob no MinIO.</summary>
    public required string Slug { get; set; }

    /// <summary>Raiz do CNPJ (8 dígitos) ou CNPJ completo. Informativo.</summary>
    public string? CnpjRaiz { get; set; }

    public bool Ativo { get; set; } = true;

    public DateTimeOffset CriadoEm { get; set; }
}
