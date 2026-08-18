using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
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
        ];
        _drivers = drivers.ToDictionary(d => d.Info.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IDbDriver> All() => _drivers.Values;

    public IDbDriver Get(string engine) =>
        _drivers.TryGetValue(engine, out var driver)
            ? driver
            : throw new NotSupportedException($"no driver for engine '{engine}'");
}
