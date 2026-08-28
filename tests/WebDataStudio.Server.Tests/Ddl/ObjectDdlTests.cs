using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Ddl;

/// The objects a table designer never covered. Every engine writes its own spelling, and what one
/// cannot do it says in a sentence rather than emitting DDL the server will reject.
public class ObjectDdlTests
{
    private static DdlWriterBase Writer(string engine) =>
        WebDataStudio.Server.Endpoints.DdlEndpoints.WriterFor(engine)!;

    private static string Sql(IReadOnlyList<DdlStatement> statements) =>
        string.Join("\n", statements.Select(s => s.Sql));

    private static SchemaNodeRef Ref(SchemaNodeKind kind, params string[] path) => new(kind, path);

    // --- views ------------------------------------------------------------------------------------

    [Fact]
    public void A_view_is_replaced_the_way_each_engine_spells_it()
    {
        Assert.StartsWith("CREATE OR REPLACE VIEW",
            Sql(Writer("postgresql").CreateOrReplaceView("public", "active", "SELECT 1")));

        Assert.StartsWith("CREATE OR REPLACE VIEW",
            Sql(Writer("mysql").CreateOrReplaceView("shop", "active", "SELECT 1")));

        Assert.StartsWith("CREATE OR ALTER VIEW",
            Sql(Writer("sqlserver").CreateOrReplaceView("dbo", "active", "SELECT 1")));
    }

    /// SQLite has no "or replace", so it drops and writes again — and both statements are in the
    /// preview, because that is what the person is agreeing to.
    [Fact]
    public void Sqlite_replaces_a_view_by_dropping_it_first()
    {
        var statements = Writer("sqlite").CreateOrReplaceView("", "active", "SELECT 1");

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("DROP VIEW IF EXISTS", statements[0].Sql);
        Assert.True(statements[0].Destructive);
        Assert.StartsWith("CREATE VIEW", statements[1].Sql);
    }

    [Fact]
    public void A_view_body_keeps_one_semicolon_at_the_end()
    {
        var sql = Sql(Writer("postgresql").CreateOrReplaceView("public", "v", "SELECT 1;"));

        Assert.EndsWith("SELECT 1;", sql);
        Assert.DoesNotContain(";;", sql);
    }

    // --- routines ---------------------------------------------------------------------------------

    /// SQL Server refuses a second CREATE for an object that exists. Saving an edited procedure has
    /// to reach it as CREATE OR ALTER, whatever the source in the editor happens to say.
    [Fact]
    public void Sqlserver_turns_a_create_into_create_or_alter()
    {
        var sql = Sql(Writer("sqlserver").CreateOrReplaceRoutine(
            new RoutineDefinition("dbo", "ship", "procedure", "CREATE PROCEDURE dbo.ship AS SELECT 1")));

        Assert.StartsWith("CREATE OR ALTER PROCEDURE", sql);
    }

    [Fact]
    public void Sqlserver_leaves_a_create_or_alter_alone()
    {
        var sql = Sql(Writer("sqlserver").CreateOrReplaceRoutine(
            new RoutineDefinition("dbo", "ship", "procedure", "CREATE OR ALTER PROCEDURE dbo.ship AS SELECT 1")));

        Assert.Equal(1, sql.Split("CREATE OR ALTER").Length - 1);
    }

    /// MySQL cannot replace a routine in place, so it drops it first — visibly.
    [Fact]
    public void Mysql_replaces_a_routine_by_dropping_it()
    {
        var statements = Writer("mysql").CreateOrReplaceRoutine(
            new RoutineDefinition("shop", "ship", "procedure", "CREATE PROCEDURE ship() BEGIN SELECT 1; END"));

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("DROP PROCEDURE IF EXISTS", statements[0].Sql);
        Assert.True(statements[0].Destructive);
    }

    [Fact]
    public void Postgres_sends_a_routine_as_written()
    {
        var sql = Sql(Writer("postgresql").CreateOrReplaceRoutine(new RoutineDefinition(
            "public", "ship", "function", "CREATE OR REPLACE FUNCTION ship() RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql")));

        Assert.StartsWith("CREATE OR REPLACE FUNCTION", sql);
        Assert.EndsWith("LANGUAGE sql;", sql);
    }

    // --- sequences --------------------------------------------------------------------------------

    [Fact]
    public void A_sequence_is_created_with_only_the_clauses_that_were_asked_for()
    {
        var sql = Sql(Writer("postgresql").CreateSequence(
            new SequenceDefinition("public", "order_id", Start: 1000, Increment: 1)));

        Assert.Contains("CREATE SEQUENCE \"public\".\"order_id\"", sql);
        Assert.Contains("START WITH 1000", sql);
        Assert.Contains("INCREMENT BY 1", sql);
        Assert.Contains("NO CYCLE", sql);
    }

    /// The answer to "the import wrote its own ids and the sequence now hands out ones that exist".
    [Fact]
    public void A_restart_is_marked_destructive_because_it_can_hand_out_ids_that_exist()
    {
        var statements = Writer("postgresql").AlterSequence(
            new SequenceDefinition("public", "order_id", RestartWith: 5000));

        Assert.Contains("RESTART WITH 5000", statements[0].Sql);
        Assert.True(statements[0].Destructive);
    }

    [Fact]
    public void Sqlserver_gives_a_sequence_a_type()
    {
        var sql = Sql(Writer("sqlserver").CreateSequence(new SequenceDefinition("dbo", "s", Start: 1)));

        Assert.Contains("AS BIGINT", sql);
    }

    [Theory]
    [InlineData("mysql", "AUTO_INCREMENT")]
    [InlineData("sqlite", "INTEGER PRIMARY KEY")]
    public void An_engine_without_sequences_says_what_to_use_instead(string engine, string hint)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            Writer(engine).CreateSequence(new SequenceDefinition("", "s")));

        Assert.Contains(hint, error.Message);
    }

    // --- schemas ----------------------------------------------------------------------------------

    [Fact]
    public void A_schema_is_created_and_dropped()
    {
        Assert.Equal("CREATE SCHEMA \"reporting\";", Sql(Writer("postgresql").CreateSchema("reporting")));

        var drop = Writer("postgresql").DropSchema("reporting", cascade: true);
        Assert.Contains("CASCADE", drop[0].Sql);
        Assert.True(drop[0].Destructive);
    }

    [Fact]
    public void Mysql_points_at_the_database_dialog_instead()
    {
        var error = Assert.Throws<NotSupportedException>(() => Writer("mysql").CreateSchema("reporting"));

        Assert.Contains("a schema is a database", error.Message);
    }

    /// SQL Server drops a schema only once it is empty; a CASCADE it cannot do is said rather than
    /// sent and rejected.
    [Fact]
    public void Sqlserver_refuses_a_cascading_schema_drop()
    {
        Assert.Throws<NotSupportedException>(() => Writer("sqlserver").DropSchema("reporting", cascade: true));
        Assert.Contains("DROP SCHEMA", Sql(Writer("sqlserver").DropSchema("reporting", cascade: false)));
    }

    // --- comments ---------------------------------------------------------------------------------

    [Fact]
    public void A_comment_lands_on_the_object_it_is_about()
    {
        Assert.Equal("COMMENT ON TABLE \"public\".\"orders\" IS 'what people bought';",
            Sql(Writer("postgresql").Comment(Ref(SchemaNodeKind.Table, "public", "orders"),
                "what people bought")));

        Assert.Equal("COMMENT ON COLUMN \"public\".\"orders\".\"total\" IS 'in cents';",
            Sql(Writer("postgresql").Comment(Ref(SchemaNodeKind.Column, "public", "orders", "total"),
                "in cents")));

        Assert.Equal("COMMENT ON VIEW \"public\".\"active\" IS NULL;",
            Sql(Writer("postgresql").Comment(Ref(SchemaNodeKind.View, "public", "active"), null)));
    }

    [Fact]
    public void Mysql_comments_a_table_with_alter_table()
    {
        var sql = Sql(Writer("mysql").Comment(Ref(SchemaNodeKind.Table, "shop", "orders"), "what people bought"));

        Assert.Contains("ALTER TABLE", sql);
        Assert.Contains("COMMENT = 'what people bought'", sql);
    }

    [Theory]
    [InlineData("sqlserver", "extended properties")]
    [InlineData("sqlite", "notes")]
    public void An_engine_without_comments_points_at_the_studios_notes(string engine, string hint)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            Writer(engine).Comment(Ref(SchemaNodeKind.Table, "dbo", "orders"), "x"));

        Assert.Contains(hint, error.Message);
    }

    // --- triggers ---------------------------------------------------------------------------------

    [Fact]
    public void A_trigger_is_switched_off_on_its_table()
    {
        var trigger = Ref(SchemaNodeKind.Trigger, "public", "orders", "audit");

        Assert.Equal("ALTER TABLE \"public\".\"orders\" DISABLE TRIGGER \"audit\";",
            Sql(Writer("postgresql").SetTriggerEnabled(trigger, enabled: false)));

        // SQL Server quotes with brackets, which is the dialect's job rather than this writer's.
        Assert.Equal("DISABLE TRIGGER [audit] ON [dbo].[orders];",
            Sql(Writer("sqlserver").SetTriggerEnabled(
                Ref(SchemaNodeKind.Trigger, "dbo", "orders", "audit"), enabled: false)));
    }

    [Fact]
    public void Switching_a_trigger_back_on_is_not_destructive()
    {
        var statements = Writer("postgresql").SetTriggerEnabled(
            Ref(SchemaNodeKind.Trigger, "public", "orders", "audit"), enabled: true);

        Assert.False(statements[0].Destructive);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    public void An_engine_that_cannot_stop_a_trigger_says_so(string engine)
    {
        Assert.Throws<NotSupportedException>(() => Writer(engine).SetTriggerEnabled(
            Ref(SchemaNodeKind.Trigger, "shop", "orders", "audit"), enabled: false));
    }

    // --- dropping ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(SchemaNodeKind.View, "DROP VIEW")]
    [InlineData(SchemaNodeKind.MaterializedView, "DROP MATERIALIZED VIEW")]
    [InlineData(SchemaNodeKind.Procedure, "DROP PROCEDURE")]
    [InlineData(SchemaNodeKind.Function, "DROP FUNCTION")]
    [InlineData(SchemaNodeKind.Sequence, "DROP SEQUENCE")]
    public void Anything_in_the_tree_drops_by_what_it_is(SchemaNodeKind kind, string expected)
    {
        var statements = Writer("postgresql").DropObject(Ref(kind, "public", "thing"));

        Assert.StartsWith(expected, statements[0].Sql);
        Assert.True(statements[0].Destructive);
    }

    [Fact]
    public void A_trigger_drops_with_its_table_where_the_engine_wants_it()
    {
        var trigger = Ref(SchemaNodeKind.Trigger, "public", "orders", "audit");

        Assert.Contains("ON \"public\".\"orders\"", Sql(Writer("postgresql").DropObject(trigger)));
        Assert.DoesNotContain(" ON ", Sql(Writer("sqlserver").DropObject(
            Ref(SchemaNodeKind.Trigger, "dbo", "orders", "audit"))));
        Assert.DoesNotContain(" ON ", Sql(Writer("sqlite").DropObject(
            Ref(SchemaNodeKind.Trigger, "orders", "audit"))));
    }

    [Fact]
    public void A_column_is_not_something_this_drops()
    {
        // A column goes through the designer, which knows the rest of the table.
        Assert.Throws<NotSupportedException>(() =>
            Writer("postgresql").DropObject(Ref(SchemaNodeKind.Column, "public", "orders", "total")));
    }

    // --- renaming ---------------------------------------------------------------------------------

    [Fact]
    public void Renaming_says_what_it_is_renaming()
    {
        Assert.StartsWith("ALTER VIEW",
            Sql(Writer("postgresql").Rename(Ref(SchemaNodeKind.View, "public", "active"), "current")));

        Assert.StartsWith("ALTER SEQUENCE",
            Sql(Writer("postgresql").Rename(Ref(SchemaNodeKind.Sequence, "public", "s"), "t")));

        Assert.StartsWith("ALTER TABLE",
            Sql(Writer("postgresql").Rename(Ref(SchemaNodeKind.Table, "public", "orders"), "sales")));
    }

    [Fact]
    public void A_trigger_is_renamed_on_its_table()
    {
        var sql = Sql(Writer("postgresql").Rename(
            Ref(SchemaNodeKind.Trigger, "public", "orders", "audit"), "audit_v2"));

        Assert.Contains("ALTER TRIGGER \"audit\" ON \"public\".\"orders\"", sql);
    }

    /// A routine is identified by its argument types, which the tree does not carry.
    [Fact]
    public void Renaming_a_routine_says_why_it_cannot()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            Writer("postgresql").Rename(Ref(SchemaNodeKind.Function, "public", "ship"), "deliver"));

        Assert.Contains("signature", error.Message);
    }

    [Fact]
    public void Mysql_renames_a_view_with_rename_table()
    {
        Assert.StartsWith("RENAME TABLE",
            Sql(Writer("mysql").Rename(Ref(SchemaNodeKind.View, "shop", "active"), "current")));
    }

    [Fact]
    public void Sqlite_says_a_view_cannot_be_renamed()
    {
        Assert.Throws<NotSupportedException>(() =>
            Writer("sqlite").Rename(Ref(SchemaNodeKind.View, "active"), "current"));
    }
}
