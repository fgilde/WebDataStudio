using System.Data.Common;
using ClickHouse.Client.ADO;
using Microsoft.Data.SqlClient;
using MongoDB.Driver;
using MySqlConnector;
using Npgsql;

namespace WebDataStudio.Server.Services;

/// Reads and rewrites the network endpoint of a connection string. Everything goes through the
/// driver's own builder — string surgery on a connection string is how passwords end up mangled.
public static class ConnectionEndpoint
{
    public static (string Host, int Port) Of(string engine, string connectionString) => engine switch
    {
        "postgresql" => Postgres(connectionString) is var p ? (p.Host ?? "localhost", p.Port) : default,
        "mysql" => MySql(connectionString) is var m ? (m.Server, (int)m.Port) : default,
        "sqlserver" => SqlServer(connectionString),
        "clickhouse" => ClickHouse(connectionString),
        "mongodb" => Mongo(connectionString),
        "redis" => Redis(connectionString),
        _ => throw new NotSupportedException($"{engine} has no network endpoint to tunnel"),
    };

    public static string Rewrite(string engine, string connectionString, string host, int port)
    {
        switch (engine)
        {
            case "postgresql":
            {
                var builder = Postgres(connectionString);
                builder.Host = host;
                builder.Port = port;
                // The certificate names the real server, not the loopback end of the tunnel.
                if (builder.SslMode is SslMode.VerifyFull) builder.SslMode = SslMode.VerifyCA;
                return builder.ToString();
            }

            case "mysql":
            {
                var builder = MySql(connectionString);
                builder.Server = host;
                builder.Port = (uint)port;
                return builder.ToString();
            }

            case "sqlserver":
            {
                var builder = new SqlConnectionStringBuilder(connectionString)
                {
                    DataSource = $"{host},{port}",
                };
                return builder.ToString();
            }

            case "clickhouse":
            {
                var builder = new ClickHouseConnectionStringBuilder(connectionString)
                {
                    Host = host,
                    Port = (ushort)port,
                };
                return builder.ToString();
            }

            case "mongodb":
            {
                var url = new MongoUrlBuilder(connectionString) { Server = new MongoServerAddress(host, port) };
                // A replica set name would make the driver rediscover the real members and go
                // straight past the tunnel; a tunnelled Mongo is a direct connection.
                url.ReplicaSetName = null;
                url.DirectConnection = true;
                return url.ToString();
            }

            case "redis":
            {
                var options = StackExchange.Redis.ConfigurationOptions.Parse(connectionString);
                options.EndPoints.Clear();
                options.EndPoints.Add(host, port);
                return options.ToString();
            }

            default:
                throw new NotSupportedException($"{engine} cannot be tunnelled");
        }
    }

    private static NpgsqlConnectionStringBuilder Postgres(string value) => new(value);
    private static MySqlConnectionStringBuilder MySql(string value) => new(value);

    private static (string, int) SqlServer(string value)
    {
        var source = new SqlConnectionStringBuilder(value).DataSource;
        var parts = source.Split(',', 2);
        return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var port) ? port : 1433);
    }

    private static (string, int) ClickHouse(string value)
    {
        var builder = new ClickHouseConnectionStringBuilder(value);
        return (builder.Host ?? "localhost", builder.Port);
    }

    private static (string, int) Mongo(string value)
    {
        var url = new MongoUrlBuilder(value);
        return (url.Server?.Host ?? "localhost", url.Server?.Port ?? 27017);
    }

    private static (string, int) Redis(string value)
    {
        var options = StackExchange.Redis.ConfigurationOptions.Parse(value);
        var endpoint = options.EndPoints.FirstOrDefault();

        return endpoint switch
        {
            System.Net.DnsEndPoint dns => (dns.Host, dns.Port),
            System.Net.IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            _ => ("localhost", 6379),
        };
    }

    /// True when this engine speaks over a socket at all — SQLite and DuckDB are files.
    public static bool IsNetworked(string engine) =>
        engine is "postgresql" or "mysql" or "sqlserver" or "clickhouse" or "mongodb" or "redis" or "oracle";

    internal static DbConnectionStringBuilder Builder(string value) => new() { ConnectionString = value };
}
