using System.Net.Http.Json;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// The one place that talks to the alert webhook. Health findings and schema drift are different
/// things to say; they are not two reasons to have two HTTP paths.
///
/// Every payload carries a `text` field — what Slack, Mattermost, Discord and Teams render — plus
/// the structured detail for anything that wants more than a sentence.
public sealed class HealthAlertSink(
    AlertOptions options, IHttpClientFactory clients, ILogger<HealthAlertSink> log)
{
    public bool Configured => options.Configured;

    public bool Covers(ConnectionSpec spec) => options.Covers(spec);

    /// True when the webhook took it. False means nobody heard it, so the caller can try again
    /// rather than assume it was said.
    public async Task<bool> PostAsync(object payload, CancellationToken ct)
    {
        if (!options.Configured) return false;

        try
        {
            var client = clients.CreateClient("alerts");
            using var response = await client.PostAsJsonAsync(options.Webhook, payload, ct);

            if (response.IsSuccessStatusCode) return true;

            log.LogWarning("the alert webhook answered {Status}", (int)response.StatusCode);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the alert webhook could not be reached");
        }

        return false;
    }

    /// A schema that moved: the kind of thing somebody wants to hear about the same day, not at the
    /// next deploy.
    public Task<bool> SchemaDriftAsync(ConnectionSpec spec, SchemaDrift drift, CancellationToken ct)
    {
        if (!options.Configured || !options.Covers(spec) || !drift.Any) return Task.FromResult(false);

        var lines = drift.Added.Select(name => $"\n• added: {name}")
            .Concat(drift.Removed.Select(name => $"\n• removed: {name}"))
            .Concat(drift.Changed.Select(change => $"\n• {change}"))
            .Take(10);

        return PostAsync(new
        {
            text = $"*{spec.Name}* — the schema moved ({drift.Summary}){string.Concat(lines)}",
            studio = "webdatastudio",
            kind = "schema-drift",
            connection = new { spec.Id, spec.Name, spec.Engine },
            drift = new { drift.Before, drift.After, drift.Added, drift.Removed, drift.Changed },
        }, ct);
    }
}
