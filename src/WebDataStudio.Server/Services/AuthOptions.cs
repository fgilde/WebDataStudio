namespace WebDataStudio.Server.Services;

/// Single-account authentication read from the environment. When either variable is missing
/// the app runs anonymously and never shows a login screen.
public sealed record AuthOptions(bool Anonymous, string? Username, string? Password)
{
    public static AuthOptions FromEnvironment(IDictionary<string, string?> env)
    {
        env.TryGetValue("WDS_USER", out var user);
        env.TryGetValue("WDS_PASSWORD", out var password);

        return string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password)
            ? new AuthOptions(true, null, null)
            : new AuthOptions(false, user, password);
    }

    // Tests inject credentials through ConfigureAppConfiguration; at runtime the environment
    // variable provider is already part of IConfiguration.
    public static AuthOptions FromConfiguration(IConfiguration config) =>
        FromEnvironment(new Dictionary<string, string?>
        {
            ["WDS_USER"] = config["WDS_USER"],
            ["WDS_PASSWORD"] = config["WDS_PASSWORD"],
        });
}
