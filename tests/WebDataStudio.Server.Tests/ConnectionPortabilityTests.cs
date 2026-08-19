using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class ConnectionPortabilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-portable").FullName;

    public void Dispose()
    {
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    private static object Definition(string name) => new
    {
        name,
        engine = "postgresql",
        connectionString = "Host=db.example;Port=5432;Username=me;Password=s3cret;Database=shop",
        readOnly = false,
    };

    [Fact]
    public async Task An_export_carries_no_secret()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Definition("prod"), ct))
            .EnsureSuccessStatusCode();

        var raw = await client.GetStringAsync("/api/connections/export", ct);

        Assert.Contains("db.example", raw);
        Assert.DoesNotContain("s3cret", raw);
        Assert.DoesNotContain("connectionString", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_import_recreates_the_definitions_without_credentials()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Definition("prod"), ct))
            .EnsureSuccessStatusCode();

        var exported = await client.GetFromJsonAsync<JsonElement>("/api/connections/export", ct);
        (await client.DeleteAsync(
            $"/api/connections/{(await Ids(client))["prod"]}", ct)).EnsureSuccessStatusCode();

        var result = await (await client.PostAsJsonAsync("/api/connections/import", exported, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("prod", result.GetProperty("imported")[0].GetString());

        var summary = (await client.GetFromJsonAsync<JsonElement>("/api/connections", ct))
            .EnumerateArray().Single().GetProperty("summary").GetString();
        Assert.Contains("db.example", summary);
    }

    [Fact]
    public async Task A_duplicate_name_is_reported_without_aborting_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Definition("prod"), ct))
            .EnsureSuccessStatusCode();

        var payload = new[]
        {
            new { name = "prod", engine = "postgresql", readOnly = false, host = "x", database = "y" },
            new { name = "staging", engine = "postgresql", readOnly = false, host = "x", database = "y" },
        };

        var result = await (await client.PostAsJsonAsync("/api/connections/import", payload, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("staging", result.GetProperty("imported")[0].GetString());
        Assert.Equal("prod", result.GetProperty("skipped")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_unknown_engine_is_skipped_rather_than_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var payload = new[] { new { name = "weird", engine = "cobol", readOnly = false } };
        var result = await (await client.PostAsJsonAsync("/api/connections/import", payload, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Empty(result.GetProperty("imported").EnumerateArray());
        Assert.Contains("cobol", result.GetProperty("skipped")[0].GetProperty("reason").GetString());
    }

    private static async Task<Dictionary<string, string>> Ids(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("id").GetString()!);
    }
}

public class ConnectionSecretTests
{
    [Theory]
    [InlineData("Host=db;Username=me;Password=s3cret;Database=shop")]
    [InlineData("Server=db;Uid=me;Pwd=s3cret")]
    [InlineData("Host=db;PASSWORD=s3cret")]
    public void A_password_is_replaced_by_a_mask(string connectionString)
    {
        var hidden = ConnectionSecret.Hide(connectionString);

        Assert.DoesNotContain("s3cret", hidden);
        Assert.Contains("•", hidden);
        Assert.True(ConnectionSecret.HasPassword(connectionString));
    }

    [Fact]
    public void The_mask_hides_the_length_of_the_password()
    {
        var shortOne = ConnectionSecret.Hide("Host=db;Password=x");
        var longOne = ConnectionSecret.Hide("Host=db;Password=xxxxxxxxxxxxxxxxxxxxxxxxxxxx");

        Assert.Equal(shortOne, longOne);
    }

    [Fact]
    public void A_password_inside_a_url_is_masked_too()
    {
        var hidden = ConnectionSecret.Hide("postgres://app:s3cret@db:5432/shop");

        Assert.DoesNotContain("s3cret", hidden);
        Assert.Contains("postgres://app:", hidden);
        Assert.Contains("@db:5432/shop", hidden);
    }

    [Fact]
    public void Everything_but_the_password_survives()
    {
        var hidden = ConnectionSecret.Hide("Host=db;Port=5432;Username=me;Password=s3cret;Database=shop");

        Assert.Contains("Host=db", hidden);
        Assert.Contains("Port=5432", hidden);
        Assert.Contains("Username=me", hidden);
        Assert.Contains("Database=shop", hidden);
    }

    [Fact]
    public void A_string_without_a_password_is_left_alone()
    {
        const string value = "Data Source=/data/local.db";

        Assert.Equal(value, ConnectionSecret.Hide(value));
        Assert.False(ConnectionSecret.HasPassword(value));
    }

    [Fact]
    public void An_empty_password_is_not_reported_as_one()
    {
        // Password= with nothing after it hides nothing, so the UI must not offer to reveal it.
        Assert.False(ConnectionSecret.HasPassword("Host=db;Password=;Database=shop"));
    }
}

public class ConnectionPropertiesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-properties").FullName;
    private readonly string _db;

    public ConnectionPropertiesTests()
    {
        _db = Path.Combine(_dir, "shop.db");

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONNECTIONS"] = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = "SHOP", engine = "sqlite", connectionString,
                        readOnly = true, group = "Development",
                    },
                }),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Properties_describe_the_definition_and_the_server()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory($"Data Source={_db}");
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/connections/{await IdAsync(client)}/properties", ct);

        var named = body.GetProperty("properties").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString());

        Assert.True(body.GetProperty("reachable").GetBoolean());
        Assert.Equal("SHOP", named["Name"]);
        Assert.Equal("read-only", named["Access"]);
        Assert.Equal("Development", named["Group"]);
        Assert.Contains("environment", named["Defined in"]!);
        // Read from the file itself, not from the definition.
        Assert.False(string.IsNullOrWhiteSpace(named["Version"]));
        Assert.True(named.ContainsKey("Size"));
    }

    [Fact]
    public async Task Properties_carry_the_capabilities_of_the_engine()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory($"Data Source={_db}");
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/connections/{await IdAsync(client)}/properties", ct);

        var caps = body.GetProperty("capabilities");
        Assert.True(caps.GetProperty("ddl").GetBoolean());
        Assert.False(caps.GetProperty("multiDatabase").GetBoolean());
    }

    [Fact]
    public async Task The_connection_string_arrives_masked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory("Data Source=x.db;Password=s3cret");
        var client = factory.CreateClient();

        var raw = await client.GetStringAsync(
            $"/api/connections/{await IdAsync(client)}/properties", ct);

        Assert.DoesNotContain("s3cret", raw);
        Assert.Contains("Data Source=x.db", raw);
    }

    [Fact]
    public async Task The_password_comes_only_when_it_is_asked_for()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory("Data Source=x.db;Password=s3cret");
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var revealed = await client.PostAsync($"/api/connections/{id}/reveal", null, ct);
        var body = await revealed.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("s3cret", body.GetProperty("connectionString").GetString()!);
    }

    [Fact]
    public async Task An_unreachable_server_still_describes_the_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        // A directory that does not exist: SQLite cannot open the file.
        using var factory = Factory("Data Source=/no/such/place/missing.db");
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/connections/{await IdAsync(client)}/properties", ct);

        Assert.False(body.GetProperty("reachable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        Assert.Contains(body.GetProperty("properties").EnumerateArray(),
            e => e.GetProperty("name").GetString() == "Name");
    }

    [Fact]
    public async Task An_unknown_connection_is_a_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory($"Data Source={_db}");

        Assert.Equal(HttpStatusCode.NotFound,
            (await factory.CreateClient().GetAsync("/api/connections/nope/properties", ct)).StatusCode);
    }
}
