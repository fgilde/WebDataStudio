using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.MongoDb;

/// MongoDB has no SqlDialect in the real sense; this exists so shared code that quotes a name or
/// pages a result keeps working. Caps.Sql = false is what actually switches the UI.
public sealed class MongoDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\\\"") + "\"";
    public override string ParameterPrefix => "@";
    public override string Paginate(string sql, int offset, int limit) => sql;
    public override bool IsReadOnlyStatement(string sql) => !MongoIsWrite(sql);

    private static bool MongoIsWrite(string sql)
    {
        try { return MongoCommandParser.Parse(sql).IsWrite; }
        catch (Exception) { return false; }
    }
}

public sealed class MongoSession(ConnectionSpec spec, IMongoClient client, IMongoDatabase database) : IDbSession
{
    public ConnectionSpec Spec { get; } = spec;
    public IMongoClient Client { get; } = client;
    public IMongoDatabase Database { get; } = database;

    /// Nothing here is ADO.NET. Anything reaching for Connection is a bug, not a fallback.
    public DbConnection Connection =>
        throw new NotSupportedException("MongoDB does not expose an ADO.NET connection");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class MongoDbDriver : IDbDriver
{
    private const int BatchSize = 200;
    private const int SampleSize = 100;

    public DriverInfo Info { get; } = new("mongodb", "MongoDB", 27017, "mongodb://localhost:27017/database");

    public DriverCapabilities Caps { get; } = new()
    {
        Sql = false, MultiDatabase = true, Backup = true, Restore = true,
        // Mongo does have a planner: queryPlanner for the estimate, executionStats for the real run.
        EstimatedPlan = true, ActualPlan = true,
        SessionList = true, KillSession = true, ServerStats = true, SystemCommands = true,
    };

    public SqlDialect Dialect { get; } = new MongoDialect();

    public Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var url = new MongoUrl(spec.ConnectionString);
        var client = new MongoClient(url);
        var database = client.GetDatabase(url.DatabaseName ?? "admin");
        return Task.FromResult<IDbSession>(new MongoSession(spec, client, database));
    }

    public async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent,
        CancellationToken ct)
    {
        var mongo = Cast(session);

        if (parent is null)
        {
            var names = await (await mongo.Client.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
            return names
                .Where(n => n is not ("admin" or "local" or "config"))
                .Select(n => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [n]), n, true))
                .ToList();
        }

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var database = mongo.Client.GetDatabase(parent.Name);
            var collections = await (await database.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);

            return collections
                .Order(StringComparer.OrdinalIgnoreCase)
                // A collection is the closest thing to a table; the UI treats it as one.
                .Select(c => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Table, [parent.Name, c]), c, false))
                .ToList();
        }

        return [];
    }

    public async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var mongo = Cast(session);
        var database = mongo.Client.GetDatabase(target.Path[0]);
        var collection = database.GetCollection<BsonDocument>(target.Name);

        // Documents have no schema, so the shape is sampled. The comment says so, because a
        // sampled shape is not a promise about every document.
        var sample = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Limit(SampleSize).ToListAsync(ct);

        var fields = new Dictionary<string, (string Type, int Count, int Position)>(StringComparer.Ordinal);
        foreach (var document in sample)
            foreach (var element in document.Elements)
            {
                if (fields.TryGetValue(element.Name, out var existing))
                    fields[element.Name] = (existing.Type == element.Value.BsonType.ToString()
                        ? existing.Type : "mixed", existing.Count + 1, existing.Position);
                else
                    fields[element.Name] = (element.Value.BsonType.ToString(), 1, fields.Count + 1);
            }

        var columns = fields
            .OrderBy(f => f.Value.Position)
            .Select(f => new ColumnInfo(f.Key, f.Value.Type,
                Nullable: f.Value.Count < sample.Count, null,
                IsPrimaryKey: f.Key == "_id", false,
                $"seen in {f.Value.Count} of {sample.Count} sampled documents", f.Value.Position))
            .ToList();

        var indexes = new List<IndexInfo>();
        using (var cursor = await collection.Indexes.ListAsync(ct))
            foreach (var index in await cursor.ToListAsync(ct))
            {
                var keys = index["key"].AsBsonDocument.Names.ToList();
                indexes.Add(new IndexInfo(index["name"].AsString, keys,
                    index.Contains("unique") && index["unique"].ToBoolean(),
                    keys is ["_id"], null));
            }

        var count = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct);

        return new ObjectDetail(target, columns, indexes, [], [], count, null,
            "shape sampled from the first 100 documents", null);
    }

    /// A page of documents, as rows. The data tab asks every engine the same question; for MongoDB
    /// the answer is a `find`, and this is where that happens instead of in the endpoint.
    public async Task<TabularPage?> PageAsync(IDbSession session, SchemaNodeRef target,
        PageQuery query, CancellationToken ct)
    {
        if (target.Kind != SchemaNodeKind.Table || target.Path.Count == 0) return null;

        var mongo = Cast(session);
        var collection = mongo.Client.GetDatabase(target.Path[0])
            .GetCollection<BsonDocument>(target.Name);

        var (filter, note) = query.FilterColumn is { Length: > 0 } && query.Filter is { Length: > 0 }
            ? MongoPage.Filter(query.FilterColumn, query.Filter)
            : (new BsonDocument(), null);

        var find = collection.Find(filter);

        if (query.Sort is { Length: > 0 } sort)
            find = find.Sort(new BsonDocument(sort, query.Desc ? -1 : 1));

        var documents = await find.Skip(query.Offset).Limit(query.Limit).ToListAsync(ct);

        // The shape the driver samples, so the columns are the ones the structure panel shows — plus
        // whatever this page turned up that the sample never saw.
        var detail = await DescribeAsync(session, target, ct);
        var (columns, rows) = MongoPage.Project(documents, detail.Columns);

        // Counting a filtered collection costs a scan on a big one, so it is only asked for when a
        // filter narrowed it; otherwise the collection's own count is already known.
        var total = filter.ElementCount == 0
            ? detail.RowCount
            : await collection.CountDocumentsAsync(filter, cancellationToken: ct);

        return new TabularPage(columns, rows, total,
            // A document is edited as a document. The grid writes cells through a change script,
            // which is SQL — so it says why rather than offering a button that cannot work.
            Editable: false,
            Reason: "a document is edited as a document: use a query tab with updateOne",
            Note: note);
    }

    public async IAsyncEnumerable<ResultChunk> ExecuteAsync(IDbSession session, ScriptRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var mongo = Cast(session);
        var database = request.Schema is { Length: > 0 }
            ? mongo.Client.GetDatabase(request.Schema)
            : mongo.Database;

        // C# forbids yielding from a catch block, so the parse error is captured and emitted below.
        MongoCommand? command = null;
        string? parseError = null;
        try
        {
            command = MongoCommandParser.Parse(request.Sql);
        }
        catch (Exception e) when (e is FormatException or NotSupportedException)
        {
            parseError = e.Message;
        }

        if (command is null)
        {
            yield return new ResultChunk.Error(0, parseError ?? "could not read the command", null, null, null);
            yield break;
        }

        if (session.Spec.ReadOnly && command.IsWrite)
        {
            yield return new ResultChunk.Error(0,
                "this connection is read-only; the command was not executed", "WDS_READONLY", null, null);
            yield break;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var collection = database.GetCollection<BsonDocument>(command.Collection);

        List<BsonDocument> documents;
        long affected = 0;

        var failure = await RunAsync(collection, command, request.MaxRows, ct);
        if (failure.Error is { } error)
        {
            yield return new ResultChunk.Error(0, error, null, null, null);
            yield break;
        }

        documents = failure.Documents;
        affected = failure.Affected;

        if (documents.Count > 0)
        {
            for (var offset = 0; offset < documents.Count; offset += BatchSize)
            {
                var batch = documents.Skip(offset).Take(BatchSize)
                    .Select(d => JsonDocument.Parse(d.ToJson(new JsonWriterSettings
                    {
                        OutputMode = JsonOutputMode.RelaxedExtendedJson,
                    })).RootElement.Clone())
                    .ToList();

                yield return new ResultChunk.Documents(0, batch);
                yield return new ResultChunk.Progress(0, offset + batch.Count, watch.ElapsedMilliseconds);
            }
        }

        yield return new ResultChunk.End(0, affected, watch.ElapsedMilliseconds,
            documents.Count >= request.MaxRows && request.MaxRows > 0);
    }

    private static async Task<(List<BsonDocument> Documents, long Affected, string? Error)> RunAsync(
        IMongoCollection<BsonDocument> collection, MongoCommand command, int maxRows, CancellationToken ct)
    {
        try
        {
            var filter = command.Arguments.Count > 0 ? command.Arguments[0] : [];

            switch (command.Operation.ToLowerInvariant())
            {
                case "find":
                {
                    var query = collection.Find(filter);
                    if (command.Sort is not null) query = query.Sort(command.Sort);
                    if (command.Skip is { } skip) query = query.Skip(skip);
                    query = query.Limit(command.Limit ?? (maxRows > 0 ? maxRows : 1000));
                    return (await query.ToListAsync(ct), 0, null);
                }

                case "findone":
                {
                    var one = await collection.Find(filter).FirstOrDefaultAsync(ct);
                    return (one is null ? [] : [one], 0, null);
                }

                case "aggregate":
                {
                    var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(command.Arguments);
                    return (await (await collection.AggregateAsync(pipeline, cancellationToken: ct))
                        .ToListAsync(ct), 0, null);
                }

                case "countdocuments":
                {
                    var count = await collection.CountDocumentsAsync(filter, cancellationToken: ct);
                    return ([new BsonDocument("count", count)], 0, null);
                }

                case "estimateddocumentcount":
                {
                    var count = await collection.EstimatedDocumentCountAsync(cancellationToken: ct);
                    return ([new BsonDocument("count", count)], 0, null);
                }

                case "insertone":
                    await collection.InsertOneAsync(filter, cancellationToken: ct);
                    return ([], 1, null);

                case "insertmany":
                    await collection.InsertManyAsync(command.Arguments, cancellationToken: ct);
                    return ([], command.Arguments.Count, null);

                case "updateone":
                {
                    var result = await collection.UpdateOneAsync(filter, command.Arguments[1], cancellationToken: ct);
                    return ([], result.ModifiedCount, null);
                }

                case "updatemany":
                {
                    var result = await collection.UpdateManyAsync(filter, command.Arguments[1], cancellationToken: ct);
                    return ([], result.ModifiedCount, null);
                }

                case "replaceone":
                {
                    var result = await collection.ReplaceOneAsync(filter, command.Arguments[1], cancellationToken: ct);
                    return ([], result.ModifiedCount, null);
                }

                case "deleteone":
                {
                    var result = await collection.DeleteOneAsync(filter, ct);
                    return ([], result.DeletedCount, null);
                }

                case "deletemany":
                {
                    var result = await collection.DeleteManyAsync(filter, ct);
                    return ([], result.DeletedCount, null);
                }

                case "getindexes":
                {
                    using var cursor = await collection.Indexes.ListAsync(ct);
                    return (await cursor.ToListAsync(ct), 0, null);
                }

                case "createindex":
                {
                    var name = await collection.Indexes.CreateOneAsync(
                        new CreateIndexModel<BsonDocument>(filter), cancellationToken: ct);
                    return ([new BsonDocument("createdIndex", name)], 1, null);
                }

                case "dropindex":
                    await collection.Indexes.DropOneAsync(filter.Values.First().AsString, ct);
                    return ([], 1, null);

                case "drop":
                    await collection.Database.DropCollectionAsync(collection.CollectionNamespace.CollectionName, ct);
                    return ([], 1, null);

                default:
                    return ([], 0, $"operation '{command.Operation}' is not supported");
            }
        }
        catch (MongoException e)
        {
            return ([], 0, e.Message);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ([], 0, $"{command.Operation} needs more arguments than were given");
        }
    }

    public async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        var mongo = Cast(session);
        var command = MongoCommandParser.Parse(sql);

        var explain = new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["find"] = command.Collection,
                ["filter"] = command.Arguments.Count > 0 ? command.Arguments[0] : new BsonDocument(),
            },
            ["verbosity"] = mode == PlanMode.Actual ? "executionStats" : "queryPlanner",
        };

        var result = await mongo.Database.RunCommandAsync<BsonDocument>(explain, cancellationToken: ct);
        var winning = result.GetValue("queryPlanner", new BsonDocument())
            .AsBsonDocument.GetValue("winningPlan", new BsonDocument()).AsBsonDocument;

        return Convert(winning);

        static PlanNode Convert(BsonDocument plan)
        {
            var stage = plan.GetValue("stage", "UNKNOWN").AsString;
            var children = new List<PlanNode>();

            if (plan.Contains("inputStage")) children.Add(Convert(plan["inputStage"].AsBsonDocument));
            if (plan.Contains("inputStages"))
                children.AddRange(plan["inputStages"].AsBsonArray.Select(s => Convert(s.AsBsonDocument)));

            string[] warnings = stage == "COLLSCAN" ? ["collection scan; no index used"] : [];
            var detail = plan.Contains("indexName") ? plan["indexName"].AsString : null;

            return new PlanNode(stage, detail, null, null, null, null, children, warnings);
        }
    }

    public async Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target,
        CancellationToken ct)
    {
        var mongo = Cast(session);
        var database = target is not null && target.Path.Count > 0
            ? mongo.Client.GetDatabase(target.Path[0])
            : mongo.Database;

        var findings = new List<AnalyzeFinding>();
        var names = await (await database.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);

        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            var collection = database.GetCollection<BsonDocument>(name);
            using var cursor = await collection.Indexes.ListAsync(ct);
            var indexes = await cursor.ToListAsync(ct);

            if (indexes.Count <= 1)
                findings.Add(new AnalyzeFinding("missing-index", "info",
                    $"{name} has only the _id index",
                    "Every query other than a lookup by _id scans the whole collection.",
                    $"db.{name}.createIndex({{ field: 1 }})"));

            foreach (var duplicate in indexes
                .GroupBy(i => string.Join(",", i["key"].AsBsonDocument.Names))
                .Where(g => g.Count() > 1))
                findings.Add(new AnalyzeFinding("duplicate-index", "warning",
                    $"Duplicate indexes on {name}",
                    $"These indexes cover ({duplicate.Key}): " +
                    string.Join(", ", duplicate.Select(d => d["name"].AsString)),
                    null));
        }

        return new AnalyzeReport(findings);
    }

    private static MongoSession Cast(IDbSession session) =>
        // Unwrap: a pooled or tunnelled session is a wrapper around the one this driver opened.
        session.Unwrap() as MongoSession
        ?? throw new InvalidOperationException("this session does not belong to the MongoDB driver");
}
