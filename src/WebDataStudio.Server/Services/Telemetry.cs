using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WebDataStudio.Server.Services;

/// Whether the studio reports what it is doing, and to where. Driven by the standard OTLP
/// variables, so a stack that already exports traces needs no studio-specific configuration.
public sealed record TelemetryOptions(bool Configured, string Endpoint, string ServiceName)
{
    public static TelemetryOptions FromConfiguration(IConfiguration config)
    {
        var endpoint = config["OTEL_EXPORTER_OTLP_ENDPOINT"]?.Trim();
        var service = config["OTEL_SERVICE_NAME"]?.Trim();

        return string.IsNullOrEmpty(endpoint)
            ? new TelemetryOptions(false, "", "webdatastudio")
            : new TelemetryOptions(true, endpoint,
                string.IsNullOrEmpty(service) ? "webdatastudio" : service);
    }
}

/// The studio's own spans and counters: what was run, how long it took, how much came back.
///
/// A studio in a stack with a collector should be visible in it — a slow query in the studio and a
/// slow query in the app are the same kind of problem, and the second is already instrumented.
public static class Telemetry
{
    public const string SourceName = "WebDataStudio";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    /// Statements run, by engine and by whether they succeeded.
    private static readonly Counter<long> Queries =
        Meter.CreateCounter<long>("wds.queries", "queries", "Statements run through the studio.");

    /// How long a run took, end to end, as the studio saw it.
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("wds.query.duration", "ms", "How long a run took.");

    /// Rows handed to a client. Says what the studio is actually moving, which the query count
    /// alone does not.
    private static readonly Counter<long> Rows =
        Meter.CreateCounter<long>("wds.rows", "rows", "Rows returned to a client.");

    /// Tool calls from an agent or from the studio's own assistant, by tool and outcome.
    private static readonly Counter<long> Tools =
        Meter.CreateCounter<long>("wds.tool.calls", "calls", "MCP tool calls.");

    /// One run. `engine` rather than the statement: a statement is data, and this is a metric.
    public static void Query(string engine, bool failed, double elapsedMs, long rows)
    {
        var tags = new TagList { { "engine", engine }, { "failed", failed } };

        Queries.Add(1, tags);
        Duration.Record(elapsedMs, tags);

        if (rows > 0) Rows.Add(rows, new TagList { { "engine", engine } });
    }

    public static void ToolCall(string tool, bool failed) =>
        Tools.Add(1, new TagList { { "tool", tool }, { "failed", failed } });

    /// A span around a piece of work, or nothing at all when nobody is listening.
    public static Activity? Span(string name) => Source.StartActivity(name);
}
