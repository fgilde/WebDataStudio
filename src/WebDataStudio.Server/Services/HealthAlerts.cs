using System.Net.Http.Json;
using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// Where the studio sends what it finds, if anywhere. Off without a webhook: a studio that posts
/// somewhere without being asked would be the wrong kind of surprise.
public sealed record AlertOptions(
    bool Configured, string Webhook, TimeSpan Interval, string MinSeverity,
    IReadOnlySet<string> Connections)
{
    /// The order the analyzers use, so "warning and worse" is a comparison rather than a list.
    private static readonly string[] Severities = ["info", "warning", "critical"];

    public static AlertOptions FromConfiguration(IConfiguration config)
    {
        var webhook = config["WDS_ALERT_WEBHOOK"]?.Trim();
        if (string.IsNullOrEmpty(webhook))
            return new AlertOptions(false, "", TimeSpan.FromHours(1), "warning",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var minutes = int.TryParse(config["WDS_ALERT_INTERVAL_MINUTES"], out var value) && value > 0
            ? value
            : 60;

        var severity = config["WDS_ALERT_MIN_SEVERITY"]?.Trim().ToLowerInvariant();
        if (!Severities.Contains(severity)) severity = "warning";

        var connections = (config["WDS_ALERT_CONNECTIONS"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new AlertOptions(true, webhook, TimeSpan.FromMinutes(minutes), severity,
            new HashSet<string>(connections, StringComparer.OrdinalIgnoreCase));
    }

    public bool Covers(ConnectionSpec spec) =>
        Connections.Count == 0 || Connections.Contains(spec.Id) || Connections.Contains(spec.Name);

    public bool Reportable(string severity) =>
        Array.IndexOf(Severities, severity.ToLowerInvariant()) >= Array.IndexOf(Severities, MinSeverity);
}

/// Runs the studio's own analysis on a timer and posts what is new to a webhook.
///
/// The health report already exists; the only thing missing was somebody looking at it. Only new
/// findings are sent — an alert that repeats every hour is one people filter into a folder they
/// never open.
public sealed class HealthAlerts(
    AlertOptions options, ConnectionRegistry registry, SessionFactory factory,
    IHttpClientFactory clients, ILogger<HealthAlerts> log) : BackgroundService
{
    /// Findings already reported, as "connection|category|title". Kept in memory: after a restart
    /// one repeat is better than a store to keep in sync.
    private readonly HashSet<string> _seen = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Configured) return;

        // A little after start, so the first sweep does not compete with the first page load.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A failed sweep is not a reason to stop watching.
                log.LogWarning(e, "the health sweep failed");
            }

            await Task.Delay(options.Interval, stoppingToken);
        }
    }

    /// One pass over every covered connection. Public so a test does not have to wait for a timer.
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var sent = 0;

        foreach (var spec in registry.All().Where(options.Covers))
        {
            var fresh = new List<AnalyzeFinding>();

            try
            {
                var (driver, session) = await factory.OpenAsync(spec.Id, ct);
                await using (session)
                {
                    var report = await driver.AnalyzeAsync(session, AnalyzeScope.Connection, null, ct);

                    foreach (var finding in report.Findings.Where(f => options.Reportable(f.Severity)))
                        if (_seen.Add($"{spec.Id}|{finding.Category}|{finding.Title}"))
                            fresh.Add(finding);
                }
            }
            catch (Exception e)
            {
                // A connection that cannot be reached is worth one log line, not an alert: the
                // thing that watches uptime already knows.
                log.LogDebug(e, "could not analyse {Connection}", spec.Name);
                continue;
            }

            if (fresh.Count == 0) continue;
            if (await PostAsync(spec, fresh, ct)) sent += fresh.Count;
        }

        return sent;
    }

    private async Task<bool> PostAsync(
        ConnectionSpec spec, IReadOnlyList<AnalyzeFinding> findings, CancellationToken ct)
    {
        // `text` is what Slack, Mattermost, Discord and Teams all read; the structured fields ride
        // along for anything that wants more than a sentence.
        var text = new StringBuilder($"*{spec.Name}* — {findings.Count} new health finding");
        if (findings.Count != 1) text.Append('s');

        foreach (var finding in findings.Take(10))
            text.Append($"\n• [{finding.Severity}] {finding.Title}");

        if (findings.Count > 10) text.Append($"\n• …and {findings.Count - 10} more");

        var payload = new
        {
            text = text.ToString(),
            studio = "webdatastudio",
            connection = new { spec.Id, spec.Name, spec.Engine },
            findings = findings.Select(finding => new
            {
                finding.Category,
                finding.Severity,
                finding.Title,
                finding.Detail,
                fix = finding.Statement,
            }),
        };

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

        // Not sent: forget them, so the next sweep tries again rather than swallowing them.
        foreach (var finding in findings) _seen.Remove($"{spec.Id}|{finding.Category}|{finding.Title}");

        return false;
    }
}
