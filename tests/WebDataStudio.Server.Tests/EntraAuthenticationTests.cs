using Microsoft.Data.SqlClient;
using WebDataStudio.Server.Drivers.SqlServer;

namespace WebDataStudio.Server.Tests;

/// Aspire hands a deployed studio an Azure SQL connection string of the form
/// `Server=tcp:...;Encrypt=True;Authentication="Active Directory Default";Database=db` and gives
/// the container a managed identity. Microsoft.Data.SqlClient 7 moved Entra authentication into a
/// separate package, so without the provider registered that string cannot be opened at all —
/// which is invisible until somebody deploys.
public class EntraAuthenticationTests
{
    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryMSI)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal)]
    public void The_driver_registers_a_provider_for_every_entra_method_a_server_can_use(
        SqlAuthenticationMethod method)
    {
        // Touching the driver runs its static constructor, which is where the registration lives.
        _ = new SqlServerDriver().Info;

        var provider = SqlAuthenticationProvider.GetProvider(method);

        Assert.NotNull(provider);
        Assert.True(provider!.IsSupported(method));
    }

    [Fact]
    public void An_entra_connection_string_from_aspire_parses_the_way_the_driver_needs_it()
    {
        // The quotes are Aspire's, and a connection string that does not round-trip here would
        // fail with a parse error rather than an authentication one.
        var builder = new SqlConnectionStringBuilder(
            """Server=tcp:example.database.windows.net,1433;Encrypt=True;Authentication="Active Directory Default";Database=db""");

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDefault, builder.Authentication);
        Assert.Equal("db", builder.InitialCatalog);
        Assert.True(builder.Encrypt is SqlConnectionEncryptOption o && o == SqlConnectionEncryptOption.Mandatory);
    }
}
