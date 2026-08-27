namespace CosmosPro.ML.DemandForCast.SyntheticData.Models;

// DTOs internos espelhando o schema declarado em
// CosmosPro.ML.DemandForCast.Worker.TableSchemas. Os nomes/tipos das propriedades
// DEVEM bater com as colunas que o Worker espera ler do CSV.

internal sealed record LojaRow(
    int LojaId,
    string Nome,
    string UF,
    string Cidade,
    string Regiao,
    string Perfil,
    byte DiasOperacaoSemana,
    DateOnly? DataAbertura,
    bool Ativo,
    string Cnpj);

internal sealed record ProdutoRow(
    string Sku,
    string Nome,
    string Categoria,
    string Subcategoria,
    string Fabricante,
    string PrincipioAtivo,
    string Apresentacao,
    string? Ean,
    string? RegistroAnvisa,
    string? ListaControle,
    string ClasseTerapeutica,
    bool Ativo);

internal sealed record VendaRow(
    DateOnly Data,
    int LojaId,
    string Sku,
    decimal Quantidade,
    decimal PrecoUnitario,
    decimal ValorTotal);

internal sealed record EstoqueDiarioRow(
    DateOnly Data,
    int LojaId,
    string Sku,
    decimal QuantidadeEmEstoque);

internal sealed record CompraRow(
    DateOnly DataPedido,
    DateOnly? DataRecebimento,
    int LojaId,
    string Sku,
    decimal Quantidade,
    string Fornecedor);

internal sealed record PromocaoRow(
    DateOnly DataInicio,
    DateOnly DataFim,
    string Sku,
    int? LojaId,
    string Tipo,
    decimal DescontoPct);
