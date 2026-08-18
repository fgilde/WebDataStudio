using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Ddl;

public class TableDiffTests
{
    private static ColumnDefinition Column(string name, string type = "text", bool nullable = true,
        string? renamedFrom = null) =>
        new(name, type, nullable, null, false, null, renamedFrom);

    private static TableDefinition Table(params ColumnDefinition[] columns) =>
        new("public", "people", columns, [], [], null);

    [Fact]
    public void An_unchanged_definition_produces_an_empty_diff()
    {
        var table = Table(Column("id"), Column("name"));
        Assert.True(TableDiff.Compute(table, table).IsEmpty);
    }

    [Fact]
    public void Adding_a_column_shows_up_as_added_only()
    {
        var change = TableDiff.Compute(Table(Column("id")), Table(Column("id"), Column("name")));

        Assert.Equal("name", Assert.Single(change.AddedColumns).Name);
        Assert.Empty(change.DroppedColumns);
    }

    [Fact]
    public void Removing_a_column_shows_up_as_dropped()
    {
        var change = TableDiff.Compute(Table(Column("id"), Column("name")), Table(Column("id")));
        Assert.Equal("name", Assert.Single(change.DroppedColumns).Name);
    }

    [Fact]
    public void Changing_a_type_is_an_alteration_carrying_both_sides()
    {
        var change = TableDiff.Compute(Table(Column("id", "int")), Table(Column("id", "bigint")));

        var altered = Assert.Single(change.AlteredColumns);
        Assert.Equal("int", altered.Before!.Type);
        Assert.Equal("bigint", altered.Column.Type);
    }

    [Fact]
    public void A_rename_is_a_rename_not_a_drop_plus_an_add()
    {
        var change = TableDiff.Compute(
            Table(Column("name")),
            Table(Column("full_name", renamedFrom: "name")));

        Assert.Single(change.RenamedColumns);
        Assert.Empty(change.DroppedColumns);
        Assert.Empty(change.AddedColumns);
    }

    [Fact]
    public void Nullability_and_default_changing_together_produce_one_alteration()
    {
        var before = Table(new ColumnDefinition("name", "text", true, null, false, null));
        var after = Table(new ColumnDefinition("name", "text", false, "'x'", false, null));

        Assert.Single(TableDiff.Compute(before, after).AlteredColumns);
    }

    [Fact]
    public void Indexes_compare_by_columns_not_by_name()
    {
        var before = new TableDefinition("public", "people", [Column("id")],
            [new IndexDefinition("ix_old", ["id"], false)], [], null);
        var after = before with { Indexes = [new IndexDefinition("ix_new", ["id"], false)] };

        var change = TableDiff.Compute(before, after);

        Assert.Empty(change.AddedIndexes);
        Assert.Empty(change.DroppedIndexes);
    }

    [Fact]
    public void A_genuinely_new_index_is_reported()
    {
        var before = new TableDefinition("public", "people", [Column("id"), Column("name")], [], [], null);
        var after = before with { Indexes = [new IndexDefinition("ix_name", ["name"], false)] };

        Assert.Single(TableDiff.Compute(before, after).AddedIndexes);
    }

    [Fact]
    public void A_changed_comment_is_reported()
    {
        var before = Table(Column("id"));
        var after = before with { Comment = "people of note" };

        Assert.True(TableDiff.Compute(before, after).CommentChanged);
    }
}

public class DdlWriterTests
{
    public static TheoryData<string, DdlWriterBase> Writers() => new()
    {
        { "postgresql", new PostgreSqlDdlWriter() },
        { "mysql", new MySqlDdlWriter() },
        { "sqlserver", new SqlServerDdlWriter() },
        { "sqlite", new SqliteDdlWriter() },
    };

    private static TableDefinition Sample() => new("public", "people",
        [
            new ColumnDefinition("id", "int", false, null, true, null),
            new ColumnDefinition("name", "text", false, null, false, null),
            new ColumnDefinition("note", "text", true, null, false, null),
        ],
        [new IndexDefinition("ix_people_name", ["name"], false)],
        [new ConstraintDefinition("pk_people", ConstraintKind.PrimaryKey, ["id"])],
        null);

    [Theory]
    [MemberData(nameof(Writers))]
    public void CreateTable_quotes_every_identifier(string engine, DdlWriterBase writer)
    {
        var sql = string.Join("\n", writer.CreateTable(Sample()).Select(s => s.Sql));

        var expected = engine switch
        {
            "mysql" => "`people`",
            "sqlserver" => "[people]",
            _ => "\"people\"",
        };
        Assert.Contains(expected, sql);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void CreateTable_marks_nullability_per_column(string engine, DdlWriterBase writer)
    {
        var sql = writer.CreateTable(Sample())[0].Sql;

        Assert.Contains("NOT NULL", sql);
        // The nullable column must not carry NOT NULL: count the occurrences instead of guessing.
        Assert.Equal(2, sql.Split("NOT NULL").Length - 1);
        _ = engine;
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void CreateTable_declares_the_primary_key_exactly_once(string engine, DdlWriterBase writer)
    {
        var sql = writer.CreateTable(Sample())[0].Sql;
        Assert.Equal(1, sql.Split("PRIMARY KEY").Length - 1);
        _ = engine;
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void Adding_a_column_produces_an_alter_add(string engine, DdlWriterBase writer)
    {
        var before = Sample();
        var after = before with { Columns = before.Columns.Append(
            new ColumnDefinition("age", "int", true, null, false, null)).ToList() };

        var sql = string.Join("\n", writer.AlterTable(before, TableDiff.Compute(before, after)).Select(s => s.Sql));

        Assert.Contains("ADD", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("age", sql);
        _ = engine;
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void Dropping_a_column_is_marked_destructive(string engine, DdlWriterBase writer)
    {
        var before = Sample();
        var after = before with { Columns = before.Columns.Where(c => c.Name != "note").ToList() };

        var statements = writer.AlterTable(before, TableDiff.Compute(before, after));
        Assert.Contains(statements, s => s.Destructive);
        _ = engine;
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void Renaming_a_column_uses_the_engine_syntax(string engine, DdlWriterBase writer)
    {
        var before = Sample();
        var after = before with
        {
            Columns = before.Columns
                .Select(c => c.Name == "note" ? c with { Name = "remark", RenamedFrom = "note" } : c)
                .ToList(),
        };

        var sql = string.Join("\n", writer.AlterTable(before, TableDiff.Compute(before, after)).Select(s => s.Sql));

        var expected = engine switch
        {
            "mysql" => "CHANGE",
            "sqlserver" => "sp_rename",
            _ => "RENAME COLUMN",
        };
        Assert.Contains(expected, sql);
    }

    [Fact]
    public void Sqlite_rebuilds_the_table_for_a_type_change()
    {
        var writer = new SqliteDdlWriter();
        var before = Sample();
        var after = before with
        {
            Columns = before.Columns.Select(c => c.Name == "note" ? c with { Type = "int" } : c).ToList(),
        };

        var statements = writer.AlterTable(before, TableDiff.Compute(before, after));
        var sql = string.Join("\n", statements.Select(s => s.Sql));

        Assert.Contains("CREATE TABLE", sql);
        Assert.Contains("INSERT INTO", sql);
        Assert.Contains("DROP TABLE", sql);
        Assert.Contains("RENAME TO", sql);
        Assert.DoesNotContain("ALTER COLUMN", sql);
    }

    [Fact]
    public void Postgres_alters_a_column_in_place()
    {
        var writer = new PostgreSqlDdlWriter();
        var before = Sample();
        var after = before with
        {
            Columns = before.Columns.Select(c => c.Name == "note" ? c with { Type = "int" } : c).ToList(),
        };

        var sql = string.Join("\n", writer.AlterTable(before, TableDiff.Compute(before, after)).Select(s => s.Sql));
        Assert.Contains("ALTER COLUMN \"note\" TYPE INTEGER", sql);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void Every_statement_carries_a_description(string engine, DdlWriterBase writer)
    {
        foreach (var statement in writer.CreateTable(Sample()))
            Assert.False(string.IsNullOrWhiteSpace(statement.Description));
        _ = engine;
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void Dropping_a_table_is_destructive(string engine, DdlWriterBase writer)
    {
        Assert.True(Assert.Single(writer.DropTable("public", "people")).Destructive);
        _ = engine;
    }
}
