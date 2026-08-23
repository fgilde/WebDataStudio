using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One grant on an object: who has what, and whether they may pass it on.
public sealed record Grant(string Grantee, string Privilege, bool Grantable);

public sealed record ObjectPrivileges(
    bool Supported, IReadOnlyList<Grant> Grants, IReadOnlyList<string> Privileges);

/// Who may do what to one object, and the statement that changes it.
///
/// Reading is the useful half: "who can see this table" is a question people answer by guessing far
/// too often. Writing goes through the same preview-then-apply handshake as any other script — a
/// GRANT is a change like any other, and this hands over the statement rather than running it.
public static class ObjectPrivilegeReader
{
    /// The privileges worth offering per engine. Deliberately the common ones: a list nobody can
    /// read is not a wizard, it is a manual.
    public static IReadOnlyList<string> PrivilegesFor(string engine) => engine switch
    {
        "postgresql" => ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"],
        "mysql" => ["SELECT", "INSERT", "UPDATE", "DELETE", "INDEX", "ALTER", "REFERENCES"],
        "sqlserver" => ["SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES", "VIEW DEFINITION"],
        "oracle" => ["SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES", "ALTER", "INDEX"],
        _ => [],
    };

    public static async Task<ObjectPrivileges> ReadAsync(
        IDbDriver driver, IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var privileges = PrivilegesFor(driver.Info.Id);
        if (privileges.Count == 0 || Sql(driver.Info.Id) is not { } sql)
            return new ObjectPrivileges(false, [], []);

        var schema = target.Path.Count > 1 ? target.Path[0] : null;
        var grants = new List<Grant>();

        var request = new ScriptRequest(sql, 500, 30, Parameters: new Dictionary<string, string?>
        {
            ["name"] = target.Name,
            ["schema"] = schema,
        });

        await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
        {
            if (chunk is ResultChunk.Error) return new ObjectPrivileges(false, [], privileges);
            if (chunk is not ResultChunk.Rows rows) continue;

            foreach (var row in rows.Items)
                grants.Add(new Grant(
                    row.Length > 0 ? row[0]?.ToString() ?? "" : "",
                    row.Length > 1 ? row[1]?.ToString() ?? "" : "",
                    row.Length > 2 && row[2]?.ToString() is "YES" or "1" or "True" or "true"));
        }

        return new ObjectPrivileges(true,
            [.. grants.OrderBy(grant => grant.Grantee, StringComparer.OrdinalIgnoreCase)
                .ThenBy(grant => grant.Privilege, StringComparer.Ordinal)],
            privileges);
    }

    /// grantee, privilege, grantable — `information_schema` answers this almost everywhere, which
    /// is the one place the standard actually pays off.
    private static string? Sql(string engine) => engine switch
    {
        "postgresql" or "mysql" or "sqlserver" => """
            SELECT grantee, privilege_type, is_grantable
              FROM information_schema.table_privileges
             WHERE table_name = @name AND (@schema IS NULL OR table_schema = @schema)
            """,

        "oracle" => """
            SELECT grantee, privilege, grantable
              FROM all_tab_privs
             WHERE table_name = upper(:name) AND (:schema IS NULL OR table_schema = upper(:schema))
            """,

        _ => null,
    };

    /// The statement that grants or revokes. Handed over as text: it goes through the same preview
    /// as anything else that changes a database.
    public static string Statement(
        IDbDriver driver, SchemaNodeRef target, string grantee, string privilege, bool revoke)
    {
        if (!PrivilegesFor(driver.Info.Id).Contains(privilege, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"'{privilege}' is not a privilege this engine offers");

        // A grantee is an identifier, and MySQL writes it as a quoted user with a host.
        var who = driver.Info.Id == "mysql"
            ? $"{driver.Dialect.QuoteLiteral(grantee)}@'%'"
            : driver.Dialect.QuoteIdentifier(grantee);

        var table = target.Path.Count > 1
            ? $"{driver.Dialect.QuoteIdentifier(target.Path[0])}.{driver.Dialect.QuoteIdentifier(target.Name)}"
            : driver.Dialect.QuoteIdentifier(target.Name);

        return revoke
            ? $"REVOKE {privilege} ON {table} FROM {who};"
            : $"GRANT {privilege} ON {table} TO {who};";
    }
}
