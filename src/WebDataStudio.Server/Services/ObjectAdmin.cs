using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One row-level security policy, as the catalogue has it.
public sealed record RowPolicy(
    string Name, string Command, string Roles, bool Permissive, string? Using, string? Check);

public sealed record RowSecurity(bool Supported, bool Enabled, bool Forced, IReadOnlyList<RowPolicy> Policies);

/// One partition of a partitioned table, with the bound that defines it.
public sealed record Partition(string Name, string Bound, long? SizeBytes, long? Rows);

public sealed record Partitioning(
    bool Supported, bool Partitioned, string? Strategy, string? Key, IReadOnlyList<Partition> Partitions);

/// The parts of an object that are neither its shape nor its data: whether row-level security is on
/// and what it says, and how a partitioned table is cut up.
///
/// PostgreSQL-only, because these are PostgreSQL features. Everything else answers "not supported"
/// rather than an empty list that reads like "none".
public static class ObjectAdmin
{
    // --- row-level security ------------------------------------------------------------------

    public static async Task<RowSecurity> ReadSecurityAsync(
        IDbDriver driver, IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        if (driver.Info.Id != "postgresql") return new RowSecurity(false, false, false, []);

        var schema = target.Path.Count > 1 ? target.Path[0] : "public";
        var flags = await OneAsync(driver, session, """
            SELECT c.relrowsecurity, c.relforcerowsecurity
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relname = @name AND n.nspname = @schema
            """, schema, target.Name, ct);

        var policies = new List<RowPolicy>();

        await foreach (var row in RowsAsync(driver, session, """
            SELECT p.policyname,
                   p.cmd,
                   coalesce(array_to_string(p.roles, ', '), 'PUBLIC'),
                   p.permissive = 'PERMISSIVE',
                   p.qual,
                   p.with_check
              FROM pg_policies p
             WHERE p.tablename = @name AND p.schemaname = @schema
             ORDER BY p.policyname
            """, schema, target.Name, ct))
            policies.Add(new RowPolicy(
                Text(row, 0) ?? "", Text(row, 1) ?? "ALL", Text(row, 2) ?? "PUBLIC",
                Flag(row, 3), Text(row, 4), Text(row, 5)));

        return new RowSecurity(true, Flag(flags, 0), Flag(flags, 1), policies);
    }

    /// Turning row-level security on with no policy means "nobody sees anything", which is a fine
    /// default and a terrible surprise — so the statement says both halves in one script.
    public static string SecurityStatement(
        IDbDriver driver, SchemaNodeRef target, bool enable, bool force)
    {
        var table = Qualify(driver, target);

        var lines = new List<string>
        {
            enable
                ? $"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;"
                : $"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;",
        };

        if (enable)
            lines.Add(force
                ? $"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;"
                : $"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");

        return string.Join("\n", lines);
    }

    public static string PolicyStatement(
        IDbDriver driver, SchemaNodeRef target, string name, string? command, string? roles,
        string? usingExpression, string? checkExpression, bool drop)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a policy needs a name");

        var table = Qualify(driver, target);
        var policy = driver.Dialect.QuoteIdentifier(name.Trim());

        if (drop) return $"DROP POLICY {policy} ON {table};";

        var verb = (command ?? "ALL").Trim().ToUpperInvariant();
        if (verb is not ("ALL" or "SELECT" or "INSERT" or "UPDATE" or "DELETE"))
            throw new ArgumentException($"'{command}' is not a policy command");

        var statement = $"CREATE POLICY {policy} ON {table}\n  FOR {verb}";

        if (roles is { Length: > 0 })
            statement += "\n  TO " + string.Join(", ", roles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => role.Equals("public", StringComparison.OrdinalIgnoreCase)
                    ? "PUBLIC"
                    : driver.Dialect.QuoteIdentifier(role)));

        // The expressions are SQL by definition — a policy is an expression — so they travel as
        // typed, and the preview is where somebody reads them before they run.
        if (usingExpression is { Length: > 0 }) statement += $"\n  USING ({usingExpression})";
        if (checkExpression is { Length: > 0 }) statement += $"\n  WITH CHECK ({checkExpression})";

        return statement + ";";
    }

    // --- partitions --------------------------------------------------------------------------

    public static async Task<Partitioning> ReadPartitionsAsync(
        IDbDriver driver, IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        if (driver.Info.Id != "postgresql") return new Partitioning(false, false, null, null, []);

        var schema = target.Path.Count > 1 ? target.Path[0] : "public";

        var head = await OneAsync(driver, session, """
            SELECT CASE p.partstrat WHEN 'r' THEN 'RANGE' WHEN 'l' THEN 'LIST'
                                    WHEN 'h' THEN 'HASH' ELSE p.partstrat::text END,
                   pg_get_partkeydef(c.oid)
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_partitioned_table p ON p.partrelid = c.oid
             WHERE c.relname = @name AND n.nspname = @schema
            """, schema, target.Name, ct);

        var strategy = Text(head, 0);
        if (strategy is null) return new Partitioning(true, false, null, null, []);

        var partitions = new List<Partition>();

        await foreach (var row in RowsAsync(driver, session, """
            SELECT child.relname,
                   pg_get_expr(child.relpartbound, child.oid),
                   pg_total_relation_size(child.oid),
                   -- reltuples is -1 until the partition has been analysed at least once. That is
                   -- "unknown", not "minus one row", so it travels as null.
                   nullif(child.reltuples, -1)::bigint
              FROM pg_inherits i
              JOIN pg_class parent ON parent.oid = i.inhparent
              JOIN pg_namespace n ON n.oid = parent.relnamespace
              JOIN pg_class child ON child.oid = i.inhrelid
             WHERE parent.relname = @name AND n.nspname = @schema
             ORDER BY child.relname
            """, schema, target.Name, ct))
            partitions.Add(new Partition(
                Text(row, 0) ?? "", Text(row, 1) ?? "", Number(row, 2), Number(row, 3)));

        return new Partitioning(true, true, strategy, Text(head, 1), partitions);
    }

    /// Detaching leaves the data as a table of its own; attaching needs the bound the parent's
    /// strategy expects. Both are statements, previewed like everything else.
    public static string PartitionStatement(
        IDbDriver driver, SchemaNodeRef target, string partition, string? bound, bool detach,
        bool concurrently)
    {
        if (string.IsNullOrWhiteSpace(partition))
            throw new ArgumentException("a partition name is required");

        var table = Qualify(driver, target);
        var child = target.Path.Count > 1
            ? $"{driver.Dialect.QuoteIdentifier(target.Path[0])}.{driver.Dialect.QuoteIdentifier(partition.Trim())}"
            : driver.Dialect.QuoteIdentifier(partition.Trim());

        if (detach)
            return $"ALTER TABLE {table} DETACH PARTITION {child}{(concurrently ? " CONCURRENTLY" : "")};";

        if (string.IsNullOrWhiteSpace(bound))
            throw new ArgumentException(
                "attaching needs the bound, e.g. FOR VALUES FROM ('2026-01-01') TO ('2026-02-01')");

        return $"ALTER TABLE {table} ATTACH PARTITION {child} {bound.Trim()};";
    }

    // --- materialised views -------------------------------------------------------------------

    /// Refreshing concurrently keeps the view readable while it runs, and needs a unique index —
    /// so the caller asks for it and the engine says no if it cannot.
    public static string RefreshStatement(IDbDriver driver, SchemaNodeRef target, bool concurrently)
    {
        if (target.Kind != SchemaNodeKind.MaterializedView)
            throw new ArgumentException("only a materialised view can be refreshed");

        return driver.Info.Id switch
        {
            "postgresql" =>
                $"REFRESH MATERIALIZED VIEW{(concurrently ? " CONCURRENTLY" : "")} {Qualify(driver, target)};",
            "oracle" =>
                $"BEGIN DBMS_MVIEW.REFRESH('{target.Name}', 'C'); END;",
            _ => throw new NotSupportedException(
                $"{driver.Info.Label} has no materialised view refresh the studio knows about"),
        };
    }

    // --- privileges across a whole schema ------------------------------------------------------

    /// "SELECT on everything in public for the reporting role" — one script rather than one dialog
    /// per table. `ALL TABLES IN SCHEMA` is one statement in PostgreSQL; elsewhere it is a list, so
    /// the caller passes the tables it found.
    public static string BulkGrantStatement(
        IDbDriver driver, string schema, string grantee, IReadOnlyList<string> privileges,
        IReadOnlyList<string> tables, bool revoke, bool includeFuture)
    {
        if (string.IsNullOrWhiteSpace(schema)) throw new ArgumentException("a schema is required");
        if (string.IsNullOrWhiteSpace(grantee)) throw new ArgumentException("a grantee is required");

        var allowed = ObjectPrivilegeReader.PrivilegesFor(driver.Info.Id);
        var wanted = privileges
            .Select(privilege => privilege.Trim().ToUpperInvariant())
            .Where(privilege => allowed.Contains(privilege, StringComparer.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        if (wanted.Count == 0) throw new ArgumentException("no privilege this engine offers was named");

        var who = driver.Info.Id == "mysql"
            ? $"{driver.Dialect.QuoteLiteral(grantee.Trim())}@'%'"
            : driver.Dialect.QuoteIdentifier(grantee.Trim());

        var list = string.Join(", ", wanted);
        var verb = revoke ? "REVOKE" : "GRANT";
        var direction = revoke ? "FROM" : "TO";
        var lines = new List<string>();

        if (driver.Info.Id == "postgresql")
        {
            var quoted = driver.Dialect.QuoteIdentifier(schema);
            lines.Add($"{verb} {list} ON ALL TABLES IN SCHEMA {quoted} {direction} {who};");

            // Without this, a table created tomorrow is not covered — the surprise behind half the
            // "but I granted that" conversations.
            if (includeFuture)
                lines.Add(revoke
                    ? $"ALTER DEFAULT PRIVILEGES IN SCHEMA {quoted} REVOKE {list} ON TABLES FROM {who};"
                    : $"ALTER DEFAULT PRIVILEGES IN SCHEMA {quoted} GRANT {list} ON TABLES TO {who};");
        }
        else
        {
            if (tables.Count == 0)
                throw new ArgumentException(
                    $"{driver.Info.Label} grants one table at a time, and no tables were found");

            foreach (var table in tables)
                lines.Add($"{verb} {list} ON " +
                          $"{driver.Dialect.QuoteIdentifier(schema)}.{driver.Dialect.QuoteIdentifier(table)} " +
                          $"{direction} {who};");
        }

        return string.Join("\n", lines);
    }

    // --- plumbing -----------------------------------------------------------------------------

    private static string Qualify(IDbDriver driver, SchemaNodeRef target) =>
        target.Path.Count > 1
            ? $"{driver.Dialect.QuoteIdentifier(target.Path[0])}.{driver.Dialect.QuoteIdentifier(target.Name)}"
            : driver.Dialect.QuoteIdentifier(target.Name);

    private static async Task<object?[]> OneAsync(
        IDbDriver driver, IDbSession session, string sql, string schema, string name,
        CancellationToken ct)
    {
        await foreach (var row in RowsAsync(driver, session, sql, schema, name, ct)) return row;
        return [];
    }

    private static async IAsyncEnumerable<object?[]> RowsAsync(
        IDbDriver driver, IDbSession session, string sql, string schema, string name,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ScriptRequest(sql, 500, 30, Parameters: new Dictionary<string, string?>
        {
            ["name"] = name,
            ["schema"] = schema,
        });

        await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
        {
            // A catalogue this server does not have — an old PostgreSQL without pg_publication —
            // is "nothing here", not an error to put in somebody's face.
            if (chunk is ResultChunk.Error) yield break;
            if (chunk is not ResultChunk.Rows rows) continue;

            foreach (var row in rows.Items) yield return row;
        }
    }

    private static string? Text(object?[] row, int index) =>
        row.Length > index ? row[index]?.ToString() : null;

    private static long? Number(object?[] row, int index) =>
        row.Length > index && row[index] is { } value && long.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;

    private static bool Flag(object?[] row, int index) =>
        row.Length > index && row[index] is { } value
        && (value is bool flag ? flag : value.ToString() is "1" or "True" or "true" or "t");
}
