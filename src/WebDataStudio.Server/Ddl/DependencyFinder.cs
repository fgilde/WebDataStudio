using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Ddl;

public sealed record DependencyReport(
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> UsedBy,
    bool BestEffort);

/// What breaks if this object changes. Every engine answers from its own catalogue; SQLite has no
/// dependency catalogue at all, so its answer is marked best-effort rather than presented as fact.
public static class DependencyFinder
{
    public static async Task<DependencyReport> FindAsync(IDbDriver driver, IDbSession session,
        SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path.Count > 1 ? target.Path[0] : "";
        var name = target.Name;

        return driver.Info.Id switch
        {
            "postgresql" => await PostgresAsync(session, schema, name, ct),
            "mysql" => await MySqlAsync(session, schema, name, ct),
            "sqlserver" => await SqlServerAsync(session, schema, name, ct),
            _ => await SqliteAsync(session, name, ct),
        };
    }

    private static async Task<DependencyReport> PostgresAsync(IDbSession session, string schema, string name,
        CancellationToken ct)
    {
        var usedBy = await ReadAsync(session, """
            SELECT DISTINCT dependent.relname
              FROM pg_depend d
              JOIN pg_rewrite r ON r.oid = d.objid
              JOIN pg_class dependent ON dependent.oid = r.ev_class
              JOIN pg_class source ON source.oid = d.refobjid
              JOIN pg_namespace n ON n.oid = source.relnamespace
             WHERE source.relname = @name AND (@schema = '' OR n.nspname = @schema)
               AND dependent.relname <> source.relname
            UNION
            SELECT t.relname
              FROM pg_constraint c
              JOIN pg_class t ON t.oid = c.conrelid
              JOIN pg_class ft ON ft.oid = c.confrelid
             WHERE c.contype = 'f' AND ft.relname = @name
            """, schema, name, ct);

        var dependsOn = await ReadAsync(session, """
            SELECT DISTINCT source.relname
              FROM pg_depend d
              JOIN pg_rewrite r ON r.oid = d.objid
              JOIN pg_class dependent ON dependent.oid = r.ev_class
              JOIN pg_class source ON source.oid = d.refobjid
             WHERE dependent.relname = @name AND source.relname <> dependent.relname
            UNION
            SELECT ft.relname
              FROM pg_constraint c
              JOIN pg_class t ON t.oid = c.conrelid
              JOIN pg_class ft ON ft.oid = c.confrelid
             WHERE c.contype = 'f' AND t.relname = @name
            """, schema, name, ct);

        return new DependencyReport(dependsOn, usedBy, false);
    }

    private static async Task<DependencyReport> MySqlAsync(IDbSession session, string schema, string name,
        CancellationToken ct)
    {
        var usedBy = await ReadAsync(session, """
            SELECT table_name FROM information_schema.views
             WHERE table_schema = COALESCE(NULLIF(@schema, ''), DATABASE())
               AND view_definition LIKE CONCAT('%', @name, '%')
            UNION
            SELECT table_name FROM information_schema.key_column_usage
             WHERE referenced_table_name = @name
               AND table_schema = COALESCE(NULLIF(@schema, ''), DATABASE())
            """, schema, name, ct);

        var dependsOn = await ReadAsync(session, """
            SELECT referenced_table_name FROM information_schema.key_column_usage
             WHERE table_name = @name AND referenced_table_name IS NOT NULL
               AND table_schema = COALESCE(NULLIF(@schema, ''), DATABASE())
            """, schema, name, ct);

        // The view scan is a text match, not a parse: a name that appears in a comment counts too.
        return new DependencyReport(dependsOn, usedBy, true);
    }

    private static async Task<DependencyReport> SqlServerAsync(IDbSession session, string schema, string name,
        CancellationToken ct)
    {
        var usedBy = await ReadAsync(session, """
            SELECT DISTINCT OBJECT_NAME(d.referencing_id)
              FROM sys.sql_expression_dependencies d
             WHERE d.referenced_entity_name = @name
               AND OBJECT_NAME(d.referencing_id) IS NOT NULL
            UNION
            SELECT OBJECT_NAME(f.parent_object_id)
              FROM sys.foreign_keys f
             WHERE OBJECT_NAME(f.referenced_object_id) = @name
            """, schema, name, ct);

        var dependsOn = await ReadAsync(session, """
            SELECT DISTINCT d.referenced_entity_name
              FROM sys.sql_expression_dependencies d
             WHERE OBJECT_NAME(d.referencing_id) = @name
            UNION
            SELECT OBJECT_NAME(f.referenced_object_id)
              FROM sys.foreign_keys f
             WHERE OBJECT_NAME(f.parent_object_id) = @name
            """, schema, name, ct);

        return new DependencyReport(dependsOn, usedBy, false);
    }

    private static async Task<DependencyReport> SqliteAsync(IDbSession session, string name, CancellationToken ct)
    {
        // SQLite keeps only the original DDL text, so this is a substring match over sqlite_master.
        var usedBy = new List<string>();
        var dependsOn = new List<string>();

        await using (var command = session.Connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var other = reader.GetString(0);
                var sql = reader.GetString(1);

                if (!other.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && sql.Contains(name, StringComparison.OrdinalIgnoreCase))
                    usedBy.Add(other);

                if (other.Equals(name, StringComparison.OrdinalIgnoreCase))
                    dependsOn.AddRange(await ReferencedTablesAsync(session, name, ct));
            }
        }

        return new DependencyReport(dependsOn.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            usedBy.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), true);
    }

    private static async Task<List<string>> ReferencedTablesAsync(IDbSession session, string table,
        CancellationToken ct)
    {
        var referenced = new List<string>();

        await using var command = session.Connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table.Replace("\"", "\"\"")}\")";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) referenced.Add(reader.GetString(2));
        return referenced;
    }

    private static async Task<List<string>> ReadAsync(IDbSession session, string sql, string schema, string name,
        CancellationToken ct)
    {
        var result = new List<string>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            foreach (var (parameterName, value) in new[] { ("schema", schema), ("name", name) })
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = parameterName;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        }
        catch (DbException)
        {
            // A catalogue the role cannot read yields no dependencies rather than an error.
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
