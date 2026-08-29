using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Analysis;

/// What kind of thing a rule checks.
public enum QualityKind
{
    /// This column has a value in every row.
    NotNull,
    /// No value appears twice.
    Unique,
    /// Every value is between two numbers, written `0..100`.
    Range,
    /// Every value points at a row that exists, written `other_table.column`.
    Referential,
    /// The newest value is not older than this, written `24h`, `30m` or `7d`.
    Freshness,
    /// Anything else, as the condition a bad row satisfies: `total < 0 OR status = ''`.
    Expression,
}

/// Where the rules a deployment ships live, if anywhere.
///
/// Rules written in the studio are workspace state. Rules that belong to a deployment belong in the
/// repository with the seed scripts and the export templates: `WDS_QUALITY_FILE` points at a JSON
/// file (or a folder of them), and what it holds is read-only here — the studio runs those rules and
/// cannot change them, the same deal a mounted export template gets.
public sealed record QualityFileOptions(bool Configured, string Path)
{
    public static QualityFileOptions FromConfiguration(IConfiguration config)
    {
        var path = config["WDS_QUALITY_FILE"]?.Trim();
        return string.IsNullOrEmpty(path)
            ? new QualityFileOptions(false, "")
            : new QualityFileOptions(true, path);
    }
}

/// One rule somebody wrote about their data.
public sealed record QualityRule(
    string Id,
    string ConnectionId,
    string Schema,
    string Table,
    /// The column the rule is about. Empty for an expression that names its own.
    string Column,
    QualityKind Kind,
    /// The bound, the reference or the interval — whatever this kind needs.
    string? Argument,
    /// What to say when it fails. A default is written where nobody said.
    string? Message,
    bool Enabled = true,
    /// True for a rule the deployment ships: it runs, and the studio cannot change or delete it.
    bool FromFile = false);

/// What a rule found.
public sealed record QualityResult(
    QualityRule Rule,
    /// How many rows break it. Zero is a pass.
    long Violations,
    string Statement,
    DateTimeOffset RanAt,
    /// Why it could not be checked, where that is the answer.
    string? Error)
{
    public bool Passed => Error is null && Violations == 0;

    public string Describe() => Error is { } error
        ? $"{Rule.Table}.{Rule.Column}: {error}"
        : Violations == 0
            ? $"{Rule.Table}{(Rule.Column.Length > 0 ? "." + Rule.Column : "")}: ok"
            : Rule.Message is { Length: > 0 } message
                ? $"{message} ({Violations} rows)"
                : $"{Rule.Table}{(Rule.Column.Length > 0 ? "." + Rule.Column : "")}: "
                  + $"{Violations} rows break {Rule.Kind}";
}

/// Rules about the data, rather than about the schema.
///
/// The health report reads the catalogue: a table without a primary key, an index nobody uses. It
/// cannot say that half of yesterday's orders have no customer, because that is not in the
/// catalogue — it is in the rows. This is the other half, and it is deliberately small: each rule
/// counts the rows that break it, so a rule is one number and a number can be watched.
public static class QualityRules
{
    /// The SQL that counts what breaks the rule. Identifiers are quoted by the dialect and the
    /// arguments are parsed rather than pasted — except an expression, which is the person's own SQL
    /// and is treated the way a query tab treats what somebody typed.
    public static string CountSql(QualityRule rule, SqlDialect dialect)
    {
        var table = rule.Schema.Length == 0
            ? dialect.QuoteIdentifier(rule.Table)
            : $"{dialect.QuoteIdentifier(rule.Schema)}.{dialect.QuoteIdentifier(rule.Table)}";

        var column = rule.Column.Length == 0 ? "" : dialect.QuoteIdentifier(rule.Column);

        return rule.Kind switch
        {
            QualityKind.NotNull => $"SELECT count(*) FROM {table} WHERE {column} IS NULL",

            // A duplicate is a group with more than one row in it; the count is of the extra rows.
            QualityKind.Unique =>
                $"SELECT COALESCE(SUM(n - 1), 0) FROM (SELECT count(*) AS n FROM {table} "
                + $"WHERE {column} IS NOT NULL GROUP BY {column} HAVING count(*) > 1) d",

            QualityKind.Range => RangeSql(rule, table, column),

            QualityKind.Referential => ReferentialSql(rule, dialect, table, column),

            // One row or none: a table is either fresh or it is not.
            QualityKind.Freshness =>
                $"SELECT CASE WHEN max({column}) IS NULL OR max({column}) < {Cutoff(rule, dialect)} "
                + $"THEN 1 ELSE 0 END FROM {table}",

            QualityKind.Expression =>
                $"SELECT count(*) FROM {table} WHERE {Required(rule.Argument, "a condition")}",

            _ => throw new NotSupportedException($"no SQL for {rule.Kind}"),
        };
    }

    private static string RangeSql(QualityRule rule, string table, string column)
    {
        var (low, high) = ParseRange(rule.Argument);

        return $"SELECT count(*) FROM {table} WHERE {column} IS NOT NULL AND ({column} < {low} "
               + $"OR {column} > {high})";
    }

    private static string ReferentialSql(QualityRule rule, SqlDialect dialect, string table,
        string column)
    {
        var reference = Required(rule.Argument, "a reference, written other_table.column");
        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is < 2 or > 3)
            throw new FormatException("a reference is written other_table.column, or schema.table.column");

        var target = parts.Length == 3
            ? $"{dialect.QuoteIdentifier(parts[0])}.{dialect.QuoteIdentifier(parts[1])}"
            : dialect.QuoteIdentifier(parts[0]);

        var targetColumn = dialect.QuoteIdentifier(parts[^1]);

        // NULL is not a broken reference: "no customer yet" is a different rule (NotNull).
        return $"SELECT count(*) FROM {table} t WHERE t.{column} IS NOT NULL AND NOT EXISTS "
               + $"(SELECT 1 FROM {target} r WHERE r.{targetColumn} = t.{column})";
    }

    /// `24h`, `30m`, `7d` as a timestamp literal this engine compares against. Written as a literal
    /// rather than as `now() - interval` because every engine spells that differently.
    private static string Cutoff(QualityRule rule, SqlDialect dialect)
    {
        var cutoff = DateTimeOffset.UtcNow - ParseInterval(rule.Argument);
        var literal = dialect.QuoteLiteral(cutoff.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture));

        return string.Format(CultureInfo.InvariantCulture, dialect.TimestampCast, literal);
    }

    public static (decimal Low, decimal High) ParseRange(string? argument)
    {
        var text = Required(argument, "a range, written 0..100");
        var parts = text.Split("..", StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)
            || !decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var high))
            throw new FormatException("a range is written 0..100");

        return low <= high ? (low, high) : (high, low);
    }

    public static TimeSpan ParseInterval(string? argument)
    {
        var text = Required(argument, "an interval, written 24h, 30m or 7d").Trim().ToLowerInvariant();
        var unit = text[^1];
        var number = text[..^1];

        if (!double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
            throw new FormatException("an interval is written 24h, 30m or 7d");

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new FormatException("an interval ends in m, h or d"),
        };
    }

    private static string Required(string? argument, string what) =>
        argument is { Length: > 0 }
            ? argument
            : throw new FormatException($"this rule needs {what}");

    /// Better, worse or the same, in words. The first and the last measurement of a window: a mean
    /// over a month says nothing about the direction, which is the only thing anybody asks.
    public static string Describe(long first, long last) => last == first
        ? "unchanged"
        : last == 0
            ? "fixed"
            : first == 0
                ? "new"
                : last > first
                    ? $"worse by {last - first}"
                    : $"better by {first - last}";

    /// A rule as an analysis finding, so what watches the health report also watches the data.
    public static AnalyzeFinding AsFinding(QualityResult result) => new(
        "data-quality",
        result.Error is not null ? "info" : result.Violations > 0 ? "warning" : "info",
        $"Data quality: {result.Rule.Table}"
        + (result.Rule.Column.Length > 0 ? $".{result.Rule.Column}" : ""),
        result.Describe(),
        result.Statement);
}

/// Where the rules live, and running them.
///
/// The rules are workspace state like a layout or an export template: text the studio keeps. Running
/// them is one counting query each, so a hundred rules are a hundred cheap queries rather than a
/// framework.
public sealed class QualityRunner(
    WorkspaceStore workspace, SessionFactory factory, QualityFileOptions file,
    ConnectionRegistry connections, ILogger<QualityRunner> log)
{
    private const string Key = "quality-rules";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// Every rule: the ones written here, and the ones the deployment ships.
    public IReadOnlyList<QualityRule> All() => [.. Saved(), .. FromFile()];

    private List<QualityRule> Saved() =>
        workspace.LoadItem(Key) is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<QualityRule>>(json, Json) ?? []
            : [];

    /// What the deployment mounted, read every time rather than cached: a file that changed while
    /// the container ran should take effect, and reading a few kilobytes is cheaper than explaining
    /// why it did not.
    ///
    /// The file names connections the way a person does — by name — so each one is resolved against
    /// the registry. A rule for a connection this studio does not have is skipped with a line in the
    /// log rather than failing every other rule.
    private List<QualityRule> FromFile()
    {
        if (!file.Configured) return [];

        var rules = new List<QualityRule>();

        try
        {
            // A file counts as itself, a folder as its .json files — and the setting may name
            // several of either, so a repository's rules and an app host's own can live together.
            var paths = ConfiguredPaths.Files(file.Path, "*.json", SearchOption.TopDirectoryOnly).ToArray();

            foreach (var path in paths)
                foreach (var entry in JsonSerializer.Deserialize<List<QualityFileRule>>(
                             File.ReadAllText(path), Json) ?? [])
                {
                    if (entry.Table is null or { Length: 0 }) continue;

                    var spec = connections.All().FirstOrDefault(candidate =>
                        candidate.Id.Equals(entry.Connection, StringComparison.OrdinalIgnoreCase)
                        || candidate.Name.Equals(entry.Connection, StringComparison.OrdinalIgnoreCase));

                    if (spec is null)
                    {
                        log.LogWarning(
                            "the quality rule for {Table} names connection {Connection}, "
                            + "which this studio does not have", entry.Table, entry.Connection);
                        continue;
                    }

                    rules.Add(new QualityRule(
                        // Stable across restarts, so its history is one series rather than a new one
                        // every time the container comes up.
                        $"file:{spec.Id}:{entry.Table}:{entry.Column}:{entry.Kind}",
                        spec.Id,
                        entry.Schema ?? "",
                        entry.Table,
                        entry.Column ?? "",
                        entry.Kind,
                        entry.Argument,
                        entry.Message,
                        entry.Enabled ?? true,
                        FromFile: true));
                }
        }
        catch (Exception e)
        {
            // A broken file must not take the rules somebody wrote in the studio with it.
            log.LogWarning(e, "could not read the quality rules from {Path}", file.Path);
        }

        return rules;
    }

    /// What the mounted file holds. The connection is named rather than identified, because a
    /// deployment writes names and the studio makes the ids.
    private sealed record QualityFileRule(
        string Connection, string? Schema, string Table, string? Column, QualityKind Kind,
        string? Argument, string? Message, bool? Enabled);

    public IReadOnlyList<QualityRule> For(string connectionId) =>
        All().Where(rule => rule.ConnectionId == connectionId).ToList();

    public void Save(QualityRule rule)
    {
        if (rule.FromFile)
            throw new InvalidOperationException(
                "this rule comes from the deployment's own file and cannot be changed here");

        var rules = Saved().ToList();
        rules.RemoveAll(existing => existing.Id == rule.Id);
        rules.Add(rule);

        workspace.SaveItem(Key, JsonSerializer.Serialize(rules, Json));
    }

    public void Delete(string id)
    {
        if (id.StartsWith("file:", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "this rule comes from the deployment's own file and cannot be deleted here");

        var rules = Saved().Where(rule => rule.Id != id).ToList();
        workspace.SaveItem(Key, JsonSerializer.Serialize(rules, Json));
    }

    /// Runs every enabled rule for one connection. A rule that cannot be checked reports why rather
    /// than stopping the others.
    public async Task<IReadOnlyList<QualityResult>> RunAsync(string connectionId,
        CancellationToken ct)
    {
        var rules = For(connectionId).Where(rule => rule.Enabled).ToList();
        if (rules.Count == 0) return [];

        var results = new List<QualityResult>();
        var (driver, session) = await factory.OpenAsync(connectionId, ct);

        await using (session)
            foreach (var rule in rules)
            {
                var statement = "";

                try
                {
                    statement = QualityRules.CountSql(rule, driver.Dialect);

                    await using var command = session.Connection.CreateCommand();
                    command.CommandText = statement;

                    var value = await command.ExecuteScalarAsync(ct);
                    var violations = value is null or DBNull ? 0 : Convert.ToInt64(value);

                    results.Add(new QualityResult(rule, violations, statement,
                        DateTimeOffset.UtcNow, null));
                }
                catch (Exception e) when (e is DbException or FormatException or NotSupportedException)
                {
                    results.Add(new QualityResult(rule, 0, statement, DateTimeOffset.UtcNow,
                        e.Message));
                }
            }

        // Every run is a measurement: one number per rule per run, so "the violations are going up"
        // is a question the history can answer rather than a feeling.
        if (workspace.Available && results.Count > 0)
            try
            {
                workspace.AddQualityRuns(connectionId, results.Select(result =>
                    (result.Rule.Id, result.Violations, result.Error)));
            }
            catch (Exception e)
            {
                log.LogWarning(e, "could not record the quality run");
            }

        // What is broken first: a list that opens on the passes is a list nobody reads twice.
        return results
            .OrderByDescending(result => result.Violations)
            .ThenBy(result => result.Rule.Table, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
