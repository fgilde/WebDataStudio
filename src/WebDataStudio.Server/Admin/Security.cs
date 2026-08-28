using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Admin;

/// One account or role the server knows.
public sealed record DbPrincipal(
    string Name,
    /// A role is a bag of rights; a user is a role that can sign in. PostgreSQL makes no other
    /// distinction, and the rest are close enough to say it the same way.
    bool IsRole,
    bool CanLogin,
    bool Superuser,
    /// When the account stops working, where the engine keeps such a date.
    string? ValidUntil,
    bool Locked,
    /// The roles this one is a member of — the answer to "why can they read that".
    IReadOnlyList<string> MemberOf);

/// One right somebody has, on one thing.
public sealed record PrivilegeGrant(string Object, string Privilege, bool Grantable);

/// What is about to be changed. Every action ends as one statement, shown before it runs.
public sealed record SecurityChange(
    string Action, string Principal,
    string? Password = null, bool Role = false, bool CanLogin = true,
    string? Privilege = null, string? Target = null, string? Member = null);

/// Accounts, roles, and who may do what.
///
/// Reading is per engine and best effort: listing the server's roles is itself a privilege, and a
/// connection without it gets an empty list rather than an error. Writing is a statement and nothing
/// else — the preview and the apply endpoint run it, the same handshake every schema change has.
public static class Security
{
    public static async Task<IReadOnlyList<DbPrincipal>> ListAsync(IDbDriver driver, IDbSession session,
        CancellationToken ct)
    {
        var sql = driver.Info.Id switch
        {
            // rolcanlogin is the whole difference between a user and a role here.
            "postgresql" => """
                SELECT r.rolname, r.rolcanlogin, r.rolsuper,
                       to_char(r.rolvaliduntil, 'YYYY-MM-DD'),
                       coalesce(string_agg(g.rolname, ',' ORDER BY g.rolname), '')
                  FROM pg_roles r
                  LEFT JOIN pg_auth_members m ON m.member = r.oid
                  LEFT JOIN pg_roles g ON g.oid = m.roleid
                 WHERE left(r.rolname, 3) <> 'pg_'   -- the server's own roles are not the point
                 GROUP BY r.rolname, r.rolcanlogin, r.rolsuper, r.rolvaliduntil
                 ORDER BY r.rolname
                """,

            // MySQL 8 keeps roles in the same table; a role is an account that cannot log in from
            // anywhere, which is what account_locked plus the '%'-less host means in practice.
            "mysql" => """
                SELECT CONCAT(u.user, '@', u.host), u.account_locked = 'N', u.Super_priv = 'Y',
                       NULL,
                       coalesce((SELECT GROUP_CONCAT(CONCAT(e.from_user) ORDER BY e.from_user)
                                   FROM mysql.role_edges e
                                  WHERE e.to_user = u.user AND e.to_host = u.host), '')
                  FROM mysql.user u
                 ORDER BY u.user, u.host
                """,

            "sqlserver" => """
                SELECT p.name, p.type <> 'R', IS_SRVROLEMEMBER('sysadmin', p.name),
                       CONVERT(varchar(10), p.modify_date, 23),
                       coalesce(STUFF((SELECT ',' + r.name
                                         FROM sys.server_role_members m
                                         JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
                                        WHERE m.member_principal_id = p.principal_id
                                          FOR XML PATH('')), 1, 1, ''), '')
                  FROM sys.server_principals p
                 WHERE p.type IN ('S', 'U', 'R') AND p.name NOT LIKE '##%'
                 ORDER BY p.name
                """,

            "oracle" => """
                SELECT u.username, 1, 0, TO_CHAR(u.expiry_date, 'YYYY-MM-DD'),
                       (SELECT LISTAGG(r.granted_role, ',') WITHIN GROUP (ORDER BY r.granted_role)
                          FROM user_role_privs r WHERE r.username = u.username)
                  FROM all_users u ORDER BY u.username
                """,

            _ => null,
        };

        if (sql is null) return [];

        var principals = new List<DbPrincipal>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var canLogin = !reader.IsDBNull(1) && ToBool(reader.GetValue(1));
                var members = reader.IsDBNull(4) ? "" : reader.GetValue(4)?.ToString() ?? "";

                principals.Add(new DbPrincipal(
                    reader.GetString(0),
                    IsRole: !canLogin,
                    CanLogin: canLogin,
                    Superuser: !reader.IsDBNull(2) && ToBool(reader.GetValue(2)),
                    ValidUntil: reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString(),
                    Locked: false,
                    MemberOf: members.Length == 0
                        ? []
                        : members.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
        }
        catch (DbException)
        {
            // Reading the account list is itself a privilege. Without it the panel stays empty
            // rather than showing an error somebody can do nothing about.
        }

        return principals;
    }

    /// Everything one account or role may do, as far as this connection is allowed to see. The
    /// answer to "why can they read that" once the role list alone does not explain it.
    public static async Task<IReadOnlyList<PrivilegeGrant>> GrantsAsync(IDbDriver driver,
        IDbSession session, string principal, CancellationToken ct)
    {
        var sql = driver.Info.Id switch
        {
            "postgresql" => """
                SELECT table_schema || '.' || table_name, privilege_type, is_grantable
                  FROM information_schema.role_table_grants
                 WHERE grantee = @p
                 ORDER BY table_schema, table_name, privilege_type
                """,

            "mysql" => null, // SHOW GRANTS is not a query with columns; handled below.

            "sqlserver" => """
                SELECT coalesce(o.name, 'the database'), p.permission_name,
                       CASE WHEN p.state = 'W' THEN 1 ELSE 0 END
                  FROM sys.database_permissions p
                  LEFT JOIN sys.objects o ON o.object_id = p.major_id
                  JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id
                 WHERE u.name = @p AND p.state IN ('G', 'W')
                 ORDER BY o.name, p.permission_name
                """,

            "oracle" => """
                SELECT owner || '.' || table_name, privilege,
                       CASE grantable WHEN 'YES' THEN 1 ELSE 0 END
                  FROM all_tab_privs WHERE grantee = :p ORDER BY owner, table_name, privilege
                """,

            _ => null,
        };

        var grants = new List<PrivilegeGrant>();

        try
        {
            if (driver.Info.Id == "mysql")
            {
                // MySQL answers with one text column per grant, so it is read as text and shown as
                // text rather than pretended into columns it does not have.
                await using var show = session.Connection.CreateCommand();
                show.CommandText = $"SHOW GRANTS FOR {Quote(driver, principal)}";

                await using var lines = await show.ExecuteReaderAsync(ct);
                while (await lines.ReadAsync(ct))
                    grants.Add(new PrivilegeGrant("", lines.GetString(0), false));

                return grants;
            }

            if (sql is null) return [];

            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            var parameter = command.CreateParameter();
            parameter.ParameterName = driver.Info.Id == "oracle" ? "p" : "@p";
            parameter.Value = principal;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                grants.Add(new PrivilegeGrant(
                    reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "",
                    reader.GetValue(1)?.ToString() ?? "",
                    !reader.IsDBNull(2) && ToBool(reader.GetValue(2))));
        }
        catch (DbException)
        {
            // Same as the list: without the privilege to read privileges, the panel says nothing.
        }

        return grants;
    }

    /// The statement one change makes. Nothing here runs anything — the preview shows it and the
    /// apply endpoint runs it, which is where the read-only check lives.
    public static string Statement(IDbDriver driver, SecurityChange change)
    {
        var engine = driver.Info.Id;
        var name = Quote(driver, change.Principal);

        return change.Action switch
        {
            "create" => Create(driver, change),
            "drop" => engine switch
            {
                "postgresql" => $"DROP ROLE {name};",
                "mysql" => change.Role ? $"DROP ROLE {name};" : $"DROP USER {name};",
                "sqlserver" => change.Role ? $"DROP ROLE {name};" : $"DROP LOGIN {name};",
                "oracle" => change.Role ? $"DROP ROLE {name};" : $"DROP USER {name};",
                _ => throw Unsupported(driver),
            },

            "password" => engine switch
            {
                "postgresql" => $"ALTER ROLE {name} PASSWORD {Literal(driver, change.Password)};",
                "mysql" => $"ALTER USER {name} IDENTIFIED BY {Literal(driver, change.Password)};",
                "sqlserver" => $"ALTER LOGIN {name} WITH PASSWORD = {Literal(driver, change.Password)};",
                "oracle" => $"ALTER USER {name} IDENTIFIED BY {driver.Dialect.QuoteIdentifier(change.Password ?? "")};",
                _ => throw Unsupported(driver),
            },

            // Whether an account may sign in at all: the cheapest way to stop one without losing
            // what it is allowed to do.
            "login" => engine switch
            {
                "postgresql" => $"ALTER ROLE {name} {(change.CanLogin ? "LOGIN" : "NOLOGIN")};",
                "mysql" => $"ALTER USER {name} ACCOUNT {(change.CanLogin ? "UNLOCK" : "LOCK")};",
                "sqlserver" => $"ALTER LOGIN {name} {(change.CanLogin ? "ENABLE" : "DISABLE")};",
                "oracle" => $"ALTER USER {name} ACCOUNT {(change.CanLogin ? "UNLOCK" : "LOCK")};",
                _ => throw Unsupported(driver),
            },

            "grant-role" => Membership(driver, change, grant: true),
            "revoke-role" => Membership(driver, change, grant: false),

            "grant" => $"GRANT {Privilege(change)} ON {Target(change)} TO {name};",
            "revoke" => $"REVOKE {Privilege(change)} ON {Target(change)} FROM {name};",

            _ => throw new NotSupportedException($"'{change.Action}' is not something this can write"),
        };
    }

    private static string Create(IDbDriver driver, SecurityChange change)
    {
        var name = Quote(driver, change.Principal);
        var password = Literal(driver, change.Password);

        return driver.Info.Id switch
        {
            // One statement for both, because in PostgreSQL a user *is* a role that can log in.
            "postgresql" => change.Role
                ? $"CREATE ROLE {name} NOLOGIN;"
                : $"CREATE ROLE {name} LOGIN PASSWORD {password};",

            "mysql" => change.Role
                ? $"CREATE ROLE {name};"
                : $"CREATE USER {name} IDENTIFIED BY {password};",

            "sqlserver" => change.Role
                ? $"CREATE ROLE {name};"
                : $"CREATE LOGIN {name} WITH PASSWORD = {password};",

            "oracle" => change.Role
                ? $"CREATE ROLE {name};"
                : $"CREATE USER {name} IDENTIFIED BY {driver.Dialect.QuoteIdentifier(change.Password ?? "")};",

            _ => throw Unsupported(driver),
        };
    }

    /// Putting somebody in a role, or taking them out of it. SQL Server spells this as an ALTER on
    /// the role rather than as a GRANT.
    private static string Membership(IDbDriver driver, SecurityChange change, bool grant)
    {
        if (change.Member is not { Length: > 0 })
            throw new NotSupportedException("which member is missing from this change");

        var role = Quote(driver, change.Principal);
        var member = Quote(driver, change.Member);

        return driver.Info.Id switch
        {
            "sqlserver" => grant
                ? $"ALTER ROLE {role} ADD MEMBER {member};"
                : $"ALTER ROLE {role} DROP MEMBER {member};",

            _ => grant ? $"GRANT {role} TO {member};" : $"REVOKE {role} FROM {member};",
        };
    }

    /// A privilege list is identifiers, not values, so it is checked rather than parameterised:
    /// anything but words, commas and spaces is refused.
    private static string Privilege(SecurityChange change)
    {
        var privilege = (change.Privilege ?? "").Trim();

        if (privilege.Length == 0) throw new NotSupportedException("which privilege is missing");

        if (!privilege.All(c => char.IsLetter(c) || c is ' ' or ','))
            throw new NotSupportedException($"'{privilege}' is not a privilege");

        return privilege.ToUpperInvariant();
    }

    /// What the privilege is on, as the person typed it: a table, a schema, `ALL TABLES IN SCHEMA
    /// public`, `DATABASE shop`. Quoting it would break every one of those forms, so it is checked
    /// for the characters that would end the statement instead.
    private static string Target(SecurityChange change)
    {
        var target = (change.Target ?? "").Trim();
        if (target.Length == 0) throw new NotSupportedException("which object is missing");

        if (target.Contains(';') || target.Contains("--"))
            throw new NotSupportedException($"'{target}' is not one object");

        return target;
    }

    /// MySQL names an account with its host and quotes it as a string; everything else quotes an
    /// identifier.
    private static string Quote(IDbDriver driver, string name)
    {
        if (driver.Info.Id != "mysql") return driver.Dialect.QuoteIdentifier(name);

        var parts = name.Split('@', 2);
        var host = parts.Length > 1 ? parts[1] : "%";

        return $"{driver.Dialect.QuoteLiteral(parts[0])}@{driver.Dialect.QuoteLiteral(host)}";
    }

    private static string Literal(IDbDriver driver, string? value) =>
        driver.Dialect.QuoteLiteral(value ?? "");

    private static NotSupportedException Unsupported(IDbDriver driver) =>
        new($"{driver.Info.Label} has no user management the studio can write");

    private static bool ToBool(object value) => value switch
    {
        bool flag => flag,
        int number => number != 0,
        long number => number != 0,
        decimal number => number != 0,
        string text => text is "Y" or "y" or "1" or "true" or "True",
        _ => false,
    };
}
