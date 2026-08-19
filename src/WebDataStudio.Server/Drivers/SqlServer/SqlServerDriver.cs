using System.Data.Common;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.SqlServer;

public sealed class SqlServerDriver : AdoDriverBase
{
    /// Entra authentication moved out of Microsoft.Data.SqlClient in 7.0: without registering the
    /// provider from Microsoft.Data.SqlClient.Extensions.Azure, a connection string that says
    /// <c>Authentication=Active Directory Default</c> cannot be opened at all. That is exactly the
    /// string Aspire hands a deployed studio for Azure SQL, together with an AZURE_CLIENT_ID for
    /// the container's managed identity, which the credential chain picks up on its own.
    ///
    /// Interactive and device-code flows are deliberately left out: nobody is standing at a
    /// browser inside a container, and a prompt that cannot be answered would hang the request.
    static SqlServerDriver()
    {
        foreach (var method in new[]
                 {
                     SqlAuthenticationMethod.ActiveDirectoryDefault,
                     SqlAuthenticationMethod.ActiveDirectoryManagedIdentity,
                     SqlAuthenticationMethod.ActiveDirectoryMSI,
                     SqlAuthenticationMethod.ActiveDirectoryPassword,
                     SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
                 })
            SqlAuthenticationProvider.SetProvider(method, new ActiveDirectoryAuthenticationProvider());
    }

    public override DriverInfo Info { get; } =
        new("sqlserver", "SQL Server", 1433,
            "Server=localhost,1433;Database=master;User Id=sa;Password=;TrustServerCertificate=True");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiSchema = true, MultiDatabase = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, ActualPlan = true, StoredProcedures = true, Triggers = true,
        Views = true, Sequences = true, ForeignKeys = true, PartialIndexes = true,
        // BACKUP DATABASE writes to the server's own disk. RESTORE needs exclusive access to the
        // database and the file already sitting there, so we do not offer it.
        IncludeColumns = true, Backup = true, UserManagement = true,
        SessionList = true, KillSession = true, ServerStats = true, SlowQueryLog = true,
        SystemCommands = true,
    };

    public override SqlDialect Dialect { get; } = new SqlServerDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new SqlConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        if (parent is null)
            return await QueryAsync(session, ct,
                """
                SELECT name FROM sys.schemas
                 WHERE name NOT IN ('sys','INFORMATION_SCHEMA','guest') AND name NOT LIKE 'db\_%' ESCAPE '\'
                 ORDER BY name
                """,
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, [s, "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, [s, "views"]), "Views", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ProcedureFolder, [s, "procedures"]), "Procedures", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.FunctionFolder, [s, "functions"]), "Functions", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.SequenceFolder, [s, "sequences"]), "Sequences", true),
            ];
        }

        var schema = parent.Path[0];
        var (sql, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => (
                "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @s ORDER BY t.name",
                SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder => (
                "SELECT v.name FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id WHERE s.name = @s ORDER BY v.name",
                SchemaNodeKind.View),
            SchemaNodeKind.ProcedureFolder => (
                "SELECT p.name FROM sys.procedures p JOIN sys.schemas s ON s.schema_id = p.schema_id WHERE s.name = @s ORDER BY p.name",
                SchemaNodeKind.Procedure),
            SchemaNodeKind.FunctionFolder => (
                """
                SELECT o.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
                 WHERE s.name = @s AND o.type IN ('FN','IF','TF') ORDER BY o.name
                """,
                SchemaNodeKind.Function),
            SchemaNodeKind.SequenceFolder => (
                "SELECT q.name FROM sys.sequences q JOIN sys.schemas s ON s.schema_id = q.schema_id WHERE s.name = @s ORDER BY q.name",
                SchemaNodeKind.Sequence),
            _ => (null, SchemaNodeKind.Table),
        };
        if (sql is null) return [];

        return await QueryAsync(session, ct, sql,
            name => new SchemaNode(new SchemaNodeRef(kind, [schema, name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View),
            schema);
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path[0];
        var name = target.Name;

        var columns = new List<ColumnInfo>();
        await using (var cmd = Command(session,
            """
            SELECT c.name,
                   t.name + CASE WHEN t.name IN ('varchar','nvarchar','char','nchar')
                                 THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar)) + ')'
                                 ELSE '' END,
                   c.is_nullable,
                   dc.definition,
                   IIF(pk.column_id IS NULL, 0, 1),
                   c.is_identity,
                   CAST(ep.value AS nvarchar(max)),
                   c.column_id
              FROM sys.columns c
              JOIN sys.objects o ON o.object_id = c.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              JOIN sys.types t ON t.user_type_id = c.user_type_id
              LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
              LEFT JOIN (
                    SELECT ic.object_id, ic.column_id
                      FROM sys.index_columns ic
                      JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                     WHERE i.is_primary_key = 1
              ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
              LEFT JOIN sys.extended_properties ep
                     ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
             WHERE s.name = @s AND o.name = @t
             ORDER BY c.column_id
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4) == 1, reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7)));
        }

        var indexes = new Dictionary<string, IndexInfo>();
        await using (var cmd = Command(session,
            """
            SELECT i.name, c.name, i.is_unique, i.is_primary_key, i.filter_definition
              FROM sys.indexes i
              JOIN sys.objects o ON o.object_id = i.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE s.name = @s AND o.name = @t AND i.name IS NOT NULL
             ORDER BY i.name, ic.key_ordinal
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var indexName = reader.GetString(0);
                if (!indexes.TryGetValue(indexName, out var existing))
                    existing = new IndexInfo(indexName, [], reader.GetBoolean(2), reader.GetBoolean(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4));
                indexes[indexName] = existing with { Columns = existing.Columns.Append(reader.GetString(1)).ToList() };
            }
        }

        var foreignKeys = new Dictionary<string, ForeignKeyInfo>();
        await using (var cmd = Command(session,
            """
            SELECT fk.name, pc.name, rs.name, rt.name, rc.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc
              FROM sys.foreign_keys fk
              JOIN sys.objects t ON t.object_id = fk.parent_object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
              JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
              JOIN sys.objects rt ON rt.object_id = fk.referenced_object_id
              JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
              JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
             WHERE s.name = @s AND t.name = @t
             ORDER BY fk.name, fkc.constraint_column_id
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (!foreignKeys.TryGetValue(key, out var existing))
                    existing = new ForeignKeyInfo(key, [], reader.GetString(2), reader.GetString(3), [],
                        reader.GetString(5), reader.GetString(6));
                foreignKeys[key] = existing with
                {
                    Columns = existing.Columns.Append(reader.GetString(1)).ToList(),
                    ReferencedColumns = existing.ReferencedColumns.Append(reader.GetString(4)).ToList(),
                };
            }
        }

        var triggers = new List<TriggerInfo>();
        await using (var cmd = Command(session,
            """
            SELECT tr.name,
                   IIF(OBJECTPROPERTY(tr.object_id,'ExecIsInsteadOfTrigger') = 1, 'INSTEAD OF', 'AFTER'),
                   STUFF(
                     IIF(OBJECTPROPERTY(tr.object_id,'ExecIsInsertTrigger') = 1, ',INSERT', '') +
                     IIF(OBJECTPROPERTY(tr.object_id,'ExecIsUpdateTrigger') = 1, ',UPDATE', '') +
                     IIF(OBJECTPROPERTY(tr.object_id,'ExecIsDeleteTrigger') = 1, ',DELETE', ''), 1, 1, '')
              FROM sys.triggers tr
              JOIN sys.objects o ON o.object_id = tr.parent_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE s.name = @s AND o.name = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                triggers.Add(new TriggerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        long? rows = null;
        long? size = null;
        await using (var cmd = Command(session,
            """
            SELECT SUM(p.rows), SUM(a.total_pages) * 8192
              FROM sys.partitions p
              JOIN sys.allocation_units a ON a.container_id = p.partition_id
              JOIN sys.objects o ON o.object_id = p.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE s.name = @s AND o.name = @t AND p.index_id IN (0,1)
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                rows = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                size = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }

        return new ObjectDetail(target, columns, indexes.Values.ToList(), foreignKeys.Values.ToList(),
            triggers, rows, size, null, null);
    }

    public override Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope,
        SchemaNodeRef? target, CancellationToken ct) =>
        Analysis.SqlServerAnalyzer.RunAsync(session, target?.Path.FirstOrDefault(), ct);

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        // SHOWPLAN_XML returns the estimated plan without executing; STATISTICS XML executes and
        // returns the actual plan as an extra result set.
        var toggle = mode == PlanMode.Actual ? "STATISTICS XML" : "SHOWPLAN_XML";

        await using (var on = session.Connection.CreateCommand())
        {
            on.CommandText = $"SET {toggle} ON";
            await on.ExecuteNonQueryAsync(ct);
        }

        string? xml;
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            xml = await ReadPlanXmlAsync(reader, ct);
        }
        finally
        {
            await using var off = session.Connection.CreateCommand();
            off.CommandText = $"SET {toggle} OFF";
            await off.ExecuteNonQueryAsync(ct);
        }

        if (xml is null) throw new InvalidOperationException("the server returned no execution plan");

        var ns = XNamespace.Get("http://schemas.microsoft.com/sqlserver/2004/07/showplan");
        var root = XDocument.Parse(xml).Descendants(ns + "RelOp").FirstOrDefault();
        return root is null
            ? new PlanNode("Plan", null, null, null, null, null, [], [])
            : Convert(root, ns);

        static PlanNode Convert(XElement element, XNamespace ns)
        {
            var children = element.Descendants(ns + "RelOp")
                .Where(e => e.Parent?.Parent == element)
                .Select(e => Convert(e, ns)).ToList();

            var operation = (string?)element.Attribute("PhysicalOp") ?? "RelOp";
            var estimatedRows = (double?)element.Attribute("EstimateRows");

            var warnings = new List<string>();
            if (operation.Contains("Scan", StringComparison.OrdinalIgnoreCase) && estimatedRows > 1000)
                warnings.Add("scan over many rows");
            if (element.Descendants(ns + "Warnings").Any()) warnings.Add("the server reported a plan warning");

            return new PlanNode(operation, (string?)element.Attribute("LogicalOp"),
                (double?)element.Attribute("EstimatedTotalSubtreeCost"), estimatedRows,
                (double?)element.Attribute("ActualRows"), null, children, warnings);
        }
    }

    private static async Task<string?> ReadPlanXmlAsync(DbDataReader reader, CancellationToken ct)
    {
        do
        {
            if (reader.FieldCount == 1 &&
                reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase) &&
                await reader.ReadAsync(ct))
                return reader.GetString(0);

            while (await reader.ReadAsync(ct)) { /* skip the data rows of the query itself */ }
        }
        while (await reader.NextResultAsync(ct));

        return null;
    }

    protected override (int? Line, int? Column) LocateError(DbException exception, string sql) =>
        exception is SqlException { LineNumber: > 0 } sqlException ? (sqlException.LineNumber, null) : (null, null);

    // --- helpers -----------------------------------------------------------

    private static DbCommand Command(IDbSession session, string sql, string schema, string? table = null)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@s", schema));
        if (table is not null) cmd.Parameters.Add(new SqlParameter("@t", table));
        return cmd;
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map, string? schema = null)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        if (schema is not null) cmd.Parameters.Add(new SqlParameter("@s", schema));

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }
}
