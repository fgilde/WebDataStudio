using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// What the deployment brings with it, for the parts of the studio that live in the browser.
///
/// The snippets a team shares and the preferences a studio should start with are workspace state:
/// one person changes them and they follow that person. These are the other half — what a stack
/// ships, read from a file and the same for everybody who opens it. The studio shows them and
/// cannot change them; a person's own snippet with the same prefix wins for that person.
public static class DeploymentEndpoints
{
    /// One snippet a deployment ships. The same shape the editor already uses.
    public sealed record ShippedSnippet(string Prefix, string Label, string Body, string? Description);

    /// The preferences a studio starts with, before anybody has changed one. Everything is optional:
    /// what a file does not say keeps the studio's own default.
    public sealed record ShippedPreferences(
        int? PageSize, bool? HistorySnapshots, int? SnapshotRows, bool? InspectBeforeRun,
        int? NotifyAfterSeconds, string? TimeZone);

    public static void MapDeploymentEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/deployment");

        // Read on every call rather than cached: a file is small, and a studio that has to be
        // restarted to pick up a snippet is a studio nobody edits the file for.
        api.MapGet("/snippets", (IConfiguration config, ILoggerFactory logs) => Results.Ok(
            ShippedFiles.Read<ShippedSnippet>(config["WDS_SNIPPETS_FILE"],
                    logs.CreateLogger("WebDataStudio.Snippets"), "snippet file")
                .Where(snippet => !string.IsNullOrWhiteSpace(snippet.Prefix)
                                  && !string.IsNullOrWhiteSpace(snippet.Body))
                .Select(snippet => new
                {
                    snippet.Prefix,
                    label = string.IsNullOrWhiteSpace(snippet.Label) ? snippet.Prefix : snippet.Label,
                    snippet.Body,
                    description = snippet.Description ?? "from the deployment",
                })));

        api.MapGet("/preferences", (IConfiguration config, ILoggerFactory logs) =>
        {
            var shipped = ShippedFiles.ReadOne<ShippedPreferences>(config["WDS_PREFERENCES_FILE"],
                logs.CreateLogger("WebDataStudio.Preferences"), "preferences file");

            return Results.Ok(new
            {
                configured = shipped is not null,
                preferences = shipped,
            });
        });
    }
}
