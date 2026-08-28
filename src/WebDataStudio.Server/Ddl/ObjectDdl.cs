using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Ddl;

/// A sequence, as the studio asks for one. Everything optional is left out of the statement rather
/// than guessed at, so the engine's own defaults apply.
public sealed record SequenceDefinition(
    string Schema, string Name,
    long? Start = null, long? Increment = null,
    long? MinValue = null, long? MaxValue = null,
    bool Cycle = false, long? Cache = null,
    /// Restart the sequence at this value. The answer to "the import wrote its own ids and now the
    /// sequence hands out ones that already exist".
    long? RestartWith = null);

/// The objects a table designer never covered: views, sequences, routines, triggers and schemas.
///
/// Everything here writes a statement and nothing runs one — the preview and the apply endpoint do
/// that, the same way they already did for tables. What an engine cannot do throws
/// NotSupportedException with a sentence that says what to do instead, which is what the UI shows.
public abstract partial class DdlWriterBase
{
    /// True where a schema is a thing of its own rather than another word for a database.
    protected virtual bool HasSchemas => true;

    /// What `DROP <kind>` is called for a node kind, or null where the engine has no such object.
    protected virtual string? DropKeyword(SchemaNodeKind kind) => kind switch
    {
        SchemaNodeKind.Table => "TABLE",
        SchemaNodeKind.View => "VIEW",
        SchemaNodeKind.MaterializedView => "MATERIALIZED VIEW",
        SchemaNodeKind.Procedure => "PROCEDURE",
        SchemaNodeKind.Function => "FUNCTION",
        SchemaNodeKind.Sequence => "SEQUENCE",
        SchemaNodeKind.Trigger => "TRIGGER",
        SchemaNodeKind.Index => "INDEX",
        _ => null,
    };

    /// The keyword `COMMENT ON` wants for a node kind.
    protected virtual string CommentKeyword(SchemaNodeKind kind) => kind switch
    {
        SchemaNodeKind.MaterializedView => "MATERIALIZED VIEW",
        SchemaNodeKind.Column => "COLUMN",
        _ => DropKeyword(kind) ?? "TABLE",
    };

    // --- views ------------------------------------------------------------------------------------

    public virtual IReadOnlyList<DdlStatement> CreateOrReplaceView(string schema, string name, string select) =>
    [
        new DdlStatement(
            $"CREATE OR REPLACE VIEW {Qualify(schema, name)} AS\n{Body(select)};",
            // Replacing a view is not destructive in itself, but it is a definition somebody else
            // may be reading through. The preview shows it either way.
            false, $"create or replace view {name}"),
    ];

    // --- routines and triggers --------------------------------------------------------------------

    /// A trigger stopped rather than dropped: the definition stays, the firing does not. The two
    /// engines that cannot do this say so instead.
    public virtual IReadOnlyList<DdlStatement> SetTriggerEnabled(SchemaNodeRef trigger, bool enabled)
    {
        var (schema, table) = TableOf(trigger);

        return
        [
            new DdlStatement(
                $"ALTER TABLE {Qualify(schema, table)} {(enabled ? "ENABLE" : "DISABLE")} TRIGGER " +
                $"{Dialect.QuoteIdentifier(trigger.Name)};",
                !enabled, $"{(enabled ? "enable" : "disable")} trigger {trigger.Name}"),
        ];
    }

    // --- sequences --------------------------------------------------------------------------------

    public virtual IReadOnlyList<DdlStatement> CreateSequence(SequenceDefinition sequence)
    {
        var clauses = Clauses(sequence, restart: false);

        return
        [
            new DdlStatement(
                $"CREATE SEQUENCE {Qualify(sequence.Schema, sequence.Name)}{clauses};",
                false, $"create sequence {sequence.Name}"),
        ];
    }

    public virtual IReadOnlyList<DdlStatement> AlterSequence(SequenceDefinition sequence)
    {
        var clauses = Clauses(sequence, restart: true);

        if (clauses.Length == 0)
            throw new NotSupportedException("nothing to change on this sequence");

        return
        [
            new DdlStatement(
                $"ALTER SEQUENCE {Qualify(sequence.Schema, sequence.Name)}{clauses};",
                // A restart can hand out ids that already exist somewhere, which is why it is
                // asked for on purpose and shown before it runs.
                sequence.RestartWith is not null, $"alter sequence {sequence.Name}"),
        ];
    }

    /// The clauses both statements share, in the order every engine spells them.
    protected string Clauses(SequenceDefinition sequence, bool restart)
    {
        var parts = new List<string>();

        if (restart && sequence.RestartWith is { } at) parts.Add($"RESTART WITH {at}");
        else if (!restart && sequence.Start is { } start) parts.Add($"START WITH {start}");

        if (sequence.Increment is { } increment) parts.Add($"INCREMENT BY {increment}");
        parts.Add(sequence.MinValue is { } min ? $"MINVALUE {min}" : "NO MINVALUE");
        parts.Add(sequence.MaxValue is { } max ? $"MAXVALUE {max}" : "NO MAXVALUE");
        if (sequence.Cache is { } cache) parts.Add($"CACHE {cache}");
        parts.Add(sequence.Cycle ? "CYCLE" : "NO CYCLE");

        return parts.Count == 0 ? "" : "\n  " + string.Join("\n  ", parts);
    }

    // --- schemas ----------------------------------------------------------------------------------

    public virtual IReadOnlyList<DdlStatement> CreateSchema(string name) =>
    [
        new DdlStatement($"CREATE SCHEMA {Dialect.QuoteIdentifier(name)};", false, $"create schema {name}"),
    ];

    public virtual IReadOnlyList<DdlStatement> DropSchema(string name, bool cascade) =>
    [
        new DdlStatement(
            $"DROP SCHEMA {(SupportsIfExists ? "IF EXISTS " : "")}{Dialect.QuoteIdentifier(name)}" +
            $"{(cascade ? " CASCADE" : "")};",
            true, cascade ? $"drop schema {name} and everything in it" : $"drop schema {name}"),
    ];

    // --- comments ---------------------------------------------------------------------------------

    /// The description the database itself keeps, which is what another tool reading this database
    /// will see. The studio's own notes are the other half: those need no rights and no migration.
    public virtual IReadOnlyList<DdlStatement> Comment(SchemaNodeRef target, string? text)
    {
        var name = target.Kind == SchemaNodeKind.Column
            ? $"{Qualify(target.Path.Count > 2 ? target.Path[0] : "", target.Path[^2])}." +
              Dialect.QuoteIdentifier(target.Name)
            : Qualify(target.Path.Count > 1 ? target.Path[0] : "", target.Name);

        var value = string.IsNullOrWhiteSpace(text) ? "NULL" : Dialect.QuoteLiteral(text);

        return
        [
            new DdlStatement($"COMMENT ON {CommentKeyword(target.Kind)} {name} IS {value};",
                false, $"comment on {target.Name}"),
        ];
    }

    // --- dropping anything the tree shows ---------------------------------------------------------

    /// One object, dropped by what it is. The table designer already had this for tables; the point
    /// here is that a view, a routine, a sequence or a trigger goes the same way — previewed, with
    /// whatever depends on it listed first.
    public virtual IReadOnlyList<DdlStatement> DropObject(SchemaNodeRef target)
    {
        if (DropKeyword(target.Kind) is not { } keyword)
            throw new NotSupportedException(
                $"the studio has no DROP for a {target.Kind.ToString().ToLowerInvariant()}");

        // A trigger belongs to its table on the engines that spell it that way.
        if (target.Kind == SchemaNodeKind.Trigger)
        {
            var (schema, table) = TableOf(target);
            return
            [
                new DdlStatement(
                    $"DROP TRIGGER {(SupportsIfExists ? "IF EXISTS " : "")}" +
                    $"{Dialect.QuoteIdentifier(target.Name)} ON {Qualify(schema, table)};",
                    true, $"drop trigger {target.Name}"),
            ];
        }

        var qualified = Qualify(target.Path.Count > 1 ? target.Path[0] : "", target.Name);

        return
        [
            new DdlStatement($"DROP {keyword} {(SupportsIfExists ? "IF EXISTS " : "")}{qualified};",
                true, $"drop {keyword.ToLowerInvariant()} {target.Name}"),
        ];
    }

    /// The table a trigger hangs on: its reference carries the parent's path, so it is the name
    /// before the trigger's own.
    protected static (string Schema, string Table) TableOf(SchemaNodeRef trigger) =>
        trigger.Path.Count >= 3 ? (trigger.Path[0], trigger.Path[^2])
        : trigger.Path.Count == 2 ? ("", trigger.Path[0])
        : throw new NotSupportedException($"'{trigger}' does not say which table this trigger is on");

    /// A body as typed, without the trailing semicolon that would end up doubled.
    protected static string Body(string text) => text.Trim().TrimEnd(';');
}
