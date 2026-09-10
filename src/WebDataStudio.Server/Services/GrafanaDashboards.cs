using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebDataStudio.Server.Services;

/// What came of a read: the dashboard, and a sentence per thing that could not come along.
public sealed record DashboardImport(Dashboard? Dashboard, IReadOnlyList<string> Notes);

/// Grafana dashboards, in and out.
///
/// Neither direction claims to be lossless, and both say what they did. What cannot come along is a
/// sentence in the report rather than a silently empty widget: a `expr` against Prometheus, a panel
/// type nothing here draws, transformations, alert rules, library panels. The panel as Grafana wrote
/// it is kept on the widget, so an export puts back what came in.
public static class GrafanaDashboards
{
    /// The schema version this writes. Pinned: it is what tells Grafana how to read the rest.
    public const int SchemaVersion = 39;

    private static readonly Dictionary<string, WidgetType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stat"] = WidgetType.Stat,
        ["singlestat"] = WidgetType.Stat,
        ["gauge"] = WidgetType.Gauge,
        ["bargauge"] = WidgetType.Bar,
        ["barchart"] = WidgetType.Bar,
        ["timeseries"] = WidgetType.Line,
        ["graph"] = WidgetType.Line,
        ["piechart"] = WidgetType.Pie,
        ["table"] = WidgetType.Table,
        ["table-old"] = WidgetType.Table,
        ["text"] = WidgetType.Text,
        ["geomap"] = WidgetType.GeoMap,
        ["heatmap"] = WidgetType.Heatmap,
        ["row"] = WidgetType.Row,
    };

    private static readonly Dictionary<WidgetType, string> Back = new()
    {
        [WidgetType.Stat] = "stat",
        [WidgetType.Gauge] = "gauge",
        [WidgetType.Bar] = "barchart",
        [WidgetType.StackedBar] = "barchart",
        [WidgetType.Pie] = "piechart",
        [WidgetType.Treemap] = "piechart",
        [WidgetType.Line] = "timeseries",
        [WidgetType.Area] = "timeseries",
        [WidgetType.StackedArea] = "timeseries",
        [WidgetType.Sparkline] = "timeseries",
        [WidgetType.Table] = "table",
        [WidgetType.List] = "table",
        [WidgetType.Sankey] = "table",
        [WidgetType.Heatmap] = "heatmap",
        [WidgetType.GeoMap] = "geomap",
        [WidgetType.Text] = "text",
        [WidgetType.Row] = "row",
    };

    /// Their threshold colours, as the four states a threshold may mean here. A colour outside this
    /// list is a colour somebody picked, and picked colours do not exist here.
    private static readonly Dictionary<string, string> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["green"] = "good",
        ["dark-green"] = "good",
        ["semi-dark-green"] = "good",
        ["yellow"] = "warning",
        ["dark-yellow"] = "warning",
        ["semi-dark-yellow"] = "warning",
        ["orange"] = "serious",
        ["dark-orange"] = "serious",
        ["semi-dark-orange"] = "serious",
        ["red"] = "critical",
        ["dark-red"] = "critical",
        ["semi-dark-red"] = "critical",
    };

    private static readonly Dictionary<string, string> Colours = new()
    {
        ["good"] = "green",
        ["warning"] = "yellow",
        ["serious"] = "orange",
        ["critical"] = "red",
    };

    public static bool LooksLikeOne(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && (root.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array
            || root.TryGetProperty("schemaVersion", out _));

    /// <param name="connections">The connections this studio has, so a datasource can be matched to
    /// one by name or uid rather than asked about.</param>
    public static DashboardImport Read(JsonElement root, IReadOnlyList<ConnectionSpecName>? connections = null)
    {
        var notes = new List<string>();

        if (root.ValueKind != JsonValueKind.Object)
            return new DashboardImport(null, ["this is not a dashboard: the JSON is not an object"]);

        // An export from the API wraps the dashboard: { "dashboard": { … }, "meta": { … } }.
        if (root.TryGetProperty("dashboard", out var inner) && inner.ValueKind == JsonValueKind.Object)
            root = inner;

        var name = Text(root, "title") ?? "imported dashboard";
        var widgets = new List<Widget>();

        foreach (var panel in Panels(root))
            ReadPanel(panel, widgets, notes, connections);

        var time = root.TryGetProperty("time", out var range) && range.ValueKind == JsonValueKind.Object
            ? new DashboardTimeRange(Text(range, "from") ?? "now-24h", Text(range, "to") ?? "now")
            : new DashboardTimeRange();

        var dashboard = Dashboards.Normalise(new Dashboard(
            Id: "", Name: name, Widgets: widgets,
            RefreshSeconds: Refresh(Text(root, "refresh")),
            UpdatedAt: DateTimeOffset.UtcNow,
            TimeRange: time,
            Variables: Variables(root, notes, connections),
            Tags: Strings(root, "tags")));

        if (root.TryGetProperty("annotations", out var annotations)
            && annotations.TryGetProperty("list", out var list)
            && list.EnumerateArray().Any(one => Text(one, "type") != "dashboard"))
            notes.Add("annotations are not imported: this studio has no annotation store");

        return new DashboardImport(dashboard, notes);
    }

    /// Panels in reading order, with a collapsed row's children after the row itself — which is
    /// where Grafana hides them.
    private static IEnumerable<JsonElement> Panels(JsonElement root)
    {
        if (!root.TryGetProperty("panels", out var panels) || panels.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var panel in panels.EnumerateArray())
        {
            yield return panel;

            if (Text(panel, "type") == "row"
                && panel.TryGetProperty("panels", out var children)
                && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray())
                    yield return child;
        }
    }

    private static void ReadPanel(JsonElement panel, List<Widget> widgets, List<string> notes,
        IReadOnlyList<ConnectionSpecName>? connections)
    {
        var kind = Text(panel, "type") ?? "";
        var title = Text(panel, "title") ?? "untitled";
        var known = Types.TryGetValue(kind, out var type);

        var position = panel.TryGetProperty("gridPos", out var grid)
            // Both are 24 columns, so this is a copy rather than a conversion.
            ? new WidgetPosition(Int(grid, "x") ?? 0, Int(grid, "y") ?? 0, Int(grid, "w") ?? 12,
                Int(grid, "h") ?? 8)
            : new WidgetPosition(0, widgets.Count * 8, 12, 8);

        if (kind == "row")
        {
            widgets.Add(new Widget("", WidgetType.Row, title, position with { W = Dashboards.Columns, H = 1 },
                new WidgetSource(), new WidgetMapping(), new WidgetOptions(Grafana: Node(panel))));
            return;
        }

        var (sql, connection, why) = Target(panel, notes, title, connections);

        if (why is not null)
        {
            notes.Add(why);
            return;
        }

        if (!known)
        {
            type = WidgetType.Table;
            notes.Add($"'{title}' is a {kind} panel, which this studio does not draw — it came in as "
                      + "a table of the rows its query returns");
        }

        if (panel.TryGetProperty("transformations", out var transformations)
            && transformations.ValueKind == JsonValueKind.Array
            && transformations.GetArrayLength() > 0)
            notes.Add($"'{title}' has {transformations.GetArrayLength()} transformation(s), which "
                      + "are not imported: what the statement returns is what the widget draws");

        if (panel.TryGetProperty("libraryPanel", out _))
            notes.Add($"'{title}' is a library panel; only the copy inside this file came along");

        var defaults = panel.TryGetProperty("fieldConfig", out var fieldConfig)
                       && fieldConfig.TryGetProperty("defaults", out var found)
            ? found
            : default;

        var stacked = Stacked(defaults, panel);

        widgets.Add(new Widget(
            Id: Text(panel, "id") ?? "",
            Type: stacked ? Stack(type) : type,
            Title: title,
            Position: position,
            Source: new WidgetSource(SourceKind.Sql, connection, sql),
            Mapping: new WidgetMapping(),
            Options: new WidgetOptions(
                Unit: Text(defaults, "unit"),
                Decimals: Int(defaults, "decimals"),
                Min: Number(defaults, "min"),
                Max: Number(defaults, "max"),
                Thresholds: Thresholds(defaults),
                Legend: Legend(panel),
                Stacked: stacked,
                Orientation: Text(panel.TryGetProperty("options", out var options) ? options : default,
                    "orientation"),
                Markdown: type == WidgetType.Text ? Markdown(panel) : null,
                Grafana: Node(panel)),
            Description: Text(panel, "description")));
    }

    private static WidgetType Stack(WidgetType type) => type switch
    {
        WidgetType.Line or WidgetType.Area => WidgetType.StackedArea,
        WidgetType.Bar => WidgetType.StackedBar,
        _ => type,
    };

    /// The statement a panel runs, the connection it runs on, and — where neither is possible — the
    /// sentence that says why this panel could not come along.
    private static (string? Sql, string? ConnectionId, string? Why) Target(JsonElement panel,
        List<string> notes, string title, IReadOnlyList<ConnectionSpecName>? connections)
    {
        if (Types.GetValueOrDefault(Text(panel, "type") ?? "") == WidgetType.Text)
            return (null, null, null);

        if (!panel.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array
            || targets.GetArrayLength() == 0)
            return (null, null, $"'{title}' has no query of its own, so there is nothing to run here");

        foreach (var target in targets.EnumerateArray())
        {
            if (Text(target, "rawSql") is { Length: > 0 } sql)
                return (sql, Connection(panel, target, notes, title, connections), null);

            if (Text(target, "expr") is { Length: > 0 })
                return (null, null,
                    $"'{title}' queries a metrics datasource with an expression, and this studio "
                    + "only talks to databases it has a connection to");
        }

        return (null, null, $"'{title}' has a query this studio cannot read as SQL");
    }

    private static string? Connection(JsonElement panel, JsonElement target, List<string> notes,
        string title, IReadOnlyList<ConnectionSpecName>? connections)
    {
        var source = target.TryGetProperty("datasource", out var one) ? one
            : panel.TryGetProperty("datasource", out var two) ? two
            : default;

        var uid = Text(source, "uid");
        var name = source.ValueKind == JsonValueKind.String ? source.GetString() : Text(source, "name");

        var match = (connections ?? [])
            .FirstOrDefault(c =>
                (uid is { Length: > 0 } && (c.Id.Equals(uid, StringComparison.OrdinalIgnoreCase)
                                            || c.Name.Equals(uid, StringComparison.OrdinalIgnoreCase)))
                || (name is { Length: > 0 } && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        if (match is not null) return match.Id;

        notes.Add($"'{title}' runs on the datasource '{name ?? uid ?? "unnamed"}', which is not a "
                  + "connection here — pick one in the widget's settings");

        return null;
    }

    private static IReadOnlyList<WidgetThreshold> Thresholds(JsonElement defaults)
    {
        if (defaults.ValueKind != JsonValueKind.Object
            || !defaults.TryGetProperty("thresholds", out var thresholds)
            || !thresholds.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
            return [];

        return steps.EnumerateArray()
            .Where(step => step.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number)
            .Select(step => new WidgetThreshold(
                step.GetProperty("value").GetDouble(),
                Levels.GetValueOrDefault(Text(step, "color") ?? "", "warning")))
            .ToList();
    }

    private static bool Stacked(JsonElement defaults, JsonElement panel)
    {
        if (defaults.ValueKind == JsonValueKind.Object
            && defaults.TryGetProperty("custom", out var custom)
            && custom.TryGetProperty("stacking", out var stacking))
        {
            var mode = stacking.ValueKind == JsonValueKind.String
                ? stacking.GetString()
                : Text(stacking, "mode");

            if (mode is { Length: > 0 } and not "none") return true;
        }

        return panel.TryGetProperty("stack", out var stack) && stack.ValueKind == JsonValueKind.True;
    }

    private static bool Legend(JsonElement panel)
    {
        if (!panel.TryGetProperty("options", out var options)
            || !options.TryGetProperty("legend", out var legend))
            return true;

        if (legend.TryGetProperty("showLegend", out var shown)) return shown.ValueKind != JsonValueKind.False;
        if (legend.TryGetProperty("show", out var old)) return old.ValueKind != JsonValueKind.False;

        return true;
    }

    private static string? Markdown(JsonElement panel) =>
        panel.TryGetProperty("options", out var options)
            ? Text(options, "content") ?? (options.TryGetProperty("mode", out _) ? null : null)
            : null;

    private static IReadOnlyList<DashboardVariable> Variables(JsonElement root, List<string> notes,
        IReadOnlyList<ConnectionSpecName>? connections)
    {
        if (!root.TryGetProperty("templating", out var templating)
            || !templating.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
            return [];

        var variables = new List<DashboardVariable>();

        foreach (var one in list.EnumerateArray())
        {
            var name = Text(one, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var kind = Text(one, "type") switch
            {
                "query" => VariableKind.Query,
                "custom" => VariableKind.Custom,
                "constant" or "textbox" => VariableKind.Constant,
                "interval" => VariableKind.Interval,
                var other => Unsupported(other, name, notes),
            };

            if (kind == VariableKind.Query && Query(one) is not { Length: > 0 })
                notes.Add($"the variable '{name}' asks its datasource for values in a way this "
                          + "studio cannot read; give it a statement in the settings");

            var values = one.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                ? options.EnumerateArray().Select(o => Text(o, "value") ?? Text(o, "text"))
                    .Where(v => v is { Length: > 0 }).Select(v => v!).ToList()
                : Text(one, "query")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

            variables.Add(new DashboardVariable(
                name!,
                kind,
                Label: Text(one, "label"),
                ConnectionId: connections?.FirstOrDefault(c =>
                    c.Name.Equals(Text(one.TryGetProperty("datasource", out var source) ? source : default, "uid"),
                        StringComparison.OrdinalIgnoreCase))?.Id,
                Sql: kind == VariableKind.Query ? Query(one) : null,
                Values: kind == VariableKind.Custom ? values : [],
                Multi: one.TryGetProperty("multi", out var multi) && multi.ValueKind == JsonValueKind.True,
                IncludeAll: one.TryGetProperty("includeAll", out var all) && all.ValueKind == JsonValueKind.True,
                Default: one.TryGetProperty("current", out var current)
                    ? Text(current, "value") ?? Text(current, "text")
                    : null));
        }

        return variables;
    }

    private static VariableKind Unsupported(string? kind, string? name, List<string> notes)
    {
        notes.Add($"the variable '{name}' is a {kind ?? "nameless"} variable, which has no meaning "
                  + "here — it came in as a written list you can fill in");
        return VariableKind.Custom;
    }

    /// A query variable's statement, which Grafana keeps as a string or inside an object.
    private static string? Query(JsonElement variable)
    {
        if (!variable.TryGetProperty("query", out var query)) return null;

        return query.ValueKind switch
        {
            JsonValueKind.String => query.GetString(),
            JsonValueKind.Object => Text(query, "query") ?? Text(query, "rawSql"),
            _ => null,
        };
    }

    /// `30s`, `5m`, `1h` — and `false` or empty for a dashboard nobody asked to refresh.
    private static int Refresh(string? refresh)
    {
        if (string.IsNullOrWhiteSpace(refresh) || refresh == "false") return 0;

        var text = refresh.Trim();
        if (!int.TryParse(text[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            return 0;

        return text[^1] switch
        {
            's' => amount,
            'm' => amount * 60,
            'h' => amount * 3600,
            _ => 0,
        };
    }

    // --- out ------------------------------------------------------------------------------------

    /// The dashboard as Grafana's own schema. Where a widget came from Grafana, its original panel
    /// is the starting point, so an export puts back the fields this studio never read.
    public static JsonObject Write(Dashboard dashboard, IReadOnlyList<ConnectionSpecName>? connections = null)
    {
        var panels = new JsonArray();
        var id = 1;

        foreach (var widget in dashboard.Widgets ?? [])
            panels.Add(WritePanel(widget, id++, connections));

        var range = dashboard.TimeRange ?? new DashboardTimeRange();

        return new JsonObject
        {
            ["title"] = dashboard.Name,
            ["uid"] = dashboard.Id is { Length: > 0 } ? dashboard.Id : null,
            ["schemaVersion"] = SchemaVersion,
            ["version"] = 1,
            ["editable"] = true,
            ["refresh"] = dashboard.RefreshSeconds > 0 ? $"{dashboard.RefreshSeconds}s" : "",
            ["time"] = new JsonObject { ["from"] = range.From, ["to"] = range.To },
            ["tags"] = new JsonArray([.. (dashboard.Tags ?? []).Select(tag => JsonValue.Create(tag))]),
            ["templating"] = new JsonObject { ["list"] = WriteVariables(dashboard, connections) },
            ["panels"] = panels,
            // Said out loud in the file, because a dashboard that came from here and goes back is a
            // thing somebody will be reading in a year.
            ["description"] = $"Exported from WebDataStudio. {(dashboard.Widgets ?? []).Count} panels.",
        };
    }

    private static JsonObject WritePanel(Widget widget, int id, IReadOnlyList<ConnectionSpecName>? connections)
    {
        // The panel as it came in, so what this studio never read goes back out.
        var panel = widget.Options.Grafana?.DeepClone()?.AsObject() ?? new JsonObject();

        panel["id"] = id;
        panel["type"] = Back.GetValueOrDefault(widget.Type, "table");
        panel["title"] = widget.Title;
        panel["description"] = widget.Description;
        panel["gridPos"] = new JsonObject
        {
            ["x"] = widget.Position.X,
            ["y"] = widget.Position.Y,
            ["w"] = widget.Position.W,
            ["h"] = widget.Position.H,
        };

        if (widget.Type == WidgetType.Row)
        {
            panel["collapsed"] = false;
            panel["panels"] = new JsonArray();
            panel.Remove("targets");
            return panel;
        }

        if (widget.Type == WidgetType.Text)
        {
            panel["options"] = new JsonObject
            {
                ["mode"] = "markdown",
                ["content"] = widget.Options.Markdown ?? "",
            };
            panel.Remove("targets");
            return panel;
        }

        var connection = connections?.FirstOrDefault(c => c.Id == widget.Source.ConnectionId);

        panel["datasource"] = new JsonObject
        {
            ["type"] = "sql",
            ["uid"] = connection?.Name ?? widget.Source.ConnectionId,
        };

        panel["targets"] = new JsonArray
        {
            new JsonObject
            {
                ["refId"] = "A",
                ["format"] = widget.Type is WidgetType.Table or WidgetType.List ? "table" : "time_series",
                ["rawQuery"] = true,
                // A federated widget is several statements; the joining SQL is the one that
                // describes what the picture is, so that is what travels.
                ["rawSql"] = widget.Source.Sql ?? "",
            },
        };

        var defaults = new JsonObject
        {
            ["unit"] = widget.Options.Unit,
            ["decimals"] = widget.Options.Decimals,
            ["min"] = widget.Options.Min,
            ["max"] = widget.Options.Max,
        };

        if (widget.Options.Thresholds is { Count: > 0 } thresholds)
            defaults["thresholds"] = new JsonObject
            {
                ["mode"] = "absolute",
                ["steps"] = new JsonArray([
                    new JsonObject { ["color"] = "green", ["value"] = null },
                    .. thresholds.Select(one => (JsonNode)new JsonObject
                    {
                        ["color"] = Colours.GetValueOrDefault(one.Level, "yellow"),
                        ["value"] = one.Value,
                    }),
                ]),
            };

        if (widget.Options.Stacked)
            defaults["custom"] = new JsonObject
            {
                ["stacking"] = new JsonObject { ["mode"] = "normal" },
            };

        panel["fieldConfig"] = new JsonObject { ["defaults"] = defaults, ["overrides"] = new JsonArray() };
        panel["options"] = new JsonObject
        {
            ["legend"] = new JsonObject { ["showLegend"] = widget.Options.Legend },
            ["orientation"] = widget.Options.Orientation,
        };

        return panel;
    }

    private static JsonArray WriteVariables(Dashboard dashboard, IReadOnlyList<ConnectionSpecName>? connections)
    {
        var list = new JsonArray();

        foreach (var variable in dashboard.Variables ?? [])
        {
            var connection = connections?.FirstOrDefault(c => c.Id == variable.ConnectionId);

            list.Add(new JsonObject
            {
                ["name"] = variable.Name,
                ["label"] = variable.Label,
                ["type"] = variable.Kind switch
                {
                    VariableKind.Query => "query",
                    VariableKind.Constant => "constant",
                    VariableKind.Interval => "interval",
                    _ => "custom",
                },
                ["multi"] = variable.Multi,
                ["includeAll"] = variable.IncludeAll,
                ["query"] = variable.Kind == VariableKind.Query
                    ? variable.Sql
                    : string.Join(",", variable.Values ?? []),
                ["datasource"] = connection is null ? null : new JsonObject
                {
                    ["type"] = "sql",
                    ["uid"] = connection.Name,
                },
                ["current"] = variable.Default is null ? null : new JsonObject
                {
                    ["text"] = variable.Default,
                    ["value"] = variable.Default,
                },
            });
        }

        return list;
    }

    // --- reading JSON without ceremony ----------------------------------------------------------

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(one => one.ValueKind == JsonValueKind.String)
                .Select(one => one.GetString()!).ToList()
            : [];

    private static JsonNode? Node(JsonElement element) => JsonNode.Parse(element.GetRawText());
}
