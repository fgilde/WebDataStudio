using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A password is whatever somebody chose, and `#` is a character people choose. In a URL it starts
/// the fragment, so `postgresql://user:pw#@host/db` is not a URL at all — .NET refuses to parse it,
/// and the studio used to drop the connection without a word (issue #1).
///
/// The same file covers the other half the report walked into: a URL pasted into the connection
/// form was stored as it was typed and handed to the driver, which answers "Format of the
/// initialization string does not conform to specification" — for every URL, hash or no hash, while
/// the form's own description offers that shape.
public class ConnectionUrlPasswordTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-url-password").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    // --- the string, taken apart ------------------------------------------------------------------

    [Fact]
    public void A_password_with_a_hash_in_it_survives_the_url()
    {
        var read = ConnectionUrl.Read("postgresql://user:pw#@192.168.0.5:5432/shop");

        Assert.NotNull(read);
        Assert.Equal("postgresql", read!.Value.Engine);
        Assert.Equal("Host=192.168.0.5;Port=5432;Database=shop;Username=user;Password=pw#",
            read.Value.ConnectionString);
    }

    /// Every character that means something to a URL and nothing to a password.
    [Theory]
    [InlineData("pw#", "pw#")]
    [InlineData("pw?x", "pw?x")]
    [InlineData("pw/x", "pw/x")]
    [InlineData("a#b?c/d", "a#b?c/d")]
    [InlineData("pw with space", "pw with space")]
    [InlineData("pw&x", "pw&x")]
    public void The_password_arrives_as_it_was_typed(string typed, string expected)
    {
        var read = ConnectionUrl.Read($"postgresql://user:{typed}@db:5432/shop");

        Assert.NotNull(read);
        Assert.EndsWith($"Password={expected}", read!.Value.ConnectionString);
    }

    /// Somebody who already encoded their password is not encoded twice.
    [Fact]
    public void An_encoded_password_is_left_alone()
    {
        var read = ConnectionUrl.Read("postgresql://user:pw%23@db:5432/shop");

        Assert.EndsWith("Password=pw#", read!.Value.ConnectionString);
    }

    /// `@` inside a password is the other classic: the host is what follows the *last* one.
    [Fact]
    public void An_at_sign_in_the_password_does_not_move_the_host()
    {
        var read = ConnectionUrl.Read("postgresql://user:p@ss@db.example:5432/shop");

        Assert.NotNull(read);
        Assert.Contains("Host=db.example", read!.Value.ConnectionString);
        Assert.EndsWith("Password=p@ss", read.Value.ConnectionString);
    }

    /// The forms that are URLs on purpose keep being URLs: their drivers parse them.
    [Theory]
    [InlineData("mongodb://user:pw#@db:27017/events", "mongodb")]
    [InlineData("redis://user:pw#@cache:6379", "redis")]
    [InlineData("s3://bucket/prefix?region=eu-central-1", "storage")]
    [InlineData("https://services.example/odata/", "odata")]
    public void A_url_shaped_engine_keeps_its_url(string value, string engine)
    {
        var read = ConnectionUrl.Read(value);

        Assert.NotNull(read);
        Assert.Equal(engine, read!.Value.Engine);
        Assert.Contains("://", read.Value.ConnectionString);
    }

    [Fact]
    public void A_provider_connection_string_is_not_a_url_and_is_left_alone()
    {
        Assert.Null(ConnectionUrl.Read("Host=db;Database=shop;Username=u;Password=pw#"));
        Assert.Null(ConnectionUrl.Read("  "));
        Assert.Null(ConnectionUrl.Read("ftp://files.example/x"));
    }

    // --- WDS_CONN_*, which is where the report started --------------------------------------------

    [Fact]
    public void An_environment_connection_with_a_hash_in_its_password_is_offered()
    {
        var connections = EnvironmentConnections.Parse(new Dictionary<string, string?>
        {
            ["WDS_CONN_SHOP"] = "postgresql://user:pw#@192.168.0.5:5432/shop",
        });

        var only = Assert.Single(connections);

        Assert.Equal("SHOP", only.Name);
        Assert.Equal("postgresql", only.Engine);
        Assert.EndsWith("Password=pw#", only.ConnectionString);
    }

    // --- and the form, which promised the same shape ----------------------------------------------

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    [Fact]
    public async Task A_url_pasted_into_the_form_is_stored_as_the_driver_wants_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections", new
        {
            name = "SHOP",
            engine = "postgresql",
            connectionString = "postgresql://user:pw#@192.168.0.5:5432/shop",
            readOnly = true,
        }, Ct);

        answer.EnsureSuccessStatusCode();

        using var made = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Ct));
        var id = made.RootElement.GetProperty("id").GetString()!;

        // The summary is built from the connection string, so it says the host rather than a URL.
        using var properties = JsonDocument.Parse(
            await client.GetStringAsync($"/api/connections/{id}/properties", Ct));

        var text = properties.RootElement.ToString();

        Assert.Contains("192.168.0.5", text);
        Assert.DoesNotContain("postgresql://", text);
    }

    /// The message the report ran into. A URL is translated before the driver sees it, so whatever
    /// comes back is about the server rather than about the shape of the string.
    [Fact]
    public async Task Testing_a_url_no_longer_fails_on_the_shape_of_the_string()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections/test", new
        {
            name = "SHOP",
            engine = "postgresql",
            // Port 1: nothing listens there, so the answer is a refusal from the network.
            connectionString = "postgresql://user:pw#@127.0.0.1:1/shop",
            readOnly = true,
        }, Ct);

        answer.EnsureSuccessStatusCode();

        var said = await answer.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("initialization string", said);
    }
}
