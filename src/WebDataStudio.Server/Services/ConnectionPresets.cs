namespace WebDataStudio.Server.Services;

/// A connection somebody would otherwise have to write from memory.
///
/// `Template` is the connection string with `{}` placeholders a person fills in. `Interactive` says
/// that opening it needs a person to sign in — the studio's device-code flow — rather than the
/// machine's own identity.
public sealed record ConnectionPreset(
    string Id,
    string Label,
    string Engine,
    string Template,
    string Description,
    bool Interactive = false);

/// The managed services whose connection strings nobody remembers, with the two Azure ones that need
/// a person's sign-in marked as such.
///
/// A preset is a starting point, not a mode: what it produces is an ordinary connection string, and
/// it can be edited afterwards like any other.
public static class ConnectionPresets
{
    private static readonly ConnectionPreset[] All =
    [
        new("azure-sql-identity", "Azure SQL (managed identity)", "sqlserver",
            "Server=tcp:{server}.database.windows.net,1433;Database={database};" +
            "Encrypt=True;Authentication=\"Active Directory Default\"",
            "The studio's own identity signs in. The better answer wherever it exists: no secret to " +
            "carry, nothing to rotate."),

        new("azure-sql-interactive", "Azure SQL (my account)", "sqlserver",
            "Server=tcp:{server}.database.windows.net,1433;Database={database};" +
            "Encrypt=True;Authentication=\"Active Directory Device Code Flow\"",
            "You sign in yourself: the studio shows a code, you enter it on a device with a browser.",
            Interactive: true),

        new("azure-sql-password", "Azure SQL (Entra user and password)", "sqlserver",
            "Server=tcp:{server}.database.windows.net,1433;Database={database};" +
            "Encrypt=True;Authentication=\"Active Directory Password\";User Id={user};Password={password}",
            "An Entra account with a password, which rules out anything with multi-factor " +
            "authentication."),

        new("synapse-serverless", "Synapse serverless SQL pool", "sqlserver",
            "Server=tcp:{workspace}-ondemand.sql.azuresynapse.net,1433;Database={database};" +
            "Encrypt=True;Authentication=\"Active Directory Device Code Flow\"",
            "The on-demand endpoint of a Synapse workspace. Queries files in the lake; there is no " +
            "table to write to.",
            Interactive: true),

        new("synapse-dedicated", "Synapse dedicated SQL pool", "sqlserver",
            "Server=tcp:{workspace}.sql.azuresynapse.net,1433;Database={pool};" +
            "Encrypt=True;Authentication=\"Active Directory Default\"",
            "A provisioned pool, which behaves like a large SQL Server with a few features missing."),

        new("fabric-warehouse", "Microsoft Fabric warehouse", "sqlserver",
            "Server=tcp:{endpoint}.datawarehouse.fabric.microsoft.com,1433;Database={warehouse};" +
            "Encrypt=True;Authentication=\"Active Directory Device Code Flow\"",
            "The warehouse's SQL endpoint, from the workspace's connection settings. A lakehouse's " +
            "SQL analytics endpoint works the same way and is read-only.",
            Interactive: true),

        new("azure-postgres", "Azure Database for PostgreSQL", "postgresql",
            "postgres://{user}:{password}@{server}.postgres.database.azure.com:5432/{database}?sslmode=require",
            "Flexible server. SSL is required, and the user is the plain name rather than " +
            "user@server on current servers."),

        new("azure-mysql", "Azure Database for MySQL", "mysql",
            "mysql://{user}:{password}@{server}.mysql.database.azure.com:3306/{database}?sslmode=required",
            "Flexible server, with SSL required."),

        new("s3-bucket", "S3 bucket", "storage",
            "s3://{bucket}/{prefix}?region={region}",
            "AWS, or anything with an S3 endpoint: add ?endpoint= for MinIO, R2, Wasabi or Ceph. " +
            "No keys means the instance role."),

        new("azure-blob-container", "Azure Blob container", "storage",
            "azblob://{account}/{container}",
            "The studio's own managed identity reads it. Add ?key= or ?sas= where that is not " +
            "available."),
    ];

    public static IReadOnlyList<ConnectionPreset> For(string? engine) =>
        string.IsNullOrWhiteSpace(engine)
            ? All
            : All.Where(p => p.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase)).ToList();
}
