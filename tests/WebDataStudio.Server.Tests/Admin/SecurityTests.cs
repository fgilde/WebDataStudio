using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Admin;

/// Accounts and roles, as each engine spells them. Nothing here runs anything: the statement is what
/// the panel shows before anybody agrees to it.
public class SecurityStatementTests
{
    private static IDbDriver Driver(string engine) => new DriverRegistry().Get(engine);

    private static string Statement(string engine, SecurityChange change) =>
        Security.Statement(Driver(engine), change);

    // --- creating -------------------------------------------------------------------------------

    [Fact]
    public void An_account_is_created_the_way_the_engine_names_one()
    {
        Assert.Equal("CREATE ROLE \"ada\" LOGIN PASSWORD 's3cret';",
            Statement("postgresql", new SecurityChange("create", "ada", "s3cret")));

        Assert.Equal("CREATE USER 'ada'@'%' IDENTIFIED BY 's3cret';",
            Statement("mysql", new SecurityChange("create", "ada", "s3cret")));

        Assert.Equal("CREATE LOGIN [ada] WITH PASSWORD = 's3cret';",
            Statement("sqlserver", new SecurityChange("create", "ada", "s3cret")));
    }

    /// A role is a bag of rights nobody signs in as. PostgreSQL says that with NOLOGIN; the others
    /// have a keyword of their own.
    [Fact]
    public void A_role_is_created_as_something_that_cannot_sign_in()
    {
        Assert.Equal("CREATE ROLE \"reporting\" NOLOGIN;",
            Statement("postgresql", new SecurityChange("create", "reporting", Role: true)));

        Assert.Equal("CREATE ROLE 'reporting'@'%';",
            Statement("mysql", new SecurityChange("create", "reporting", Role: true)));

        Assert.Equal("CREATE ROLE [reporting];",
            Statement("sqlserver", new SecurityChange("create", "reporting", Role: true)));
    }

    [Fact]
    public void A_mysql_account_keeps_the_host_it_was_named_with()
    {
        Assert.Equal("CREATE USER 'ada'@'10.0.0.5' IDENTIFIED BY 'p';",
            Statement("mysql", new SecurityChange("create", "ada@10.0.0.5", "p")));
    }

    // --- changing -------------------------------------------------------------------------------

    [Fact]
    public void A_password_is_changed_rather_than_the_account_recreated()
    {
        Assert.Equal("ALTER ROLE \"ada\" PASSWORD 'new';",
            Statement("postgresql", new SecurityChange("password", "ada", "new")));

        Assert.Equal("ALTER USER 'ada'@'%' IDENTIFIED BY 'new';",
            Statement("mysql", new SecurityChange("password", "ada", "new")));

        Assert.Equal("ALTER LOGIN [ada] WITH PASSWORD = 'new';",
            Statement("sqlserver", new SecurityChange("password", "ada", "new")));
    }

    /// The cheapest way to stop an account without losing what it may do — and the way back.
    [Fact]
    public void Signing_in_can_be_switched_off_and_on_again()
    {
        Assert.Equal("ALTER ROLE \"ada\" NOLOGIN;",
            Statement("postgresql", new SecurityChange("login", "ada", CanLogin: false)));

        Assert.Equal("ALTER ROLE \"ada\" LOGIN;",
            Statement("postgresql", new SecurityChange("login", "ada", CanLogin: true)));

        Assert.Equal("ALTER USER 'ada'@'%' ACCOUNT LOCK;",
            Statement("mysql", new SecurityChange("login", "ada", CanLogin: false)));

        Assert.Equal("ALTER LOGIN [ada] DISABLE;",
            Statement("sqlserver", new SecurityChange("login", "ada", CanLogin: false)));
    }

    [Fact]
    public void An_account_is_dropped_by_what_it_is()
    {
        Assert.Equal("DROP ROLE \"ada\";", Statement("postgresql", new SecurityChange("drop", "ada")));
        Assert.Equal("DROP USER 'ada'@'%';", Statement("mysql", new SecurityChange("drop", "ada")));
        Assert.Equal("DROP LOGIN [ada];", Statement("sqlserver", new SecurityChange("drop", "ada")));
        Assert.Equal("DROP ROLE [reporting];",
            Statement("sqlserver", new SecurityChange("drop", "reporting", Role: true)));
    }

    // --- membership -----------------------------------------------------------------------------

    [Fact]
    public void Putting_somebody_in_a_role_is_a_grant_except_where_it_is_an_alter()
    {
        Assert.Equal("GRANT \"reporting\" TO \"ada\";",
            Statement("postgresql", new SecurityChange("grant-role", "reporting", Member: "ada")));

        Assert.Equal("REVOKE \"reporting\" FROM \"ada\";",
            Statement("postgresql", new SecurityChange("revoke-role", "reporting", Member: "ada")));

        Assert.Equal("ALTER ROLE [reporting] ADD MEMBER [ada];",
            Statement("sqlserver", new SecurityChange("grant-role", "reporting", Member: "ada")));

        Assert.Equal("ALTER ROLE [reporting] DROP MEMBER [ada];",
            Statement("sqlserver", new SecurityChange("revoke-role", "reporting", Member: "ada")));
    }

    [Fact]
    public void A_membership_without_a_member_says_so()
    {
        Assert.Throws<NotSupportedException>(() =>
            Statement("postgresql", new SecurityChange("grant-role", "reporting")));
    }

    // --- privileges -----------------------------------------------------------------------------

    [Fact]
    public void A_privilege_is_granted_and_taken_back()
    {
        Assert.Equal("GRANT SELECT ON orders TO \"ada\";",
            Statement("postgresql", new SecurityChange("grant", "ada", Privilege: "select", Target: "orders")));

        Assert.Equal("REVOKE SELECT, INSERT ON ALL TABLES IN SCHEMA public FROM \"ada\";",
            Statement("postgresql", new SecurityChange("revoke", "ada",
                Privilege: "select, insert", Target: "ALL TABLES IN SCHEMA public")));
    }

    /// A privilege and an object cannot be parameters — they are identifiers. So they are checked
    /// instead, and anything that could end the statement is refused rather than quoted away.
    [Theory]
    [InlineData("SELECT; DROP TABLE orders", "orders")]
    [InlineData("SELECT", "orders; DROP TABLE people")]
    [InlineData("SELECT", "orders -- ")]
    [InlineData("", "orders")]
    [InlineData("SELECT", "")]
    public void A_privilege_or_object_that_is_not_one_is_refused(string privilege, string target)
    {
        Assert.Throws<NotSupportedException>(() =>
            Statement("postgresql", new SecurityChange("grant", "ada", Privilege: privilege, Target: target)));
    }

    [Fact]
    public void A_password_with_a_quote_in_it_stays_one_value()
    {
        var sql = Statement("postgresql", new SecurityChange("create", "ada", "it's fine"));

        Assert.Contains("'it''s fine'", sql);
    }

    [Fact]
    public void An_action_nobody_wrote_a_statement_for_is_refused()
    {
        Assert.Throws<NotSupportedException>(() =>
            Statement("postgresql", new SecurityChange("become-superuser", "ada")));
    }

    [Fact]
    public void An_engine_without_accounts_says_which_one_it_is()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            Statement("sqlite", new SecurityChange("create", "ada", "p")));

        Assert.Contains("SQLite", error.Message);
    }
}
