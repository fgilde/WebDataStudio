using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Editing;

namespace WebDataStudio.Server.Tests.Editing;

public class RowIdentityTests
{
    private static ObjectDetail Table(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<IndexInfo>? indexes = null,
        SchemaNodeKind kind = SchemaNodeKind.Table, bool partitioned = false) =>
        new(new SchemaNodeRef(kind, ["public", "people"]), columns, indexes ?? [], [], [], null, null,
            null, null, partitioned);

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

    [Fact]
    public void A_key_less_table_is_editable_by_where_the_row_physically_is()
    {
        var result = RowIdentity.Resolve(Table([Column("name")]), new PostgreSqlDialect());

        Assert.True(result.Editable);
        Assert.Equal([RowIdentity.AddressColumn], result.KeyColumns);
        Assert.Equal("ctid", result.RowAddress);

        // It works and it is not free: the reason says what moves under you.
        Assert.Contains("moves when the row is updated", result.Reason);
    }

    [Fact]
    public void An_engine_without_an_address_still_says_no()
    {
        // SQL Server's %%physloc%% is undocumented and moves; MySQL keeps its row id to itself.
        var result = RowIdentity.Resolve(Table([Column("name")]), new SqlServerDialect());

        Assert.False(result.Editable);
        Assert.Null(result.RowAddress);
    }

    [Fact]
    public void A_materialised_view_is_not_addressed_by_where_its_rows_are()
    {
        // It reads like a table and is refreshed rather than updated: PostgreSQL refuses an UPDATE
        // on one outright, so offering the grid as editable would be a promise the engine breaks.
        var result = RowIdentity.Resolve(
            Table([Column("path")], null, SchemaNodeKind.MaterializedView), new PostgreSqlDialect());

        Assert.False(result.Editable);
        Assert.Null(result.RowAddress);
    }

    [Fact]
    public void A_partitioned_table_is_not_addressed_by_where_its_rows_are()
    {
        // The root holds no rows of its own. Every ctid it hands out belongs to a partition, and
        // two partitions can hand out the same one — so writing by it would eventually write the
        // wrong row, in a different partition, without an error.
        var result = RowIdentity.Resolve(
            Table([Column("sensor")], null, partitioned: true), new PostgreSqlDialect());

        Assert.False(result.Editable);
        Assert.Null(result.RowAddress);
    }

    [Fact]
    public void A_partitioned_table_with_a_key_is_still_editable()
    {
        // Nothing about a key depends on where the row physically is, so the ordinary path stands.
        var result = RowIdentity.Resolve(
            Table([Column("id", pk: true)], null, partitioned: true), new PostgreSqlDialect());

        Assert.True(result.Editable);
        Assert.Equal(["id"], result.KeyColumns);
    }

    [Fact]
    public void A_key_still_wins_over_the_address()
    {
        var result = RowIdentity.Resolve(Table([Column("id", pk: true)]), new PostgreSqlDialect());

        Assert.Equal(["id"], result.KeyColumns);
        Assert.Null(result.RowAddress);
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
    public void An_update_by_physical_address_is_not_an_update_by_a_column()
    {
        var change = new RowChange("update",
            new Dictionary<string, object?> { [RowIdentity.AddressColumn] = "(0,7)" },
            new Dictionary<string, object?> { ["name"] = "ada" });

        var statement = Assert.Single(Build(new PostgreSqlDialect(), change).Statements);

        // Unquoted, and cast: ctid is not text, and PostgreSQL refuses `tid = text` rather than
        // guessing. The value still travels as a parameter.
        Assert.Equal("UPDATE \"public\".\"people\" SET \"name\" = @p0 WHERE ctid = CAST(@k0 AS tid)",
            statement.Sql);
        Assert.Equal("(0,7)", statement.Parameters["k0"]);
    }

    [Fact]
    public void A_delete_by_physical_address_says_it_the_same_way()
    {
        var change = new RowChange("delete",
            new Dictionary<string, object?> { [RowIdentity.AddressColumn] = "42" },
            new Dictionary<string, object?>());

        var statement = Assert.Single(Build(new SqliteDialect(), change).Statements);

        // SQLite applies integer affinity to the comparison, so no cast is needed or wanted.
        Assert.Equal("DELETE FROM \"public\".\"people\" WHERE rowid = $k0", statement.Sql);
        Assert.True(statement.Destructive);
    }

    [Fact]
    public void An_engine_with_no_address_treats_that_name_as_a_column()
    {
        // Nothing here can produce this change set, and if something did, quoting it as the column
        // it claims to be fails loudly instead of writing the wrong row.
        var change = new RowChange("update",
            new Dictionary<string, object?> { [RowIdentity.AddressColumn] = "x" },
            new Dictionary<string, object?> { ["name"] = "ada" });

        var statement = Assert.Single(Build(new SqlServerDialect(), change).Statements);
        Assert.Contains("[wds_row_address] = @k0", statement.Sql);
    }

    /// A table with the types a string is not: this is where "column is of type date but expression
    /// is of type text" came from.
    private static readonly ObjectDetail Typed = new(
        new SchemaNodeRef(SchemaNodeKind.Table, ["public", "members"]),
        [new ColumnInfo("id", "uuid", false, null, true, false, null, 1),
         new ColumnInfo("signed_up", "date", true, null, false, false, null, 2),
         new ColumnInfo("balance", "numeric", true, null, false, false, null, 3),
         new ColumnInfo("note", "text", true, null, false, false, null, 4),
         new ColumnInfo("mood", "mood", true, null, false, false, null, 5),
         new ColumnInfo("avatar", "bytea", true, null, false, false, null, 6)],
        [], [], [], null, null, null, null);

    private static ChangeScript BuildTyped(SqlDialect dialect, RowChange change) =>
        ChangeScriptBuilder.Build(new ChangeSet("c1", "Table:public/members", [change]), Typed, dialect);

    [Fact]
    public void A_string_going_into_a_typed_column_says_what_it_is()
    {
        // A parameter reaches the engine as a string, and PostgreSQL refuses `date = text` rather
        // than guessing — so the statement says it.
        var sql = BuildTyped(new PostgreSqlDialect(), new RowChange("insert",
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>
            {
                ["signed_up"] = "2026-01-01",
                ["balance"] = "10.50",
                ["note"] = "nothing to see",
                ["mood"] = "ok",
            })).Statements[0].Sql;

        Assert.Contains("CAST(@p0 AS timestamp)", sql);
        Assert.Contains("CAST(@p1 AS numeric)", sql);
        // Text into a text column needs nothing said about it.
        Assert.Contains("@p2,", sql);
        Assert.DoesNotContain("CAST(@p2", sql);
        // An enum is cast to its own declared type, which came out of the catalogue.
        Assert.Contains("CAST(@p3 AS mood)", sql);
    }

    [Fact]
    public void A_value_that_is_already_typed_is_left_alone()
    {
        var sql = BuildTyped(new PostgreSqlDialect(), new RowChange("insert",
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>
            {
                ["balance"] = 10.5,
                ["signed_up"] = null,
            })).Statements[0].Sql;

        Assert.DoesNotContain("CAST", sql);
    }

    [Fact]
    public void Binary_is_not_text_that_looks_odd_and_is_not_cast()
    {
        // Casting a string into a binary column would write nonsense; an error is more honest.
        var sql = BuildTyped(new PostgreSqlDialect(), new RowChange("insert",
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["avatar"] = "not really an image" })).Statements[0].Sql;

        Assert.DoesNotContain("CAST", sql);
    }

    [Fact]
    public void A_key_in_the_where_clause_is_typed_as_well()
    {
        // `WHERE id = $1` on a uuid column fails exactly the way an inserted date does.
        var update = BuildTyped(new PostgreSqlDialect(), new RowChange("update",
            new Dictionary<string, object?> { ["id"] = "8f14e45f-ceea-467a-9a36-dedd4bea2543" },
            new Dictionary<string, object?> { ["note"] = "changed" })).Statements[0].Sql;

        Assert.Contains("WHERE \"id\" = CAST(@k0 AS uuid)", update);

        var delete = BuildTyped(new PostgreSqlDialect(), new RowChange("delete",
            new Dictionary<string, object?> { ["id"] = "8f14e45f-ceea-467a-9a36-dedd4bea2543" },
            new Dictionary<string, object?>())).Statements[0].Sql;

        Assert.Contains("CAST(@k0 AS uuid)", delete);
    }

    [Fact]
    public void Each_engine_casts_the_way_it_spells_it()
    {
        var change = new RowChange("insert", new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["signed_up"] = "2026-01-01" });

        Assert.Contains("CAST(@p0 AS DATETIME)", BuildTyped(new MySqlDialect(), change).Statements[0].Sql);
        Assert.Contains("CAST(@p0 AS DATETIME2)",
            BuildTyped(new SqlServerDialect(), change).Statements[0].Sql);
        // SQLite has no types to speak of, and casting a date there would read it as a number.
        Assert.DoesNotContain("CAST", BuildTyped(new WebDataStudio.Server.Drivers.Sqlite.SqliteDialect(), change).Statements[0].Sql);
    }

    [Fact]
    public void The_preview_shows_the_cast_that_will_run()
    {
        var script = BuildTyped(new PostgreSqlDialect(), new RowChange("insert",
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["signed_up"] = "2026-01-01" }));

        // What is approved has to be what executes, casts included.
        Assert.Contains("CAST('2026-01-01' AS timestamp)", script.Text);
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
