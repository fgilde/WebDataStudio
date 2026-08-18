using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

/// A live database an engine's contract test can run against. Each implementation seeds the same
/// `people` table (id, name, active) with three rows and an `orders` table with a foreign key to it.
public interface IDriverFixture : IAsyncLifetime
{
    IDbDriver Driver { get; }
    ConnectionSpec Spec { get; }
    /// The schema the seeded tables live in, or null for engines without schemas.
    string? Schema { get; }
}
