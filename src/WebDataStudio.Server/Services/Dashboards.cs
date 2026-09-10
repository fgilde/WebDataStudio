using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WebDataStudio.Server.Services;

/// What a widget draws. The list is the spec's, and the order is the palette's order in the editor:
/// the headline forms first, then the shapes, then the ones that carry rows.
[JsonConverter(typeof(JsonStringEnumConverter<WidgetType>))]
public enum WidgetType
{
    Stat, Gauge, Bar, StackedBar, Pie, Treemap, Line, Area, StackedArea, Sparkline,
    Table, List, Heatmap, GeoMap, Sankey, Text, Row,
}

/// Where a widget's rows come from. None of the three is new machinery: `Sql` and `Path` are the
/// query endpoint a tab already uses, `Federated` is the studio's own federation.
[JsonConverter(typeof(JsonStringEnumConverter<SourceKind>))]
public enum SourceKind { Sql, Path, Federated }

[JsonConverter(typeof(JsonStringEnumConverter<VariableKind>))]
public enum VariableKind { Query, Custom, Constant, Interval }

/// A place on the canvas, in Grafana's own units: 24 columns, `H` in rows of `Layout.RowHeight`.
/// Sharing the units is what makes the import a copy rather than a conversion.
public sealed record WidgetPosition(int X, int Y, int W, int H);

public sealed record FederatedSource(string ConnectionId, string Sql, string Alias);

public sealed record WidgetSource(
    SourceKind Kind = SourceKind.Sql,
    string? ConnectionId = null,
    string? Sql = null,
    IReadOnlyList<FederatedSource>? Sources = null,
    int? MaxRowsPerSource = null);

/// Which column plays which role. Kept apart from the options on purpose: the reference
/// implementations put both in one bag, and that is what makes fifteen widget types unmaintainable.
public sealed record WidgetMapping(
    string? Time = null,
    string? Category = null,
    string? Series = null,
    IReadOnlyList<string>? Values = null,
    string? Latitude = null,
    string? Longitude = null,
    string? From = null,
    string? To = null,
    string? Weight = null);

/// One threshold and the state it means. The levels are the reserved status colours and nothing
/// else: a threshold ships with its number, never with a colour a dashboard picked.
public sealed record WidgetThreshold(double Value, string Level);

public sealed record WidgetOptions(
    string? Unit = null,
    int? Decimals = null,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<WidgetThreshold>? Thresholds = null,
    bool Legend = true,
    bool Stacked = false,
    /// `horizontal` for a bar chart with long labels, which is a rule rather than a preference.
    string? Orientation = null,
    string? ColorMode = null,
    string? Ramp = null,
    string? Markdown = null,
    IReadOnlyList<string>? Columns = null,
    int? Limit = null,
    /// The panel as Grafana wrote it, kept so an export puts back what came in. Never read by
    /// anything that draws.
    JsonNode? Grafana = null);

public sealed record Widget(
    string Id,
    WidgetType Type,
    string Title,
    WidgetPosition Position,
    WidgetSource Source,
    WidgetMapping Mapping,
    WidgetOptions Options,
    string? Description = null);

public sealed record DashboardLayout(int Columns = 24, int RowHeight = 40);

/// One range per dashboard, relative (`now-24h`) or absolute (ISO). `Column` is the default column
/// `$__timeFilter()` filters on when a widget does not name one.
public sealed record DashboardTimeRange(string From = "now-24h", string To = "now", string? Column = null);

public sealed record DashboardVariable(
    string Name,
    VariableKind Kind = VariableKind.Custom,
    string? Label = null,
    string? ConnectionId = null,
    string? Sql = null,
    IReadOnlyList<string>? Values = null,
    bool Multi = false,
    bool IncludeAll = false,
    string? Default = null);

/// A page of widgets, a time range and the variables the statements read.
public sealed record Dashboard(
    string Id,
    string Name,
    IReadOnlyList<Widget> Widgets,
    /// How often the widgets run themselves. 0 means only when somebody asks.
    int RefreshSeconds,
    DateTimeOffset UpdatedAt,
    DashboardLayout? Layout = null,
    DashboardTimeRange? TimeRange = null,
    IReadOnlyList<DashboardVariable>? Variables = null,
    IReadOnlyList<string>? Tags = null,
    /// True for one the deployment ships: the studio shows it and cannot change or delete it, the
    /// same deal a mounted quality rule gets.
    bool FromFile = false);

/// One box on a dashboard as the studio wrote them before there was a canvas. Still read, still
/// written by app hosts, and still what a `WDS_DASHBOARD_FILE` in a repository looks like.
public sealed record DashboardTile(string Title, string ConnectionId, string Sql, string View, int Width);

/// The rules a dashboard is held to, in one place.
///
/// Everything here is arithmetic on what a browser sent: a widget outside the grid comes back
/// inside it, a refresh of one second becomes ten, and a page of four hundred widgets becomes
/// sixty. None of it refuses — a dashboard is a document people paste to each other, and the answer
/// to a strange one is to make it sane rather than to lose it.
public static class Dashboards
{
    public const int Columns = 24;
    public const int MaxWidgets = 60;
    public const int MaxVariables = 20;

    /// The views the old tiles had, and the type each becomes.
    private static readonly Dictionary<string, WidgetType> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["number"] = WidgetType.Stat,
        ["table"] = WidgetType.Table,
        ["chart"] = WidgetType.Bar,
    };

    /// The way back, for the tile list a dashboard still writes for an older image.
    private static string ViewOf(WidgetType type) => type switch
    {
        WidgetType.Stat or WidgetType.Gauge or WidgetType.Sparkline => "number",
        WidgetType.Table or WidgetType.List or WidgetType.Text or WidgetType.Row => "table",
        _ => "chart",
    };

    public static Dashboard Normalise(Dashboard dashboard)
    {
        var layout = dashboard.Layout ?? new DashboardLayout();

        var widgets = (dashboard.Widgets ?? [])
            .Where(widget => widget is not null)
            .Take(MaxWidgets)
            .Select(Normalise)
            .ToList();

        var variables = (dashboard.Variables ?? [])
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
            .Take(MaxVariables)
            .Select(variable => variable with
            {
                Name = Slug(variable.Name),
                Values = variable.Values?.Where(v => v is not null).ToList() ?? [],
            })
            .ToList();

        return dashboard with
        {
            Name = dashboard.Name?.Trim() is { Length: > 0 } name ? name : "untitled",
            Widgets = widgets,
            Variables = variables,
            Tags = dashboard.Tags?.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct().ToList() ?? [],
            // A refresh under ten seconds is a load test, not a dashboard.
            RefreshSeconds = dashboard.RefreshSeconds > 0 ? Math.Clamp(dashboard.RefreshSeconds, 10, 3600) : 0,
            Layout = new DashboardLayout(Columns, Math.Clamp(layout.RowHeight, 20, 200)),
            TimeRange = Normalise(dashboard.TimeRange ?? new DashboardTimeRange()),
        };
    }

    private static DashboardTimeRange Normalise(DashboardTimeRange range) => new(
        string.IsNullOrWhiteSpace(range.From) ? "now-24h" : range.From.Trim(),
        string.IsNullOrWhiteSpace(range.To) ? "now" : range.To.Trim(),
        string.IsNullOrWhiteSpace(range.Column) ? null : range.Column.Trim());

    private static Widget Normalise(Widget widget)
    {
        // Positions arrive from a canvas in the browser, so they are clamped rather than trusted:
        // a widget at column 40 would be a widget nobody can see.
        var w = Math.Clamp(widget.Position?.W ?? 8, 1, Columns);
        var x = Math.Clamp(widget.Position?.X ?? 0, 0, Columns - w);
        var h = Math.Clamp(widget.Position?.H ?? 6, widget.Type == WidgetType.Row ? 1 : 2, 60);
        var y = Math.Max(0, widget.Position?.Y ?? 0);

        var source = widget.Source ?? new WidgetSource();

        return widget with
        {
            Id = string.IsNullOrWhiteSpace(widget.Id) ? Guid.NewGuid().ToString("n")[..12] : widget.Id.Trim(),
            Title = widget.Title?.Trim() is { Length: > 0 } title ? title : "untitled",
            Position = new WidgetPosition(x, y, w, h),
            Source = source with
            {
                ConnectionId = source.ConnectionId?.Trim(),
                Sql = source.Sql?.Trim(),
                Sources = (source.Sources ?? [])
                    .Where(one => !string.IsNullOrWhiteSpace(one.Sql) && !string.IsNullOrWhiteSpace(one.Alias))
                    .Take(8)
                    .Select(one => one with { Alias = Slug(one.Alias) })
                    .ToList(),
            },
            Mapping = (widget.Mapping ?? new WidgetMapping()) with
            {
                Values = widget.Mapping?.Values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? [],
            },
            Options = (widget.Options ?? new WidgetOptions()) with
            {
                Thresholds = widget.Options?.Thresholds?
                    .Where(one => Levels.Contains(one.Level))
                    .OrderBy(one => one.Value)
                    .ToList() ?? [],
                Columns = widget.Options?.Columns?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? [],
                Decimals = widget.Options?.Decimals is { } decimals ? Math.Clamp(decimals, 0, 8) : null,
                Limit = widget.Options?.Limit is { } limit ? Math.Clamp(limit, 1, 10_000) : null,
            },
        };
    }

    /// The four states a threshold may mean. Reserved: none of them is ever "series four".
    private static readonly HashSet<string> Levels =
        new(["good", "warning", "serious", "critical"], StringComparer.OrdinalIgnoreCase);

    /// A name a statement can carry — a variable's name and a federated alias are both identifiers.
    private static string Slug(string value) =>
        new string(value.Trim().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    /// The old shape, laid out: four to a row in the order they were written, each tile's width
    /// counted in quarters of the grid.
    public static IReadOnlyList<Widget> FromTiles(IEnumerable<DashboardTile> tiles)
    {
        var widgets = new List<Widget>();
        var (x, y) = (0, 0);

        foreach (var tile in tiles.Where(one => !string.IsNullOrWhiteSpace(one?.Sql)))
        {
            var w = Math.Clamp(tile.Width, 1, 4) * (Columns / 4);
            if (x + w > Columns) (x, y) = (0, y + 6);

            widgets.Add(Normalise(new Widget(
                Id: "",
                Type: Views.GetValueOrDefault(tile.View ?? "", WidgetType.Table),
                Title: tile.Title,
                Position: new WidgetPosition(x, y, w, 6),
                Source: new WidgetSource(SourceKind.Sql, tile.ConnectionId, tile.Sql),
                Mapping: new WidgetMapping(),
                Options: new WidgetOptions())));

            x += w;
        }

        return widgets;
    }

    /// The same dashboard as tiles, for the column an older image reads. Lossy on purpose: it is a
    /// downgrade path, not a second model.
    public static IReadOnlyList<DashboardTile> ToTiles(Dashboard dashboard) =>
        (dashboard.Widgets ?? [])
            .Where(widget => widget.Type != WidgetType.Row)
            .Select(widget => new DashboardTile(
                widget.Title, widget.Source?.ConnectionId ?? "", widget.Source?.Sql ?? "",
                ViewOf(widget.Type),
                Math.Clamp((int)Math.Round(widget.Position.W / (double)(Columns / 4)), 1, 4)))
            .ToList();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
