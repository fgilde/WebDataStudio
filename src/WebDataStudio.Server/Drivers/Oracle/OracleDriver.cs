using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.Oracle;

public sealed class OracleDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string TextType => "VARCHAR2(4000)";

    // Oracle has no SELECT without a FROM.
    public override string Ping => "SELECT 1 FROM dual";

    public override string? RowAddress => "ROWID";
    public override string RowAddressPredicate(string parameter) => $"ROWID = CHARTOROWID({parameter})";

    public override string NumberCast => "CAST({0} AS NUMBER)";

    // Oracle reads a timestamp by the session's NLS format, which is not something to rely on, so
    // the format is spelled out.
    public override string TimestampCast => "TO_TIMESTAMP({0},'YYYY-MM-DD HH24:MI:SS')";

    public override string ParameterPrefix => ":";

    public override string Paginate(string sql, int offset, int limit) =>
        $"{sql} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";

    /// A PL/SQL block writes even though it starts with a keyword the base class does not know.
    public override bool IsReadOnlyStatement(string sql) =>
        !sql.TrimStart().StartsWith("begin", StringComparison.OrdinalIgnoreCase)
        && !sql.TrimStart().StartsWith("declare", StringComparison.OrdinalIgnoreCase)
        && base.IsReadOnlyStatement(sql);

    /// Oracle folds unquoted identifiers to upper case, so the designer and introspection must
    /// agree on which spelling they compare.
    public static string NormalizeIdentifier(string name) => name.ToUpperInvariant();

    /// Oracle's own JSON_VALUE, which wants the path as a literal and an array index rather than a
    /// wildcard.
    public override string JsonPath(string column, string path) =>
        $"JSON_VALUE({column}, '{JsonPathLiteral(path).Replace("[*]", "[0]")}')";


    // Oracle: NUMBER for everything numeric, and no BOOLEAN in a table before 23c.
    public override string BooleanType => "NUMBER(1)";
    public override string SmallIntType => "NUMBER(5)";
    public override string IntType => "NUMBER(10)";
    public override string BigIntType => "NUMBER(19)";
    public override string DoubleType => "BINARY_DOUBLE";
    public override string TimeType => "TIMESTAMP";
    public override string DecimalType(string duckdbType) => "NUMBER";

}

public sealed class OracleDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } =
        new("oracle", "Oracle", 1521, "Data Source=localhost:1521/FREEPDB1;User Id=system;Password=");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiSchema = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, StoredProcedures = true, Triggers = true, Views = true,
        MaterializedViews = true, Sequences = true, ForeignKeys = true,
        Backup = false, Restore = false, UserManagement = true, SessionList = true,
        KillSession = true, ServerStats = true, SystemCommands = true,
        ActivityProgress = true,
    };

    public override SqlDialect Dialect { get; } = new OracleDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new OracleConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct, bool systemObjects = false)
    {
        // An Oracle schema is a user, and an install ships dozens of them — SYS, SYSTEM, XDB and
        // the rest. The server flags them itself, which is a better list than any hard-coded one.
        if (parent is null)
            return await QueryAsync(session, ct,
                $"""
                SELECT username FROM all_users
                 WHERE {(systemObjects ? "1 = 1" : "oracle_maintained = 'N'")}
                 ORDER BY username
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
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.SequenceFolder, [s, "sequences"]), "Sequences", true),
            ];
        }

        var schema = parent.Path[0];
        var (sql, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder =>
                ("SELECT table_name FROM all_tables WHERE owner = :s ORDER BY table_name", SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder =>
                ("SELECT view_name FROM all_views WHERE owner = :s ORDER BY view_name", SchemaNodeKind.View),
            SchemaNodeKind.ProcedureFolder =>
                ("SELECT DISTINCT object_name FROM all_procedures WHERE owner = :s AND object_type = 'PROCEDURE' ORDER BY object_name",
                    SchemaNodeKind.Procedure),
            SchemaNodeKind.SequenceFolder =>
                ("SELECT sequence_name FROM all_sequences WHERE sequence_owner = :s ORDER BY sequence_name",
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
            SELECT c.column_name, c.data_type, c.nullable, c.data_default,
                   CASE WHEN pk.column_name IS NULL THEN 0 ELSE 1 END,
                   c.identity_column, cc.comments, c.column_id
              FROM all_tab_columns c
              LEFT JOIN (
                   SELECT acc.column_name
                     FROM all_constraints ac
                     JOIN all_cons_columns acc ON acc.constraint_name = ac.constraint_name
                                              AND acc.owner = ac.owner
                    WHERE ac.constraint_type = 'P' AND ac.owner = :s AND ac.table_name = :t
              ) pk ON pk.column_name = c.column_name
              LEFT JOIN all_col_comments cc ON cc.owner = c.owner AND cc.table_name = c.table_name
                                           AND cc.column_name = c.column_name
             WHERE c.owner = :s AND c.table_name = :t
             ORDER BY c.column_id
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2) == "Y",
                    reader.IsDBNull(3) ? null : reader.GetValue(3).ToString(),
                    Convert.ToInt32(reader.GetValue(4)) == 1,
                    !reader.IsDBNull(5) && reader.GetString(5) == "YES",
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    Convert.ToInt32(reader.GetValue(7))));
        }

        var indexes = new Dictionary<string, IndexInfo>();
        await using (var cmd = Command(session,
            """
            SELECT i.index_name, ic.column_name, i.uniqueness
              FROM all_indexes i
              JOIN all_ind_columns ic ON ic.index_name = i.index_name AND ic.index_owner = i.owner
             WHERE i.table_owner = :s AND i.table_name = :t
             ORDER BY i.index_name, ic.column_position
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var indexName = reader.GetString(0);
                if (!indexes.TryGetValue(indexName, out var existing))
                    existing = new IndexInfo(indexName, [], reader.GetString(2) == "UNIQUE", false, null);
                indexes[indexName] = existing with { Columns = existing.Columns.Append(reader.GetString(1)).ToList() };
            }
        }

        var foreignKeys = new Dictionary<string, ForeignKeyInfo>();
        await using (var cmd = Command(session,
            """
            SELECT ac.constraint_name, acc.column_name, rc.owner, rc.table_name, rcc.column_name,
                   ac.delete_rule
              FROM all_constraints ac
              JOIN all_cons_columns acc ON acc.constraint_name = ac.constraint_name AND acc.owner = ac.owner
              JOIN all_constraints rc ON rc.constraint_name = ac.r_constraint_name AND rc.owner = ac.r_owner
              JOIN all_cons_columns rcc ON rcc.constraint_name = rc.constraint_name AND rcc.owner = rc.owner
                                       AND rcc.position = acc.position
             WHERE ac.constraint_type = 'R' AND ac.owner = :s AND ac.table_name = :t
             ORDER BY ac.constraint_name, acc.position
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (!foreignKeys.TryGetValue(key, out var existing))
                    existing = new ForeignKeyInfo(key, [], reader.GetString(2), reader.GetString(3), [],
                        reader.IsDBNull(5) ? "NO ACTION" : reader.GetString(5), "NO ACTION");
                foreignKeys[key] = existing with
                {
                    Columns = existing.Columns.Append(reader.GetString(1)).ToList(),
                    ReferencedColumns = existing.ReferencedColumns.Append(reader.GetString(4)).ToList(),
                };
            }
        }

        long? rows = null;
        await using (var cmd = Command(session,
            "SELECT num_rows FROM all_tables WHERE owner = :s AND table_name = :t", schema, name))
        {
            var value = await cmd.ExecuteScalarAsync(ct);
            rows = value is null or DBNull ? null : Convert.ToInt64(value);
        }

        return new ObjectDetail(target, columns, indexes.Values.ToList(), foreignKeys.Values.ToList(),
            [], rows, null, null, null);
    }

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        if (mode == PlanMode.Actual)
            throw new NotSupportedException("Oracle actual plans need SQL trace; only estimated plans are supported");

        var id = $"wds{Guid.NewGuid():N}"[..24];

        await using (var explain = session.Connection.CreateCommand())
        {
            explain.CommandText = $"EXPLAIN PLAN SET STATEMENT_ID = '{id}' FOR {sql}";
            await explain.ExecuteNonQueryAsync(ct);
        }

        var nodes = new List<PlanNode>();
        await using (var read = session.Connection.CreateCommand())
        {
            read.CommandText =
                $"SELECT operation, options, object_name, cost, cardinality FROM plan_table " +
                $"WHERE statement_id = '{id}' ORDER BY id";

            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var operation = reader.GetString(0) +
                    (reader.IsDBNull(1) ? "" : " " + reader.GetString(1));
                var relation = reader.IsDBNull(2) ? null : reader.GetString(2);
                var cost = reader.IsDBNull(3) ? (double?)null : Convert.ToDouble(reader.GetValue(3));
                var rows = reader.IsDBNull(4) ? (double?)null : Convert.ToDouble(reader.GetValue(4));

                string[] warnings = operation.Contains("FULL", StringComparison.OrdinalIgnoreCase)
                    ? ["full table scan"] : [];

                nodes.Add(new PlanNode(operation, relation, cost, rows, null, null, [], warnings));
            }
        }

        return new PlanNode("SELECT STATEMENT", null, nodes.FirstOrDefault()?.EstimatedCost, null, null, null,
            nodes, []);
    }

    // --- helpers -----------------------------------------------------------

    private static DbCommand Command(IDbSession session, string sql, string schema, string? table = null)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        ((OracleCommand)cmd).BindByName = true;
        cmd.Parameters.Add(new OracleParameter("s", schema));
        if (table is not null) cmd.Parameters.Add(new OracleParameter("t", table));
        return cmd;
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map, string? schema = null)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        ((OracleCommand)cmd).BindByName = true;
        if (schema is not null) cmd.Parameters.Add(new OracleParameter("s", schema));

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }
}
