using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Redis;
using WebDataStudio.Server.Endpoints;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Mcp;

/// One tool an agent can call, with the JSON Schema it takes.
public sealed record McpTool(string Name, string Description, object InputSchema, bool Writes);

/// What a tool call produced: text for the agent, and whether it went wrong.
public sealed record McpToolResult(string Text, bool IsError = false);

/// The studio's own capabilities, offered as MCP tools — and to the studio's assistant, which
/// calls the same registry.
///
/// The rules are the studio's rules, not looser ones: a read-only connection stays read-only, a
/// masked column stays masked, and a write is previewed before it runs. An agent gets the same
/// deal a person gets, which is the only way this is safe to expose at all.
public sealed class McpToolbox(
    ConnectionRegistry registry, SessionFactory factory, MaskPolicyStore policies,
    McpOptions options, IMemoryCache cache, WorkspaceStore workspace,
    Analysis.QualityRunner quality)
{
    /// Rows a single tool call may return. An agent that wants more can page with `offset`.
    private const int MaxRows = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // An agent reading `"kind": 0` learns nothing; the name is the whole point of an enum here.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public IReadOnlyList<McpTool> Tools =>
        [.. All()
            .Where(tool => options.AllowWrite || !tool.Writes)
            .Where(tool => options.Only is null || options.Only.Contains(tool.Name))];

    private static IEnumerable<McpTool> All()
    {
        yield return new McpTool("list_connections",
            "The databases this studio can reach: id, name, engine, whether it is read-only, and "
            + "the group and colour it carries. Every other tool takes one of these ids.",
            Object(), Writes: false);

        yield return new McpTool("list_tables",
            "Every table and view of a connection, with its ref — the answer to \"what is in this "
            + "database\" in one call. Use this before list_objects: that one walks the tree a level "
            + "at a time, which is only worth doing for a database too large to list.",
            Object(
                ("connectionId", "string", "Connection id from list_connections.", true),
                ("schema", "string", "Only this schema, if the engine has schemas.", false)),
            Writes: false);

        yield return new McpTool("list_objects",
            "Walks the object tree of a connection. Without a parent it lists the top level "
            + "(schemas or folders); with one it lists that node's children. A node's `ref` is what "
            + "describe_object and browse_rows take.",
            Object(
                ("connectionId", "string", "Connection id from list_connections.", true),
                ("parent", "string", "A node ref, e.g. \"Schema:public\". Omit for the top level.", false)),
            Writes: false);

        yield return new McpTool("describe_object",
            "Columns, indexes, foreign keys and triggers of one table or view, plus its row count "
            + "where the engine knows it.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("ref", "string", "Object ref, e.g. \"Table:public/orders\".", true)),
            Writes: false);

        yield return new McpTool("browse_rows",
            "A page of rows from one table or view, with the same masking and the same row cap the "
            + "studio's data tab applies.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("ref", "string", "Object ref of a table or view.", true),
                ("limit", "integer", $"Rows to return, at most {MaxRows}.", false),
                ("offset", "integer", "Rows to skip.", false)),
            Writes: false);

        yield return new McpTool("run_query",
            "Runs a read-only statement and returns its rows. Writes and DDL are refused here — "
            + "preview_script and apply_script are the way to change anything. Masked columns stay "
            + "masked.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("sql", "string", "One statement. SELECT, SHOW, EXPLAIN, WITH … and the like.", true),
                ("limit", "integer", $"Rows to return, at most {MaxRows}.", false)),
            Writes: false);

        yield return new McpTool("explain_plan",
            "The query plan for a statement, as the engine reports it: operations, estimated cost "
            + "and rows, and the actual rows where the engine can measure them. The way to answer "
            + "\"why is this slow\" without guessing.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("sql", "string", "The statement to explain.", true),
                ("actual", "string", "\"true\" runs the statement to measure it, where the engine can.", false)),
            Writes: false);

        yield return new McpTool("health_report",
            "The studio's own analysis of a connection: missing indexes, duplicate indexes, tables "
            + "without a primary key, bloat, and whatever else the engine can be asked. Each finding "
            + "carries the statement that would fix it, which preview_script then takes.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("ref", "string", "One table, e.g. \"Table:public/orders\". Omit for the connection.", false)),
            Writes: false);

        yield return new McpTool("server_activity",
            "What the server is doing right now: running statements with their age, and who is "
            + "waiting on whom. Empty on an engine that cannot be asked, rather than an error.",
            Object(("connectionId", "string", "Connection id.", true)),
            Writes: false);

        yield return new McpTool("redis_value",
            "One Redis key: its type, TTL and value, in the shape that type has. A key/value store "
            + "has no rows, so browse_rows and run_query do not apply to it.",
            Object(
                ("connectionId", "string", "A Redis connection id.", true),
                ("key", "string", "The key to read.", true),
                ("database", "integer", "Database number, default 0.", false)),
            Writes: false);

        yield return new McpTool("find_data",
            "Looks for a value in every text column of every table, and answers with the tables and "
            + "columns that hold it. The answer to \"where does this customer number actually live\" "
            + "in one call, on a schema nobody documented.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("value", "string", "What to look for.", true),
                ("schema", "string", "Only this schema.", false),
                ("exact", "string", "\"true\" matches the whole value instead of a substring.", false)),
            Writes: false);

        yield return new McpTool("json_shape",
            "What is actually inside a JSON or JSONB column: which paths exist, how often, with "
            + "which types and an example — plus the SELECT that flattens them into columns on this "
            + "engine. Reading one row of a document column is a guess; this is the shape.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("ref", "string", "Object ref of the table, e.g. \"Table:public/events\".", true),
                ("column", "string", "The JSON column.", true),
                ("sample", "integer", "Documents to read, default 200.", false)),
            Writes: false);

        yield return new McpTool("table_sizes",
            "How big every table is, and — once the studio has looked twice — how much bigger than "
            + "it was: the biggest absolute change first, with a per-day rate. Answers \"what is "
            + "eating the disk\" and \"what is growing\" together.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("days", "integer", "How far back to compare, default 30.", false)),
            Writes: false);

        yield return new McpTool("query_stats",
            "The statements this studio has run, grouped by shape: how often, how long, and whether "
            + "they are getting slower. A fingerprint rather than the text, so the same query with "
            + "different parameters is one row.",
            Object(
                ("connectionId", "string", "Only this connection. Omit for all of them.", false),
                ("days", "integer", "How far back, default 7.", false),
                ("top", "integer", "How many statements, default 20.", false)),
            Writes: false);

        yield return new McpTool("inspect_sql",
            "Reads a statement without running it and reports what is worth knowing before it does: "
            + "a DELETE with no WHERE, a cartesian join, a NOT IN that a NULL will break. Cheaper "
            + "than finding out on production.",
            Object(
                ("connectionId", "string", "Whose dialect to read it in. Omit for a common one.", false),
                ("sql", "string", "The statement or script.", true)),
            Writes: false);

        yield return new McpTool("quality_rules",
            "The rules somebody wrote about this connection's data: has a value, no duplicates, "
            + "between two numbers, points at a row that exists, newest value is recent, or their "
            + "own condition.",
            Object(("connectionId", "string", "Connection id.", true)),
            Writes: false);

        yield return new McpTool("run_quality_rules",
            "Runs those rules and answers with how many rows break each one. One counting query per "
            + "rule; a rule that cannot be checked reports why rather than stopping the others.",
            Object(("connectionId", "string", "Connection id.", true)),
            Writes: false);

        yield return new McpTool("save_quality_rule",
            "Writes a rule about the data, so what was found once is watched from then on: a failing "
            + "rule joins the health findings in the studio's alert sweep. Changes the studio's own "
            + "state, not the database.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("table", "string", "The table the rule is about.", true),
                ("kind", "string",
                    "NotNull, Unique, Range, Referential, Freshness or Expression.", true),
                ("column", "string", "The column. Omit for an expression that names its own.", false),
                ("schema", "string", "The schema, where the engine has them.", false),
                ("argument", "string",
                    "What the kind needs: \"0..100\", \"customers.id\", \"24h\", or the condition "
                    + "a bad row satisfies.", false),
                ("message", "string", "What to say when it fails.", false)),
            Writes: true);

        yield return new McpTool("preview_script",
            "Splits a script into statements, says which of them are destructive, and returns a "
            + "hash. Nothing runs. The hash is what apply_script takes, so what runs is what was "
            + "read.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("sql", "string", "The script to look at.", true)),
            Writes: true);

        yield return new McpTool("apply_script",
            "Runs a script that preview_script returned a hash for. Refused on a read-only "
            + "connection, and refused when the hash is unknown or expired.",
            Object(
                ("connectionId", "string", "Connection id.", true),
                ("hash", "string", "Hash from preview_script.", true)),
            Writes: true);
    }

    public async Task<McpToolResult> CallAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var tool = All().FirstOrDefault(t => t.Name == name);

        if (tool is null)
            return new McpToolResult($"there is no tool called '{name}'", IsError: true);

        // A narrowed endpoint refuses by name: a tool that is not listed must not be callable
        // either, or the whitelist is decoration.
        if (options.Only is not null && !options.Only.Contains(name))
            return new McpToolResult(
                $"'{name}' is not one of the tools this endpoint offers (WDS_MCP_TOOLS names them)",
                IsError: true);

        if (tool.Writes && !options.AllowWrite)
            return new McpToolResult(
                "this studio's MCP endpoint is read-only; set WDS_MCP_ALLOW_WRITE=true to change that",
                IsError: true);

        using var span = Telemetry.Span($"mcp.{name}");

        try
        {
            var result = await Dispatch(name, arguments, ct);

            Telemetry.ToolCall(name, result.IsError);
            span?.SetTag("failed", result.IsError);

            return result;
        }
        catch (UnknownConnectionException e) { return Failed(name, e.Message); }
        catch (FormatException e) { return Failed(name, e.Message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return Failed(name, e.Message); }
    }

    private static McpToolResult Failed(string name, string message)
    {
        Telemetry.ToolCall(name, failed: true);
        return new McpToolResult(message, IsError: true);
    }

    private async Task<McpToolResult> Dispatch(string name, JsonElement arguments, CancellationToken ct)
    {
        {
            return name switch
            {
                "list_connections" => ListConnections(),
                "list_tables" => await ListTablesAsync(arguments, ct),
                "list_objects" => await ListObjectsAsync(arguments, ct),
                "describe_object" => await DescribeAsync(arguments, ct),
                "browse_rows" => await BrowseAsync(arguments, ct),
                "run_query" => await QueryAsync(arguments, ct),
                "explain_plan" => await ExplainAsync(arguments, ct),
                "health_report" => await HealthAsync(arguments, ct),
                "server_activity" => await ActivityAsync(arguments, ct),
                "redis_value" => await RedisValueAsync(arguments, ct),
                "find_data" => await FindDataAsync(arguments, ct),
                "json_shape" => await JsonShapeAsync(arguments, ct),
                "table_sizes" => await TableSizesAsync(arguments, ct),
                "query_stats" => QueryStatsReport(arguments),
                "inspect_sql" => await InspectSqlAsync(arguments, ct),
                "quality_rules" => Ok(quality.For(Required(arguments, "connectionId"))),
                "run_quality_rules" => await RunQualityAsync(arguments, ct),
                "save_quality_rule" => SaveQualityRule(arguments),
                "preview_script" => await PreviewAsync(arguments, ct),
                "apply_script" => await ApplyAsync(arguments, ct),
                _ => new McpToolResult($"there is no tool called '{name}'", IsError: true),
            };
        }
    }

    // --- the tools themselves ---------------------------------------------------------------

    private McpToolResult ListConnections() => Ok(registry.All().Select(spec => new
    {
        id = spec.Id,
        name = spec.Name,
        engine = spec.Engine,
        readOnly = spec.ReadOnly,
        group = spec.Group,
        colour = spec.Color,
    }));

    /// Walks the tree for the caller, breadth-first, and returns the leaves. Bounded, because a
    /// database with thousands of tables is exactly where an unbounded walk hurts.
    private async Task<McpToolResult> ListTablesAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var schema = Optional(arguments, "schema");

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            var found = new List<object>();
            var queue = new Queue<SchemaNodeRef?>();
            queue.Enqueue(null);
            var visited = 0;

            while (queue.Count > 0 && visited++ < 200 && found.Count < 500)
            {
                var parent = queue.Dequeue();

                foreach (var node in await driver.IntrospectAsync(session, parent, ct))
                {
                    if (node.Ref.Kind is SchemaNodeKind.Table or SchemaNodeKind.View
                        or SchemaNodeKind.MaterializedView)
                    {
                        if (schema is { Length: > 0 } && node.Ref.Path.Count > 1
                            && !node.Ref.Path[0].Equals(schema, StringComparison.OrdinalIgnoreCase))
                            continue;

                        found.Add(new
                        {
                            @ref = node.Ref.ToString(),
                            name = node.Ref.Name,
                            schema = node.Ref.Path.Count > 1 ? node.Ref.Path[0] : null,
                            kind = node.Ref.Kind.ToString(),
                            detail = node.Detail,
                        });
                        continue;
                    }

                    // A folder or a schema: worth opening, and nothing to report by itself.
                    if (node.HasChildren) queue.Enqueue(node.Ref);
                }
            }

            return Ok(new { count = found.Count, tables = found });
        }
    }

    private async Task<McpToolResult> ListObjectsAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var parent = Optional(arguments, "parent");

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            var target = parent is null ? null : SchemaNodeRef.Parse(parent);
            var nodes = await driver.IntrospectAsync(session, target, ct);

            return Ok(nodes.Select(node => new
            {
                @ref = node.Ref.ToString(),
                label = node.Label,
                kind = node.Ref.Kind.ToString(),
                hasChildren = node.HasChildren,
                detail = node.Detail,
            }));
        }
    }

    private async Task<McpToolResult> DescribeAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var reference = Required(arguments, "ref");

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            var detail = await driver.DescribeAsync(session, SchemaNodeRef.Parse(reference), ct);
            var policy = policies.For(connection);

            return Ok(new
            {
                @ref = detail.Ref.ToString(),
                rowCount = detail.RowCount,
                sizeBytes = detail.SizeBytes,
                comment = detail.Comment,
                columns = detail.Columns.Select(column => new
                {
                    column.Name,
                    column.DataType,
                    column.Nullable,
                    column.IsPrimaryKey,
                    column.Default,
                    // Said out loud, so an agent does not report dots as the value.
                    masked = SensitiveColumns.ShouldMask(column.Name, policy),
                }),
                indexes = detail.Indexes.Select(index => new
                {
                    index.Name, index.Columns, index.Unique, index.Primary,
                }),
                foreignKeys = detail.ForeignKeys.Select(key => new
                {
                    key.Name, key.Columns, key.ReferencedSchema, key.ReferencedTable,
                    key.ReferencedColumns,
                }),
                triggers = detail.Triggers.Select(trigger => new
                {
                    trigger.Name, trigger.Timing, trigger.Event,
                }),
            });
        }
    }

    private async Task<McpToolResult> BrowseAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var reference = Required(arguments, "ref");
        var limit = Math.Clamp(Number(arguments, "limit") ?? 50, 1, MaxRows);
        var offset = Math.Max(Number(arguments, "offset") ?? 0, 0);

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            if (!driver.Caps.TabularBrowse)
                return new McpToolResult(
                    $"{driver.Info.Label} has no rows to browse; it is a key/value store",
                    IsError: true);

            var target = SchemaNodeRef.Parse(reference);
            // The driver's own FROM: for a table it is the qualified name, for a file in a bucket it
            // is the reader that opens it. Building the name here read a Parquet file as a table
            // called "bucket"."key", which is nothing.
            var table = driver.FromClause(session, target) ?? Qualify(target, driver.Dialect);
            var sql = driver.Dialect.Paginate($"SELECT * FROM {table}", offset, limit);

            return await ReadAsync(driver, session, connection, sql, limit, ct);
        }
    }

    private async Task<McpToolResult> QueryAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var sql = Required(arguments, "sql");
        var limit = Math.Clamp(Number(arguments, "limit") ?? 50, 1, MaxRows);

        if (!ReadOnlyStatement.Looks(sql))
            return new McpToolResult(
                "run_query only runs statements that read. Use preview_script and apply_script to "
                + "change something — they show the script before it runs.",
                IsError: true);

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session) return await ReadAsync(driver, session, connection, sql, limit, ct);
    }

    private async Task<McpToolResult> ReadAsync(IDbDriver driver, IDbSession session,
        string connection, string sql, int limit, CancellationToken ct)
    {
        var columns = new List<ColumnMeta>();
        var rows = new List<object?[]>();
        string? error = null;

        var request = new ScriptRequest(sql, limit, 60);
        var policy = policies.For(connection);

        await foreach (var chunk in Masking.Stream(driver.ExecuteAsync(session, request, ct), policy, ct))
            switch (chunk)
            {
                case ResultChunk.Columns c: columns = [.. c.Items]; break;
                case ResultChunk.Rows r: rows.AddRange(r.Items); break;
                case ResultChunk.Error e: error = e.Text; break;
            }

        if (error is not null) return new McpToolResult(error, IsError: true);

        return Ok(new
        {
            columns = columns.Select(column => new { column.Name, column.DataType }),
            rowCount = rows.Count,
            rows = rows.Take(limit).Select(row => row.Select(Text).ToArray()),
        });
    }

    private async Task<McpToolResult> ExplainAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var sql = Required(arguments, "sql");
        var actual = string.Equals(Optional(arguments, "actual"), "true", StringComparison.OrdinalIgnoreCase);

        // An actual plan runs the statement, so it has to obey the same rule run_query does.
        if (actual && !ReadOnlyStatement.Looks(sql))
            return new McpToolResult(
                "an actual plan runs the statement, and this one is not a read; ask for the "
                + "estimated plan instead", IsError: true);

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            if (!driver.Caps.EstimatedPlan && !driver.Caps.ActualPlan)
                return new McpToolResult($"{driver.Info.Label} has no query plans", IsError: true);

            var mode = actual && driver.Caps.ActualPlan ? PlanMode.Actual : PlanMode.Estimated;
            var plan = await driver.ExplainAsync(session, sql, mode, ct);

            return Ok(new { mode = mode.ToString(), plan = Flatten(plan, 0) });
        }
    }

    /// The plan as a flat list with a depth per node: a tree in JSON is harder for a model to read
    /// than an indented list, and the depth is the only part of the shape that matters.
    private static List<object> Flatten(PlanNode node, int depth)
    {
        var flat = new List<object>
        {
            new
            {
                depth,
                operation = node.Operation,
                detail = node.Detail,
                estimatedCost = node.EstimatedCost,
                estimatedRows = node.EstimatedRows,
                actualRows = node.ActualRows,
            },
        };

        foreach (var child in node.Children) flat.AddRange(Flatten(child, depth + 1));
        return flat;
    }

    private async Task<McpToolResult> HealthAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var reference = Optional(arguments, "ref");

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            var target = reference is null ? null : SchemaNodeRef.Parse(reference);
            var scope = target is null ? AnalyzeScope.Connection : AnalyzeScope.Table;
            var report = await driver.AnalyzeAsync(session, scope, target, ct);

            return Ok(new
            {
                count = report.Findings.Count,
                findings = report.Findings.Select(finding => new
                {
                    finding.Category,
                    finding.Severity,
                    finding.Title,
                    finding.Detail,
                    // The statement that fixes it, for preview_script. Never run from here.
                    fix = finding.Statement,
                }),
            });
        }
    }

    private async Task<McpToolResult> ActivityAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            var activity = await ServerActivity.ReadAsync(driver, session, ct);

            return Ok(new
            {
                running = activity.Operations.Select(operation => new
                {
                    session = operation.Id,
                    operation.Kind,
                    operation.Target,
                    operation.PercentComplete,
                    operation.ElapsedMs,
                    statement = operation.Statement,
                }),
                waiting = activity.Waits.Select(wait => new
                {
                    wait.Blocker,
                    wait.Blocked,
                    wait.Resource,
                    wait.WaitMs,
                    statement = wait.Statement,
                }),
            });
        }
    }

    private async Task<McpToolResult> RedisValueAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var key = Required(arguments, "key");
        var database = Number(arguments, "database") ?? 0;

        var (_, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            if (session.Unwrap() is not RedisSession redis)
                return new McpToolResult("that connection is not Redis", IsError: true);

            // The first page of a collection value: enough to see what is in there, capped like
            // every other tool.
            var value = await RedisValues.ReadAsync(
                redis.Multiplexer.GetDatabase(database), key, 0, MaxRows, ct);

            return value is null
                ? new McpToolResult($"there is no key '{key}'", IsError: true)
                : Ok(value);
        }
    }

    private async Task<McpToolResult> PreviewAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var sql = Required(arguments, "sql");

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            if (session.Spec.ReadOnly)
                return new McpToolResult("this connection is read-only", IsError: true);

            var statements = StatementSplitter.Split(sql, driver.Dialect);
            if (statements.Count == 0)
                return new McpToolResult("there is nothing to run", IsError: true);

            var hash = Hash(connection, sql);
            cache.Set($"mcp:{hash}", (connection, sql), TimeSpan.FromMinutes(10));

            return Ok(new
            {
                hash,
                statements = statements.Select(statement => new
                {
                    sql = statement.Text.Trim(),
                    destructive = Destructive(statement.Text),
                }),
                note = "nothing has run yet; call apply_script with this hash to run it",
            });
        }
    }

    private async Task<McpToolResult> ApplyAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var hash = Required(arguments, "hash");

        if (cache.Get($"mcp:{hash}") is not ValueTuple<string, string> planned
            || planned.Item1 != connection)
            return new McpToolResult(
                "that hash is unknown or expired; call preview_script again", IsError: true);

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            if (session.Spec.ReadOnly)
                return new McpToolResult("this connection is read-only", IsError: true);

            var affected = 0L;
            string? error = null;

            var request = new ScriptRequest(planned.Item2, MaxRows, 300, Transactional: true);

            await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
                switch (chunk)
                {
                    case ResultChunk.End e: affected += e.RowsAffected; break;
                    case ResultChunk.Error e: error = e.Text; break;
                }

            if (error is not null) return new McpToolResult(error, IsError: true);

            cache.Remove($"mcp:{hash}");
            return Ok(new { applied = true, rowsAffected = affected });
        }
    }

    // --- plumbing ---------------------------------------------------------------------------

    private static string Qualify(SchemaNodeRef target, SqlDialect dialect) =>
        target.Path.Count > 1
            ? $"{dialect.QuoteIdentifier(target.Path[0])}.{dialect.QuoteIdentifier(target.Name)}"
            : dialect.QuoteIdentifier(target.Name);

    private static bool Destructive(string sql) =>
        new[] { "DROP", "TRUNCATE", "DELETE", "ALTER TABLE", "UPDATE" }
            .Any(word => sql.TrimStart().StartsWith(word, StringComparison.OrdinalIgnoreCase));

    private static string Hash(string connection, string sql) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(connection + "\n" + sql)))[..32].ToLowerInvariant();

    private static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime date => date.ToString("O"),
        DateTimeOffset date => date.ToString("O"),
        _ => value.ToString(),
    };

    /// A value, anywhere. The same walk the studio's own "Find data" does, with the same cap.
    private async Task<McpToolResult> FindDataAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var value = Required(arguments, "value");
        var exact = string.Equals(Optional(arguments, "exact"), "true",
            StringComparison.OrdinalIgnoreCase);

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            var result = await DataSearch.RunAsync(driver, session, value,
                Optional(arguments, "schema"), exact, DataSearch.DefaultMaxTables, 30, ct);

            return Ok(new
            {
                hits = result.Hits,
                result.TablesSearched,
                result.TablesSkipped,
                result.Notes,
                result.Truncated,
            });
        }
    }

    /// The shape of a document column, and the SELECT that flattens it.
    private async Task<McpToolResult> JsonShapeAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var reference = Required(arguments, "ref");
        var column = Required(arguments, "column");

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            var target = SchemaNodeRef.Parse(reference);
            var detail = await driver.DescribeAsync(session, target, ct);

            // Only a column that exists: the name is interpolated into SQL.
            if (detail.Columns.All(c => !c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                return new McpToolResult($"no column '{column}' on {target.Name}", IsError: true);

            if (driver.FromClause(session, target) is not { } from)
                return new McpToolResult("this object cannot be read", IsError: true);

            var report = await JsonShape.DescribeAsync(driver, session, from, column,
                Number(arguments, "sample") ?? JsonShape.DefaultSample, ct);

            return Ok(new
            {
                report.Sampled,
                report.Parsed,
                report.Note,
                paths = report.Paths.Select(path => new
                {
                    path.Path,
                    path.Types,
                    path.Present,
                    path.Example,
                    expression = JsonShape.Expression(driver.Dialect, column, path.Path),
                }),
                flatten = JsonShape.FlattenSql(driver.Dialect, from, column, report.Paths),
            });
        }
    }

    /// How big, and how much bigger than last time. Asking records a sample, so the history builds
    /// itself — the same as the admin panel.
    private async Task<McpToolResult> TableSizesAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var window = Math.Clamp(Number(arguments, "days") ?? 30, 1, 365);

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session)
        {
            if (!Analysis.TableSizes.Supported(driver.Info.Id))
                return Ok(new
                {
                    available = false,
                    reason = $"{driver.Info.Label} does not report a size per table",
                });

            var sizes = await Analysis.TableSizes.ReadAsync(driver, session, ct);

            if (sizes.Count > 0 && workspace.Available)
                workspace.AddSizeSamples(connection,
                    sizes.Select(size => (size.Schema, size.Table, size.Bytes, size.Rows)));

            var samples = workspace.Available
                ? workspace.ListSizeSamples(connection, DateTimeOffset.UtcNow.AddDays(-window))
                    .Select(sample => new Analysis.SizeGrowth.Sample(sample.Schema, sample.Table,
                        sample.Bytes, sample.Rows, sample.At))
                : [];

            return Ok(new
            {
                available = true,
                days = window,
                tables = sizes.Take(100),
                growth = Analysis.SizeGrowth.Between(samples),
            });
        }
    }

    /// What this studio has run, grouped by shape and told whether it is getting slower.
    private McpToolResult QueryStatsReport(JsonElement arguments)
    {
        if (!workspace.Available)
            return new McpToolResult("this studio has no workspace file, so it kept no history",
                IsError: true);

        var window = Math.Clamp(Number(arguments, "days") ?? 7, 1, 365);
        var since = DateTimeOffset.UtcNow.AddDays(-window);

        var entries = workspace.ListHistory(Optional(arguments, "connectionId"), null, 5000)
            .Where(entry => entry.ExecutedAt >= since)
            .ToList();

        return Ok(new
        {
            days = window,
            runs = entries.Count,
            statements = QueryStats.Report(entries, Number(arguments, "top") ?? 20),
        });
    }

    /// What is worth knowing about a statement before it runs.
    private async Task<McpToolResult> InspectSqlAsync(JsonElement arguments, CancellationToken ct)
    {
        var sql = Required(arguments, "sql");
        var connection = Optional(arguments, "connectionId");

        // No connection named: the checks that matter here read the same in every dialect, so a
        // default beats a refusal.
        if (connection is not { Length: > 0 })
            return Ok(SqlInspections.Inspect(sql, new Drivers.PostgreSql.PostgreSqlDialect()));

        var (driver, session) = await factory.OpenAsync(connection, ct);
        await using (session) return Ok(SqlInspections.Inspect(sql, driver.Dialect));
    }

    private async Task<McpToolResult> RunQualityAsync(JsonElement arguments, CancellationToken ct)
    {
        var connection = Required(arguments, "connectionId");
        var results = await quality.RunAsync(connection, ct);

        return Ok(new
        {
            ran = results.Count,
            failing = results.Count(result => !result.Passed),
            results = results.Select(result => new
            {
                table = result.Rule.Table,
                column = result.Rule.Column,
                kind = result.Rule.Kind.ToString(),
                result.Violations,
                result.Error,
                describes = result.Describe(),
                result.Statement,
            }),
        });
    }

    private McpToolResult SaveQualityRule(JsonElement arguments)
    {
        var connection = Required(arguments, "connectionId");
        var kind = Required(arguments, "kind");

        if (!Enum.TryParse<Analysis.QualityKind>(kind, ignoreCase: true, out var parsed))
            return new McpToolResult(
                $"'{kind}' is not a rule; the kinds are "
                + string.Join(", ", Enum.GetNames<Analysis.QualityKind>()),
                IsError: true);

        var rule = new Analysis.QualityRule(
            Guid.NewGuid().ToString("N")[..12],
            connection,
            Optional(arguments, "schema") ?? "",
            Required(arguments, "table"),
            Optional(arguments, "column") ?? "",
            parsed,
            Optional(arguments, "argument"),
            Optional(arguments, "message"));

        quality.Save(rule);
        return Ok(rule);
    }

    private static McpToolResult Ok(object payload) =>
        new(JsonSerializer.Serialize(payload, Json));

    private static string Required(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"'{name}' is required");

    private static string? Optional(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number : null,
            JsonValueKind.String => int.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    /// A JSON Schema object from (name, type, description, required) tuples — enough of the spec
    /// for a tool definition, without a schema library.
    private static object Object(params (string Name, string Type, string Description, bool Required)[] fields)
    {
        var properties = new Dictionary<string, object>();
        foreach (var field in fields)
            properties[field.Name] = new { type = field.Type, description = field.Description };

        return new
        {
            type = "object",
            properties,
            required = fields.Where(field => field.Required).Select(field => field.Name).ToArray(),
        };
    }
}

/// Whether a statement only reads. Deliberately a whitelist: anything this does not recognise is
/// treated as a write, so a new keyword cannot slip through as "probably fine".
public static class ReadOnlyStatement
{
    private static readonly string[] Reading =
    [
        "SELECT", "WITH", "SHOW", "EXPLAIN", "DESCRIBE", "DESC", "PRAGMA", "VALUES", "TABLE",
        // Redis and MongoDB consoles speak their own commands; these are their read verbs.
        "GET", "MGET", "HGET", "HGETALL", "LRANGE", "SMEMBERS", "ZRANGE", "SCAN", "TTL", "TYPE",
        "EXISTS", "INFO", "DBSIZE", "FIND", "COUNT", "AGGREGATE", "DISTINCT",
    ];

    public static bool Looks(string sql)
    {
        var trimmed = sql.TrimStart();

        // A leading comment is fine; skip line comments before judging the first word.
        while (trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            var newline = trimmed.IndexOf('\n');
            if (newline < 0) return false;
            trimmed = trimmed[(newline + 1)..].TrimStart();
        }

        var word = new string([.. trimmed.TakeWhile(char.IsLetter)]);
        if (word.Length == 0) return false;

        // One statement only: a reading first statement followed by a write would sail through.
        var rest = trimmed.TrimEnd().TrimEnd(';');
        if (rest.Contains(';', StringComparison.Ordinal)) return false;

        return Reading.Contains(word, StringComparer.OrdinalIgnoreCase);
    }
}
