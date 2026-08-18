namespace WebDataStudio.Server.Drivers.Abstractions;

/// What an engine can do. The UI hides everything set to false, and a driver must throw
/// NotSupportedException for anything it declares false — asserted by the contract suite.
public sealed record DriverCapabilities
{
    public bool Sql { get; init; } = true;
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
    public bool Backup { get; init; }
    public bool Restore { get; init; }
    public bool UserManagement { get; init; }
    public bool SessionList { get; init; }
    public bool KillSession { get; init; }
    public bool ServerStats { get; init; }
    public bool SlowQueryLog { get; init; }
    public bool SystemCommands { get; init; }
}
