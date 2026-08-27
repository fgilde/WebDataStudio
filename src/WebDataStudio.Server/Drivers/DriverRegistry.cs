using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.ClickHouse;
using WebDataStudio.Server.Drivers.DuckDb;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.MongoDb;
using WebDataStudio.Server.Drivers.Oracle;
using WebDataStudio.Server.Drivers.Redis;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Drivers.Storage;
using WebDataStudio.Server.Drivers.SqlServer;

namespace WebDataStudio.Server.Drivers;

public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDbDriver> _drivers;

    public DriverRegistry()
    {
        IDbDriver[] drivers =
        [
            new PostgreSqlDriver(),
            new MySqlDriver(),
            new SqlServerDriver(),
            new SqliteDriver(),
            new OracleDriver(),
            new DuckDbDriver(),
            new ClickHouseDriver(),
            new MongoDbDriver(),
            new RedisDriver(),
            new StorageDriver(),
        ];
        _drivers = drivers.ToDictionary(d => d.Info.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IDbDriver> All() => _drivers.Values;

    public IDbDriver Get(string engine) =>
        _drivers.TryGetValue(engine, out var driver)
            ? driver
            : throw new NotSupportedException($"no driver for engine '{engine}'");
}
