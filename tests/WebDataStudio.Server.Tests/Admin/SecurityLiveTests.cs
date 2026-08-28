using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Tests.Drivers;

namespace WebDataStudio.Server.Tests.Admin;

/// The reading half, against a real server: who exists, which of them can sign in, who is in which
/// role, and what one of them may do.
public class SecurityLiveTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task RunAsync(IDbSession session, string sql)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Accounts_and_roles_come_back_together_with_what_separates_them()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        await RunAsync(session, """
            DROP ROLE IF EXISTS wds_reader;
            DROP ROLE IF EXISTS wds_analyst;
            CREATE ROLE wds_reader NOLOGIN;
            CREATE ROLE wds_analyst LOGIN PASSWORD 'p';
            GRANT wds_reader TO wds_analyst;
            GRANT SELECT ON people TO wds_reader;
            """);

        var principals = await Security.ListAsync(fixture.Driver, session, Ct);

        var reader = Assert.Single(principals, p => p.Name == "wds_reader");
        var analyst = Assert.Single(principals, p => p.Name == "wds_analyst");

        // A role is a bag of rights nobody signs in as; the account is the one that can.
        Assert.True(reader.IsRole);
        Assert.False(reader.CanLogin);
        Assert.True(analyst.CanLogin);
        Assert.False(analyst.IsRole);

        // And the answer to "why can they read that": the role they are in.
        Assert.Contains("wds_reader", analyst.MemberOf);

        // The server's own roles are not the interesting ones and stay out of the list.
        Assert.DoesNotContain(principals, p => p.Name.StartsWith("pg_"));
    }

    [Fact]
    public async Task What_one_of_them_may_do_is_asked_for_by_name()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        await RunAsync(session, """
            DROP ROLE IF EXISTS wds_grants;
            CREATE ROLE wds_grants NOLOGIN;
            GRANT SELECT, INSERT ON people TO wds_grants;
            """);

        var grants = await Security.GrantsAsync(fixture.Driver, session, "wds_grants", Ct);

        Assert.Contains(grants, g => g.Object.EndsWith("people") && g.Privilege == "SELECT");
        Assert.Contains(grants, g => g.Object.EndsWith("people") && g.Privilege == "INSERT");
        Assert.DoesNotContain(grants, g => g.Privilege == "DELETE");
    }

    [Fact]
    public async Task An_account_nobody_granted_anything_has_an_empty_list_rather_than_an_error()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        Assert.Empty(await Security.GrantsAsync(fixture.Driver, session, "nobody_at_all", Ct));
    }

    /// The statements this panel writes have to be ones the server accepts — the round trip is the
    /// only way to know that.
    [Fact]
    public async Task Every_statement_the_panel_writes_runs()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);
        await RunAsync(session, "DROP ROLE IF EXISTS wds_round; DROP ROLE IF EXISTS wds_group;");

        foreach (var change in new[]
        {
            new SecurityChange("create", "wds_group", Role: true),
            new SecurityChange("create", "wds_round", "s3cret"),
            new SecurityChange("password", "wds_round", "other"),
            new SecurityChange("login", "wds_round", CanLogin: false),
            new SecurityChange("login", "wds_round", CanLogin: true),
            new SecurityChange("grant-role", "wds_group", Member: "wds_round"),
            new SecurityChange("grant", "wds_round", Privilege: "select", Target: "people"),
            new SecurityChange("revoke", "wds_round", Privilege: "select", Target: "people"),
            new SecurityChange("revoke-role", "wds_group", Member: "wds_round"),
            new SecurityChange("drop", "wds_round"),
            new SecurityChange("drop", "wds_group", Role: true),
        })
            await RunAsync(session, Security.Statement(fixture.Driver, change));

        var left = await Security.ListAsync(fixture.Driver, session, Ct);
        Assert.DoesNotContain(left, p => p.Name is "wds_round" or "wds_group");
    }
}
