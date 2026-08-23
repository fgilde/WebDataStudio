using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Npgsql;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One declared parameter, as pg_get_function_arguments spells it.
public sealed record FunctionArgument(string Name, string Type, string Mode, bool HasDefault);

public sealed record FunctionInfo(
    bool Supported, string? Language, string? Returns, bool ReturnsSet,
    IReadOnlyList<FunctionArgument> Arguments, string? Source);

public sealed record TrialRun(
    IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, IReadOnlyList<string> Notices,
    double ElapsedMs, bool Truncated);

/// What a function is and what it does when it runs. Not a debugger: there is no stepping, no
/// breakpoint and no variable inspection. What it offers instead is the source, the declared
/// parameters, and a trial run inside a transaction that is always rolled back, with whatever the
/// function raised as a notice and how long it took.
///
/// For PL/pgSQL that turns RAISE NOTICE into a usable trace, which is how most people debug it
/// anyway.
public static class FunctionInspector
{
    /// A trial run reads at most this many rows: the point is to see that it works, not to page
    /// through a set-returning function.
    private const int MaxRows = 200;

    public static async Task<FunctionInfo> ReadAsync(
        IDbDriver driver, IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        if (driver.Info.Id != "postgresql") return new FunctionInfo(false, null, null, false, [], null);

        var schema = target.Path.Count > 1 ? target.Path[0] : "public";

        await using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT l.lanname,
                   pg_get_function_result(p.oid),
                   p.proretset,
                   pg_get_function_arguments(p.oid),
                   pg_get_functiondef(p.oid)
              FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
              JOIN pg_language l ON l.oid = p.prolang
             WHERE p.proname = @name AND n.nspname = @schema
             -- An overloaded name has more than one row; the first is the one the tree points at.
             ORDER BY p.oid
             LIMIT 1
            """;

        Add(command, "@name", target.Name);
        Add(command, "@schema", schema);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new FunctionInfo(true, null, null, false, [], null);

        return new FunctionInfo(
            true,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            !reader.IsDBNull(2) && reader.GetBoolean(2),
            ParseArguments(reader.IsDBNull(3) ? "" : reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    /// Runs the function and throws the transaction away. Side effects that PostgreSQL keeps
    /// outside a transaction — a sequence that moved, a dblink call, anything the function wrote to
    /// disk — survive the rollback, which is why the caller has to say it accepts that.
    public static async Task<TrialRun> RunAsync(IDbDriver driver, IDbSession session,
        SchemaNodeRef target, IReadOnlyList<string?> values, CancellationToken ct)
    {
        if (driver.Info.Id != "postgresql")
            throw new NotSupportedException($"{driver.Info.Label} has no trial run");

        var info = await ReadAsync(driver, session, target, ct);
        if (info.Source is null) throw new ArgumentException($"no function named {target.Name}");

        // Only the arguments that are passed in; a parameter with a default may be left out, and
        // OUT parameters are results rather than inputs.
        var inputs = info.Arguments.Where(argument => argument.Mode is not "OUT").ToList();
        if (values.Count > inputs.Count)
            throw new ArgumentException(
                $"{target.Name} takes {inputs.Count} arguments, not {values.Count}");

        var notices = new List<string>();
        var connection = session.Connection as NpgsqlConnection
            ?? throw new NotSupportedException("the trial run needs a PostgreSQL connection");

        void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
        connection.Notice += Collect;

        // Notices only arrive while the connection is not multiplexed away from us; asking for them
        // is what makes RAISE NOTICE visible at all.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            var call = new StringBuilder();

            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0) call.Append(", ");
                // The declared type, spelled out: a text parameter handed to an integer argument
                // fails, and guessing which is which is the server's job, not the browser's.
                call.Append($"@p{index}::{inputs[index].Type}");
                Add(command, $"@p{index}", values[index]);
            }

            command.CommandText =
                $"SELECT * FROM {Quote(driver, target, inputs.Count > 0 ? call.ToString() : "")}";
            command.Transaction = transaction;

            var clock = Stopwatch.StartNew();
            await using var reader = await command.ExecuteReaderAsync(ct);

            var columns = new List<string>();
            for (var index = 0; index < reader.FieldCount; index++) columns.Add(reader.GetName(index));

            var rows = new List<object?[]>();
            var truncated = false;

            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == MaxRows) { truncated = true; break; }

                var row = new object?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                    row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);

                rows.Add(row);
            }

            clock.Stop();
            return new TrialRun(columns, rows, notices, clock.Elapsed.TotalMilliseconds, truncated);
        }
        finally
        {
            connection.Notice -= Collect;
            // Always: a trial run that committed would not be a trial.
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static string Quote(IDbDriver driver, SchemaNodeRef target, string arguments)
    {
        var name = target.Path.Count > 1
            ? $"{driver.Dialect.QuoteIdentifier(target.Path[0])}.{driver.Dialect.QuoteIdentifier(target.Name)}"
            : driver.Dialect.QuoteIdentifier(target.Name);

        return $"{name}({arguments})";
    }

    /// "p_from date, p_to date DEFAULT now(), OUT total numeric" into its parts. Splitting on
    /// commas has to skip the ones inside parentheses — a numeric(10, 2) is one type, not two.
    public static List<FunctionArgument> ParseArguments(string declaration)
    {
        var arguments = new List<FunctionArgument>();

        foreach (var part in SplitTopLevel(declaration))
        {
            var text = part.Trim();
            if (text.Length == 0) continue;

            var mode = "IN";
            foreach (var candidate in (string[])["INOUT ", "VARIADIC ", "OUT ", "IN "])
                if (text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    mode = candidate.Trim().ToUpperInvariant();
                    text = text[candidate.Length..].TrimStart();
                    break;
                }

            var hasDefault = false;
            var defaultAt = text.IndexOf(" DEFAULT ", StringComparison.OrdinalIgnoreCase);
            if (defaultAt >= 0) { hasDefault = true; text = text[..defaultAt]; }

            // A parameter may be unnamed, in which case the whole thing is the type.
            var space = text.IndexOf(' ');
            var (name, type) = space < 0
                ? ($"${arguments.Count + 1}", text)
                : (text[..space], text[(space + 1)..].Trim());

            arguments.Add(new FunctionArgument(name, type, mode, hasDefault));
        }

        return arguments;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(' or '[': depth++; break;
                case ')' or ']': depth--; break;
                case ',' when depth == 0:
                    yield return text[start..index];
                    start = index + 1;
                    break;
            }
        }

        if (start < text.Length) yield return text[start..];
    }

    private static void Add(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? (object)DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
