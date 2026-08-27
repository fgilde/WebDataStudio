using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Drivers;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A PostgreSQL that is only reachable from inside a Docker network, plus an OpenSSH jump host
/// that is reachable from here. Nothing but a working tunnel can read the seeded rows.
public sealed class JumpHostFixture : IAsyncLifetime
{
    private INetwork _network = null!;
    private IContainer _postgres = null!;
    private IContainer _ssh = null!;

    public const string User = "wds";
    public const string Password = "wds-tunnel-pass";
    public const string DatabaseHost = "hidden-db";

    public int SshPort => _ssh.GetMappedPublicPort(2222);

    /// Points at the container alias, which does not resolve from the test host at all — that is
    /// what makes this a tunnel test rather than a connection test.
    public string HiddenConnectionString =>
        $"Host={DatabaseHost};Port=5432;Username=postgres;Password=secret;Database=shop;Timeout=10";

    public async ValueTask InitializeAsync()
    {
        _network = new NetworkBuilder().Build();

        _postgres = new ContainerBuilder()
            .WithImage("postgres:17-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(DatabaseHost)
            .WithEnvironment("POSTGRES_PASSWORD", "secret")
            .WithEnvironment("POSTGRES_DB", "shop")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
            .Build();

        // A plain sshd rather than a prebuilt image: the linuxserver one ships
        // AllowTcpForwarding off, which is exactly the feature under test here.
        _ssh = new ContainerBuilder()
            .WithImage("alpine:3.22")
            .WithNetwork(_network)
            .WithPortBinding(2222, true)
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand(
                "apk add --no-cache openssh >/dev/null && ssh-keygen -A && " +
                $"adduser -D {User} && echo '{User}:{Password}' | chpasswd && " +
                "sed -i 's/^#*AllowTcpForwarding.*/AllowTcpForwarding yes/' /etc/ssh/sshd_config && " +
                "/usr/sbin/sshd -D -e -p 2222")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Server listening on"))
            .Build();

        await _network.CreateAsync();
        await _postgres.StartAsync();
        await _ssh.StartAsync();

        // Seed through the postgres container itself: the test host cannot reach it.
        var seed = await _postgres.ExecAsync([
            "psql", "-U", "postgres", "-d", "shop", "-c",
            "CREATE TABLE people (id int primary key, name text); " +
            "INSERT INTO people VALUES (1,'ada'),(2,'linus');",
        ]);

        Assert.Equal(0, seed.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _ssh.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DeleteAsync();
    }
}

public class SshTunnelTests(JumpHostFixture fixture) : IClassFixture<JumpHostFixture>
{
    private TunnelSpec Spec() => new("127.0.0.1", fixture.SshPort, JumpHostFixture.User,
        Password: JumpHostFixture.Password);

    private ConnectionSpec Connection(TunnelSpec? tunnel) => new(
        "", "hidden", "postgresql", fixture.HiddenConnectionString,
        false, null, null, ConnectionSource.Stored, tunnel);

    /// Goes through the real store, so the tunnel takes the same encrypt-and-read path the
    /// application uses rather than a shortcut only the test knows.
    private static (SessionFactory Factory, string Id) Factory(ConnectionSpec spec, TunnelManager tunnels)
    {
        var directory = Directory.CreateTempSubdirectory("wds-tunnel").FullName;
        var protector = new SecretProtector(directory, Convert.ToBase64String(new byte[32]));
        var store = new ConnectionStore(Path.Combine(directory, "wds.db"), protector);
        var stored = store.Add(spec);

        var config = new ConfigurationBuilder().Build();
        var registry = new ConnectionRegistry(config, store);
        return (new SessionFactory(registry, new DriverRegistry(), tunnels, new SessionPool(config),
                new EntraSignIn(Microsoft.Extensions.Logging.Abstractions.NullLogger<EntraSignIn>.Instance)),
            stored.Id);
    }

    [Fact]
    public async Task The_database_is_not_reachable_without_the_tunnel()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var db = new NpgsqlConnection(fixture.HiddenConnectionString);
        await Assert.ThrowsAnyAsync<Exception>(() => db.OpenAsync(ct));
    }

    [Fact]
    public async Task A_query_runs_through_the_tunnel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tunnels = new TunnelManager();

        var (factory, id) = Factory(Connection(Spec()), tunnels);
        var (_, session) = await factory.OpenAsync(id, ct);
        await using (session)
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM people";
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
        }
    }

    [Fact]
    public async Task Two_sessions_share_one_tunnel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tunnels = new TunnelManager();
        var (factory, id) = Factory(Connection(Spec()), tunnels);

        var (_, first) = await factory.OpenAsync(id, ct);
        var (_, second) = await factory.OpenAsync(id, ct);

        await using (first)
        await using (second)
            Assert.Equal(1, tunnels.LiveCount);
    }

    [Fact]
    public async Task A_broken_tunnel_names_ssh_rather_than_timing_out()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tunnels = new TunnelManager();
        var broken = Spec() with { Password = "wrong" };

        var (factory, id) = Factory(Connection(broken), tunnels);
        var error = await Assert.ThrowsAsync<SshTunnelException>(() => factory.OpenAsync(id, ct));

        Assert.Contains("SSH", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tunnels.LiveCount);
    }

    [Fact]
    public async Task The_session_connects_through_the_local_forward()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tunnels = new TunnelManager();

        var (factory, id) = Factory(Connection(Spec()), tunnels);
        var (_, session) = await factory.OpenAsync(id, ct);

        // The session's spec is what actually connects, so anything shelling out to a dump tool
        // with it — pg_dump, mysqldump — goes through the tunnel too.
        await using (session)
        {
            Assert.Contains("127.0.0.1", session.Spec.ConnectionString);
            Assert.DoesNotContain(JumpHostFixture.DatabaseHost, session.Spec.ConnectionString);
        }
    }

    [Fact]
    public void A_stored_tunnel_never_reaches_the_client()
    {
        var directory = Directory.CreateTempSubdirectory("wds-tunnel-dto").FullName;
        var protector = new SecretProtector(directory, Convert.ToBase64String(new byte[32]));
        var store = new ConnectionStore(Path.Combine(directory, "wds.db"), protector);

        var stored = store.Add(Connection(Spec() with { PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----" }));
        var dto = ConnectionRegistry.ToDto(store.Get(stored.Id)!);

        // The round trip has to keep the key, and the DTO has to drop it.
        Assert.NotNull(store.Get(stored.Id)!.Tunnel!.PrivateKey);
        Assert.True(dto.Tunnelled);
        Assert.DoesNotContain("PRIVATE KEY", System.Text.Json.JsonSerializer.Serialize(dto));
    }
}
