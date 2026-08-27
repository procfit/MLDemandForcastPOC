namespace CosmosPro.ML.DemandForCast.Worker.Tests;

public sealed class TableSchemasParseTests
{
    private static readonly TableSchemas.Column IntNotNull = new("LojaId", typeof(int), Nullable: false);
    private static readonly TableSchemas.Column DecimalNotNull = new("Quantidade", typeof(decimal), Nullable: false);
    private static readonly TableSchemas.Column DateNotNull = new("Data", typeof(DateTime), Nullable: false);
    private static readonly TableSchemas.Column StringNotNull = new("Nome", typeof(string), Nullable: false);
    private static readonly TableSchemas.Column StringNullable = new("Regiao", typeof(string), Nullable: true);
    private static readonly TableSchemas.Column BoolNotNull = new("Ativo", typeof(bool), Nullable: false);
    private static readonly TableSchemas.Column ByteNotNull = new("DiasOperacaoSemana", typeof(byte), Nullable: false);

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    public void Parse_bit_aceita_0_1_e_true_false(string raw, bool expected)
    {
        TableSchemas.Parse(BoolNotNull, raw).Should().Be(expected);
    }

    [Fact]
    public void Parse_bit_com_valor_invalido_joga_FormatException()
    {
        Action act = () => TableSchemas.Parse(BoolNotNull, "sim");
        act.Should().Throw<FormatException>().WithMessage("*bit*");
    }

    [Fact]
    public void Parse_int_converte_corretamente()
    {
        TableSchemas.Parse(IntNotNull, "42").Should().Be(42);
    }

    [Fact]
    public void Parse_decimal_usa_invariant_culture_ponto_como_separador_decimal()
    {
        TableSchemas.Parse(DecimalNotNull, "123.456").Should().Be(123.456m);
    }

    [Fact]
    public void Parse_DateTime_ISO_format()
    {
        TableSchemas.Parse(DateNotNull, "2026-05-20").Should().Be(new DateTime(2026, 5, 20));
    }

    [Fact]
    public void Parse_string_remove_aspas_e_whitespace()
    {
        TableSchemas.Parse(StringNotNull, "  \"Hello World\"  ").Should().Be("Hello World");
    }

    [Fact]
    public void Parse_vazio_em_coluna_nullable_retorna_DBNull()
    {
        TableSchemas.Parse(StringNullable, "").Should().Be(DBNull.Value);
        TableSchemas.Parse(StringNullable, "   ").Should().Be(DBNull.Value);
    }

    [Fact]
    public void Parse_vazio_em_coluna_obrigatoria_joga_FormatException()
    {
        Action act = () => TableSchemas.Parse(IntNotNull, "");
        act.Should().Throw<FormatException>().WithMessage("*LojaId*");
    }

    [Fact]
    public void Parse_byte_converte_corretamente()
    {
        TableSchemas.Parse(ByteNotNull, "7").Should().Be((byte)7);
    }
}

public sealed class TableSchemasBuildEmptyTests
{
    [Fact]
    public void BuildEmpty_Vendas_cria_DataTable_com_7_colunas_tipadas()
    {
        var dt = TableSchemas.BuildEmpty("Vendas");

        dt.TableName.Should().Be("Vendas");
        // 6 colunas do CSV + RedeId, que o Worker injeta (não vem no arquivo).
        dt.Columns.Count.Should().Be(7);
        dt.Columns["RedeId"]!.DataType.Should().Be(typeof(int));
        dt.Columns["Data"]!.DataType.Should().Be(typeof(DateTime));
        dt.Columns["LojaId"]!.DataType.Should().Be(typeof(int));
        dt.Columns["Sku"]!.DataType.Should().Be(typeof(string));
        dt.Columns["Quantidade"]!.DataType.Should().Be(typeof(decimal));
        dt.Rows.Count.Should().Be(0);
    }

    [Fact]
    public void RedeId_e_ServerSupplied_em_todas_as_tabelas()
    {
        foreach (var (tabela, colunas) in TableSchemas.ByTable)
        {
            var redeId = colunas.SingleOrDefault(c => c.Name == "RedeId");
            redeId.Should().NotBeNull($"{tabela} precisa de RedeId para o isolamento por rede");
            redeId!.ServerSupplied.Should().BeTrue(
                $"{tabela}.RedeId não pode vir do CSV — o cliente reivindicaria a rede de outro");
            redeId.Nullable.Should().BeFalse();
        }
    }

    [Fact]
    public void BuildEmpty_define_AllowDBNull_conforme_schema()
    {
        var dt = TableSchemas.BuildEmpty("Lojas");

        dt.Columns["LojaId"]!.AllowDBNull.Should().BeFalse();
        dt.Columns["Regiao"]!.AllowDBNull.Should().BeTrue();
        dt.Columns["Ativo"]!.AllowDBNull.Should().BeFalse();
    }

    [Fact]
    public void Todas_as_tabelas_do_zip_estao_definidas()
    {
        var expectedTables = new[] { "Lojas", "Produtos", "Vendas", "EstoquesDiarios", "Compras", "Promocoes" };
        foreach (var t in expectedTables)
        {
            TableSchemas.ByTable.Should().ContainKey(t);
        }
    }
}
