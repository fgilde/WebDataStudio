using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

public sealed record PropertyEntry(string Group, string Name, string? Value);

/// What a connection is: where it points, which engine answers, and what that engine can do.
/// Read on demand from the server itself, so a version bump shows up without a redeploy.
public static class ConnectionProperties
{
    public static async Task<IReadOnlyList<PropertyEntry>> ReadAsync(
        IDbDriver driver, IDbSession session, CancellationToken ct)
    {
        var entries = new List<PropertyEntry>();

        void Add(string group, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) entries.Add(new PropertyEntry(group, name, value.Trim()));
        }

        // The engine is already named from the definition; this reads what the server says.
        try
        {
            Add("Server", "Database", session.Connection.Database);
        }
        catch (Exception)
        {
            // Not every provider exposes the current database before a command runs.
        }

        foreach (var (name, sql) in Queries(driver.Info.Id))
            Add("Server", name, await ScalarAsync(session, sql, ct));

        return entries;
    }

    /// One scalar per row of information. Anything that fails is left out rather than reported:
    /// a properties dialog that errors because a role may not read a setting is worse than one
    /// with fewer rows.
    private static IEnumerable<(string Name, string Sql)> Queries(string engine) => engine switch
    {
        "postgresql" =>
        [
            ("Version", "SELECT version()"),
            ("User", "SELECT current_user"),
            ("Encoding", "SHOW server_encoding"),
            ("Time zone", "SHOW TimeZone"),
            ("Size", "SELECT pg_size_pretty(pg_database_size(current_database()))"),
        ],
        "mysql" =>
        [
            ("Version", "SELECT version()"),
            ("User", "SELECT current_user()"),
            ("Encoding", "SELECT @@character_set_database"),
            ("Time zone", "SELECT @@system_time_zone"),
            ("Size", """
                SELECT concat(round(sum(data_length + index_length) / 1024 / 1024, 1), ' MB')
                  FROM information_schema.tables WHERE table_schema = database()
                """),
        ],
        "sqlserver" =>
        [
            ("Version", "SELECT @@VERSION"),
            ("User", "SELECT SUSER_SNAME()"),
            ("Collation", "SELECT CONVERT(varchar, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))"),
            ("Recovery model", "SELECT CONVERT(varchar, DATABASEPROPERTYEX(DB_NAME(), 'Recovery'))"),
            ("Size", """
                SELECT CONVERT(varchar, CAST(SUM(size) * 8.0 / 1024 AS decimal(10,1))) + ' MB'
                  FROM sys.database_files
                """),
        ],
        "sqlite" =>
        [
            ("Version", "SELECT sqlite_version()"),
            ("File", "SELECT file FROM pragma_database_list WHERE name = 'main'"),
            ("Encoding", "PRAGMA encoding"),
            ("Page size", "PRAGMA page_size"),
            ("Size", """
                SELECT CAST(ROUND(
                    (SELECT * FROM pragma_page_count) * (SELECT * FROM pragma_page_size) / 1024.0, 1)
                    AS TEXT) || ' KB'
                """),
            ("Journal mode", "PRAGMA journal_mode"),
            ("Foreign keys", "SELECT CASE WHEN (SELECT * FROM pragma_foreign_keys) = 1 THEN 'on' ELSE 'off' END"),
        ],
        "oracle" =>
        [
            ("Version", "SELECT banner FROM v$version WHERE rownum = 1"),
            ("User", "SELECT user FROM dual"),
        ],
        "duckdb" =>
        [
            ("Version", "SELECT version()"),
        ],
        "clickhouse" =>
        [
            ("Version", "SELECT version()"),
            ("User", "SELECT currentUser()"),
            ("Time zone", "SELECT timezone()"),
        ],
        _ => [],
    };

    private static async Task<string?> ScalarAsync(IDbSession session, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 10;

            return (await command.ExecuteScalarAsync(ct))?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
