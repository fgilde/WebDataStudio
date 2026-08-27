namespace WebDataStudio.Server.Drivers.Abstractions;

public enum SchemaNodeKind
{
    Database, Schema, TableFolder, ViewFolder, ProcedureFolder, FunctionFolder,
    TriggerFolder, SequenceFolder, Table, View, MaterializedView, Procedure,
    Function, Trigger, Sequence, Column, Index, ForeignKey,

    // The rest of the catalogue. Schema-scoped: types and domains. Server-scoped: everything a
    // database has one list of — extensions, roles, tablespaces, and logical replication.
    TypeFolder, Type, Domain,
    ExtensionFolder, Extension,
    RoleFolder, Role,
    TablespaceFolder, Tablespace,
    PublicationFolder, Publication,
    SubscriptionFolder, Subscription,

    // Object storage: a container, a prefix inside it, an object, and the row that fetches the next
    // page of a listing — because a container is paged rather than walked.
    Container, Prefix, StorageObject, StorageMore,
}

/// Identifies an object across the whole tree. `Path` is the ordered chain of names from the
/// database down to the object, which every driver can turn back into a qualified name.
public sealed record SchemaNodeRef(SchemaNodeKind Kind, IReadOnlyList<string> Path)
{
    public string Name => Path.Count == 0 ? "" : Path[^1];
    public override string ToString() => $"{Kind}:{string.Join('/', Path)}";

    public static SchemaNodeRef Parse(string value)
    {
        var split = value.Split(':', 2);
        if (split.Length != 2 || !Enum.TryParse<SchemaNodeKind>(split[0], out var kind))
            throw new FormatException($"'{value}' is not a schema node reference");
        return new SchemaNodeRef(kind, split[1].Split('/'));
    }
}

public sealed record SchemaNode(
    SchemaNodeRef Ref,
    string Label,
    bool HasChildren,
    string? Detail = null);

public sealed record ColumnInfo(
    string Name, string DataType, bool Nullable, string? Default,
    bool IsPrimaryKey, bool IsIdentity, string? Comment, int Position);

public sealed record IndexInfo(
    string Name, IReadOnlyList<string> Columns, bool Unique, bool Primary, string? Filter,
    /// A full-text index. It is spelled differently per engine, so it cannot be diffed against a
    /// plain index and is created and dropped as a whole.
    bool FullText = false);

public sealed record ForeignKeyInfo(
    string Name, IReadOnlyList<string> Columns,
    string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns,
    string OnDelete, string OnUpdate);

public sealed record TriggerInfo(string Name, string Timing, string Event);

public sealed record ObjectDetail(
    SchemaNodeRef Ref,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<TriggerInfo> Triggers,
    long? RowCount,
    long? SizeBytes,
    string? Comment,
    string? Ddl);
