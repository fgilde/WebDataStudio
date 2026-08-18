using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Editing;

namespace WebDataStudio.Server.Tests.Editing;

public class RowIdentityTests
{
    private static ObjectDetail Table(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<IndexInfo>? indexes = null,
        SchemaNodeKind kind = SchemaNodeKind.Table) =>
        new(new SchemaNodeRef(kind, ["public", "people"]), columns, indexes ?? [], [], [], null, null, null, null);

    private static ColumnInfo Column(string name, bool pk = false, bool nullable = false) =>
        new(name, "text", nullable, null, pk, false, null, 1);

    [Fact]
    public void Prefers_the_primary_key()
    {
        var result = RowIdentity.Resolve(Table([Column("id", pk: true), Column("name")]));

        Assert.True(result.Editable);
        Assert.Equal(["id"], result.KeyColumns);
    }

    [Fact]
    public void Falls_back_to_a_unique_index_over_non_nullable_columns()
    {
        var result = RowIdentity.Resolve(Table(
            [Column("code"), Column("name")],
            [new IndexInfo("ux_code", ["code"], Unique: true, Primary: false, Filter: null)]));

        Assert.True(result.Editable);
        Assert.Equal(["code"], result.KeyColumns);
    }

    [Fact]
    public void Ignores_a_unique_index_that_allows_nulls()
    {
        var result = RowIdentity.Resolve(Table(
            [Column("code", nullable: true)],
            [new IndexInfo("ux_code", ["code"], Unique: true, Primary: false, Filter: null)]));

        Assert.False(result.Editable);
    }

    [Fact]
    public void Reports_why_a_key_less_table_cannot_be_edited()
    {
        var result = RowIdentity.Resolve(Table([Column("name")]));

        Assert.False(result.Editable);
        Assert.Contains("no primary key", result.Reason);
    }

    [Fact]
    public void A_view_is_not_editable()
    {
        var result = RowIdentity.Resolve(Table([Column("id", pk: true)], null, SchemaNodeKind.View));
        Assert.False(result.Editable);
    }
}

public class ChangeSetHashTests
{
    private static ChangeSet Set(params RowChange[] changes) => new("c1", "Table:public/people", changes);

    [Fact]
    public void Is_stable_across_property_ordering()
    {
        var a = Set(new RowChange("update",
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["name"] = "ada", ["age"] = 36 }));

        var b = Set(new RowChange("update",
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["age"] = 36, ["name"] = "ada" }));

        Assert.Equal(a.Hash(), b.Hash());
    }

    [Fact]
    public void Changes_when_a_value_changes()
    {
        var a = Set(new RowChange("update", new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["name"] = "ada" }));
        var b = Set(new RowChange("update", new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["name"] = "linus" }));

        Assert.NotEqual(a.Hash(), b.Hash());
    }

    [Fact]
    public void Distinguishes_null_from_the_text_null()
    {
        var a = Set(new RowChange("update", new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["name"] = null }));
        var b = Set(new RowChange("update", new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["name"] = "null" }));

        Assert.NotEqual(a.Hash(), b.Hash());
    }
}

public class ChangeScriptBuilderTests
{
    private static readonly ObjectDetail Detail = new(
        new SchemaNodeRef(SchemaNodeKind.Table, ["public", "people"]),
        [new ColumnInfo("id", "int", false, null, true, false, null, 1),
         new ColumnInfo("name", "text", true, null, false, false, null, 2)],
        [], [], [], null, null, null, null);

    private static ChangeScript Build(SqlDialect dialect, params RowChange[] changes) =>
        ChangeScriptBuilder.Build(new ChangeSet("c1", "Table:public/people", changes), Detail, dialect);

    private static RowChange Update(object key, string column, object? value) =>
        new("update", new Dictionary<string, object?> { ["id"] = key },
            new Dictionary<string, object?> { [column] = value });

    [Fact]
    public void Update_sets_only_the_changed_column()
    {
        var script = Build(new PostgreSqlDialect(), Update(1, "name", "ada"));
        var statement = Assert.Single(script.Statements);

        Assert.Equal("UPDATE \"public\".\"people\" SET \"name\" = @p0 WHERE \"id\" = @k0", statement.Sql);
        Assert.Equal("ada", statement.Parameters["p0"]);
        Assert.Equal(1, statement.Parameters["k0"]);
    }

    [Fact]
    public void Quotes_identifiers_per_dialect()
    {
        Assert.Contains("`people`", Build(new MySqlDialect(), Update(1, "name", "ada")).Statements[0].Sql);
        Assert.Contains("[people]", Build(new SqlServerDialect(), Update(1, "name", "ada")).Statements[0].Sql);
    }

    [Fact]
    public void Insert_lists_exactly_the_supplied_columns()
    {
        var script = Build(new PostgreSqlDialect(), new RowChange("insert",
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["id"] = 9, ["name"] = "grace" }));

        Assert.Contains("INSERT INTO \"public\".\"people\" (\"id\", \"name\") VALUES (@p0, @p1)",
            script.Statements[0].Sql);
    }

    [Fact]
    public void Delete_matches_every_key_column_and_is_marked_destructive()
    {
        var script = Build(new PostgreSqlDialect(), new RowChange("delete",
            new Dictionary<string, object?> { ["id"] = 1, ["tenant"] = "acme" },
            new Dictionary<string, object?>()));

        var statement = Assert.Single(script.Statements);
        Assert.Contains("WHERE \"id\" = @k0 AND \"tenant\" = @k1", statement.Sql);
        Assert.True(statement.Destructive);
    }

    [Fact]
    public void Null_travels_as_a_parameter_not_as_the_word_null()
    {
        var statement = Build(new PostgreSqlDialect(), Update(1, "name", null)).Statements[0];

        Assert.Null(statement.Parameters["p0"]);
        Assert.Contains("SET \"name\" = @p0", statement.Sql);
    }

    [Fact]
    public void Preview_text_is_fully_substituted_and_readable()
    {
        var script = Build(new PostgreSqlDialect(), Update(1, "name", "it's"));

        Assert.Contains("UPDATE \"public\".\"people\" SET \"name\" = 'it''s' WHERE \"id\" = 1;", script.Text);
        Assert.DoesNotContain("@p0", script.Text);
    }

    [Fact]
    public void Preview_substitutes_double_digit_parameters_correctly()
    {
        var values = Enumerable.Range(0, 12).ToDictionary(i => $"c{i}", i => (object?)i);
        var script = Build(new PostgreSqlDialect(),
            new RowChange("insert", new Dictionary<string, object?>(), values));

        Assert.DoesNotContain("@p", script.Text);
    }

    [Fact]
    public void Orders_deletes_before_updates_before_inserts()
    {
        var script = Build(new PostgreSqlDialect(),
            new RowChange("insert", new Dictionary<string, object?>(), new Dictionary<string, object?> { ["id"] = 3 }),
            Update(1, "name", "ada"),
            new RowChange("delete", new Dictionary<string, object?> { ["id"] = 2 }, new Dictionary<string, object?>()));

        Assert.StartsWith("DELETE", script.Statements[0].Sql);
        Assert.StartsWith("UPDATE", script.Statements[1].Sql);
        Assert.StartsWith("INSERT", script.Statements[2].Sql);
    }
}
