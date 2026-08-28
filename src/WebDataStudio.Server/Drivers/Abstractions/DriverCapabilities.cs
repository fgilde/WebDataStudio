namespace WebDataStudio.Server.Drivers.Abstractions;

/// What an engine can do. The UI hides everything set to false, and a driver must throw
/// NotSupportedException for anything it declares false — asserted by the contract suite.
public sealed record DriverCapabilities
{
    public bool Sql { get; init; } = true;

    /// Whether an object can be browsed as a page of rows. False for a key/value store, where an
    /// object is one value with a shape of its own and `SELECT * FROM key` means nothing.
    public bool TabularBrowse { get; init; } = true;

    /// Whether a container - a database, a folder of keys - is itself a page of rows. True for a key
    /// space, where the inventory of keys with their types and their expiry is the interesting table
    /// and no single object holds it.
    public bool BrowseContainers { get; init; }
    public bool MultiSchema { get; init; }
    public bool MultiDatabase { get; init; }
    public bool EstimatedPlan { get; init; }
    public bool ActualPlan { get; init; }
    public bool Transactions { get; init; }
    public bool Ddl { get; init; }
    public bool StoredProcedures { get; init; }
    public bool Triggers { get; init; }
    public bool Views { get; init; }
    public bool MaterializedViews { get; init; }
    public bool Sequences { get; init; }
    public bool ForeignKeys { get; init; }
    public bool PartialIndexes { get; init; }
    public bool IncludeColumns { get; init; }
    public bool FullTextIndexes { get; init; }
    public bool Backup { get; init; }
    public bool Restore { get; init; }
    public bool UserManagement { get; init; }
    public bool SessionList { get; init; }
    public bool KillSession { get; init; }
    public bool ServerStats { get; init; }
    public bool SlowQueryLog { get; init; }
    public bool SystemCommands { get; init; }

    /// The server has a scheduler of its own: SQL Server Agent, pg_cron, MySQL events. What runs
    /// there is listed and its history read; changing it is a statement the person reads first.
    public bool Jobs { get; init; }

    /// The server can say what it is working on right now — a vacuum's progress, a running
    /// statement's age — and which session is waiting for which.
    public bool ActivityProgress { get; init; }

    /// The server can report its replicas and how far behind they are.
    public bool Replication { get; init; }
}
