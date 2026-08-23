using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Admin;

public sealed record BackupOptions(
    bool SchemaOnly, bool DataOnly, IReadOnlyList<string>? Tables,
    /// plain, custom or tar. Only pg_dump has more than one; the others say so rather than
    /// producing a plain dump with a misleading extension.
    string? Format = null,
    bool NoOwner = false, bool Clean = false, int? Compress = null);

public sealed record BackupPlan(
    string File, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment,
    string Extension, string ContentType);

/// Backup and restore shell the engine's own dump tool. Everything about how each tool is invoked
/// lives here; the endpoint only streams what comes back.
public static class BackupService
{
    public static BackupPlan Plan(IDbDriver driver, ConnectionSpec spec, BackupOptions options)
    {
        if (!driver.Caps.Backup)
            throw new NotSupportedException($"{driver.Info.Label} has no backup tool wired up");

        return driver.Info.Id switch
        {
            "postgresql" => PostgresPlan(spec, options),
            "mysql" => MySqlPlan(spec, options),
            "mongodb" => MongoPlan(spec, options),
            "redis" => RedisPlan(spec, options),
            _ => throw new NotSupportedException($"{driver.Info.Label} has no backup tool wired up"),
        };
    }

    private static BackupPlan PostgresPlan(ConnectionSpec spec, BackupOptions options)
    {
        var builder = new NpgsqlConnectionStringBuilder(spec.ConnectionString);
        var format = (options.Format ?? "plain").ToLowerInvariant();
        if (format is not ("plain" or "custom" or "tar"))
            throw new NotSupportedException($"'{format}' is not a pg_dump format");

        var arguments = new List<string>
        {
            "--host", builder.Host ?? "localhost",
            "--port", builder.Port.ToString(),
            "--username", builder.Username ?? "postgres",
            "--no-password",
            "--format", format,
        };

        if (options.SchemaOnly) arguments.Add("--schema-only");
        if (options.DataOnly) arguments.Add("--data-only");
        if (options.NoOwner) arguments.Add("--no-owner");

        // A plain dump can carry the DROPs that make it replayable over an existing database; the
        // other formats decide that at restore time, so pg_dump refuses the flag there.
        if (options.Clean && format == "plain") arguments.Add("--clean");
        if (options.Clean && format != "plain")
            throw new NotSupportedException(
                "'clean' belongs to a plain dump; a custom or tar dump decides that when it is restored");

        if (options.Compress is { } level)
        {
            if (level is < 0 or > 9) throw new NotSupportedException("compression runs from 0 to 9");
            arguments.Add("--compress"); arguments.Add(level.ToString());
        }

        foreach (var table in options.Tables ?? []) { arguments.Add("--table"); arguments.Add(table); }
        arguments.Add(builder.Database ?? "postgres");

        // The extension has to match what comes out: a custom dump named .sql is a file nobody can
        // restore twice.
        var (extension, contentType) = format switch
        {
            "custom" => ("dump", "application/octet-stream"),
            "tar" => ("tar", "application/x-tar"),
            _ => options.Compress is > 0
                ? ("sql.gz", "application/gzip")
                : ("sql", "application/sql"),
        };

        return new BackupPlan("pg_dump", arguments,
            // PGPASSWORD, not --password: an argument would show up in every process listing.
            new Dictionary<string, string> { ["PGPASSWORD"] = builder.Password ?? "" },
            extension, contentType);
    }

    private static BackupPlan MySqlPlan(ConnectionSpec spec, BackupOptions options)
    {
        Refuse(options, "mysqldump");

        var builder = new MySqlConnectionStringBuilder(spec.ConnectionString);
        var arguments = new List<string>
        {
            $"--host={builder.Server}",
            $"--port={builder.Port}",
            $"--user={builder.UserID}",
            "--skip-ssl",
        };

        if (options.SchemaOnly) arguments.Add("--no-data");
        if (options.DataOnly) arguments.Add("--no-create-info");
        arguments.Add(builder.Database);
        foreach (var table in options.Tables ?? []) arguments.Add(table);

        return new BackupPlan("mysqldump", arguments,
            new Dictionary<string, string> { ["MYSQL_PWD"] = builder.Password ?? "" },
            "sql", "application/sql");
    }

    /// Options that only pg_dump has. Ignoring them would produce a file that does not match what
    /// the dialog said it asked for.
    private static void Refuse(BackupOptions options, string tool)
    {
        if (options.Format is { } format && !format.Equals("plain", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{tool} writes one format only");
        if (options.NoOwner) throw new NotSupportedException($"{tool} has no --no-owner");
        if (options.Clean) throw new NotSupportedException($"{tool} has no --clean");
        if (options.Compress is not null) throw new NotSupportedException($"{tool} does not compress");
    }

    private static BackupPlan MongoPlan(ConnectionSpec spec, BackupOptions options)
    {
        Refuse(options, "mongodump");
        return new BackupPlan("mongodump", ["--uri", spec.ConnectionString, "--archive"],
            new Dictionary<string, string>(), "archive", "application/octet-stream");
    }

    private static BackupPlan RedisPlan(ConnectionSpec spec, BackupOptions options)
    {
        Refuse(options, "redis-cli");

        var url = new Uri(spec.ConnectionString.StartsWith("redis", StringComparison.OrdinalIgnoreCase)
            ? spec.ConnectionString
            : "redis://" + spec.ConnectionString);

        return new BackupPlan("redis-cli",
            ["-h", url.Host, "-p", (url.IsDefaultPort ? 6379 : url.Port).ToString(), "--rdb", "-"],
            new Dictionary<string, string>(), "rdb", "application/octet-stream");
    }

    /// SQLite copies itself consistently without an external tool, and SQL Server backs up
    /// server-side to a path the server can reach — neither streams through us.
    public static async Task<string?> BackupInProcessAsync(IDbDriver driver, IDbSession session,
        string targetPath, CancellationToken ct)
    {
        switch (driver.Info.Id)
        {
            case "sqlite":
            {
                await using var command = session.Connection.CreateCommand();
                command.CommandText = $"VACUUM INTO '{targetPath.Replace("'", "''")}'";
                await command.ExecuteNonQueryAsync(ct);
                return targetPath;
            }

            case "sqlserver":
            {
                var database = new SqlConnectionStringBuilder(session.Spec.ConnectionString).InitialCatalog;
                await using var command = session.Connection.CreateCommand();
                command.CommandText =
                    $"BACKUP DATABASE [{database.Replace("]", "]]")}] TO DISK = @path WITH INIT";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@path";
                parameter.Value = targetPath;
                command.Parameters.Add(parameter);

                await command.ExecuteNonQueryAsync(ct);
                // The file lands on the database server, not here: the caller gets the path.
                return targetPath;
            }

            default:
                return null;
        }
    }

    public static BackupPlan RestorePlan(IDbDriver driver, ConnectionSpec spec)
    {
        if (!driver.Caps.Restore)
            throw new NotSupportedException($"{driver.Info.Label} has no restore tool wired up");

        return driver.Info.Id switch
        {
            "postgresql" => PostgresRestore(spec),
            "mysql" => MySqlRestore(spec),
            "mongodb" => new BackupPlan("mongorestore", ["--uri", spec.ConnectionString, "--archive"],
                new Dictionary<string, string>(), "archive", "application/octet-stream"),
            _ => throw new NotSupportedException($"{driver.Info.Label} has no restore tool wired up"),
        };
    }

    private static BackupPlan PostgresRestore(ConnectionSpec spec)
    {
        var builder = new NpgsqlConnectionStringBuilder(spec.ConnectionString);
        return new BackupPlan("psql",
            [
                "--host", builder.Host ?? "localhost",
                "--port", builder.Port.ToString(),
                "--username", builder.Username ?? "postgres",
                "--no-password",
                "--dbname", builder.Database ?? "postgres",
            ],
            new Dictionary<string, string> { ["PGPASSWORD"] = builder.Password ?? "" },
            "sql", "application/sql");
    }

    private static BackupPlan MySqlRestore(ConnectionSpec spec)
    {
        var builder = new MySqlConnectionStringBuilder(spec.ConnectionString);
        return new BackupPlan("mysql",
            [
                $"--host={builder.Server}", $"--port={builder.Port}", $"--user={builder.UserID}",
                "--skip-ssl", builder.Database,
            ],
            new Dictionary<string, string> { ["MYSQL_PWD"] = builder.Password ?? "" },
            "sql", "application/sql");
    }

    /// True when the tool the plan needs is actually on PATH inside this container.
    public static bool ToolAvailable(string file)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var candidates = OperatingSystem.IsWindows() ? new[] { file + ".exe", file } : [file];

        return paths.Any(directory => candidates.Any(candidate =>
        {
            try { return File.Exists(Path.Combine(directory, candidate)); }
            catch (ArgumentException) { return false; }
        }));
    }

    internal static string DatabaseName(DbConnection connection) => connection.Database;
}
