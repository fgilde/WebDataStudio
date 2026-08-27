using System.Data.Common;
using DuckDB.NET.Data;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Drivers.Storage;

/// A connection to a bucket, which is two things at once: the store, for listing and reading
/// objects, and a DuckDB in memory that has been told how to reach the same bucket.
public sealed class StorageSession : IDbSession
{
    public ConnectionSpec Spec { get; }
    public DbConnection Connection { get; }
    public IObjectStore Store { get; }

    private StorageSession(ConnectionSpec spec, DbConnection connection, IObjectStore store)
    {
        Spec = spec;
        Connection = connection;
        Store = store;
    }

    public static async Task<StorageSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        // For a storage connection the "connection string" is the URL the person configured.
        var store = ObjectStores.For(spec.ConnectionString);
        var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);

        try
        {
            foreach (var statement in DuckDbExtensions.Preamble(
                         store.Target.Provider, DuckDbExtensions.BundledDirectory))
                await RunAsync(connection, statement, ct);

            // The credentials go in as a DuckDB secret once, here, so no query has to carry them.
            if (store.SecretStatement() is { } secret) await RunAsync(connection, secret, ct);
        }
        catch
        {
            await connection.DisposeAsync();
            if (store is IDisposable disposable) disposable.Dispose();
            throw;
        }

        return new StorageSession(spec, connection, store);
    }

    private static async Task RunAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        if (Store is IDisposable disposable) disposable.Dispose();
    }
}
