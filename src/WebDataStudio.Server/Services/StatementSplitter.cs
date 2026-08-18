using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

public sealed record Statement(string Text, int StartOffset, int EndOffset, int StartLine);

/// Splits a script into executable statements. A character scanner, not a parser: it only needs
/// to know where strings, comments, quoted identifiers and dollar-quoted bodies begin and end.
public static class StatementSplitter
{
    public static IReadOnlyList<Statement> Split(string sql, SqlDialect dialect)
    {
        var statements = new List<Statement>();
        var current = new StringBuilder();
        var start = 0;
        var line = 1;
        var startLine = 0;
        var i = 0;

        void Flush(int end)
        {
            var text = current.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                statements.Add(new Statement(text, start, end, startLine));
            current.Clear();
            start = end + 1;
            // startLine is set by Begin() when the next statement's first real character shows up:
            // capturing it here would report the line of the terminator, not of the statement.
            startLine = 0;
        }

        // Marks where the current statement actually begins, skipping the whitespace after the
        // previous terminator.
        void Begin(int offset)
        {
            if (startLine != 0) return;
            startLine = line;
            start = offset;
        }

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '\n') line++;
            if (!char.IsWhiteSpace(c)) Begin(i);

            // line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') current.Append(sql[i++]);
                continue;
            }

            // block comment
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                current.Append(sql[i++]);
                current.Append(sql[i++]);
                while (i < sql.Length && !(sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/'))
                {
                    if (sql[i] == '\n') line++;
                    current.Append(sql[i++]);
                }
                if (i < sql.Length) { current.Append(sql[i++]); current.Append(sql[i++]); }
                continue;
            }

            // string literal or quoted identifier
            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                current.Append(sql[i++]);
                while (i < sql.Length)
                {
                    if (sql[i] == '\n') line++;
                    // a doubled quote is an escaped quote, not a terminator
                    if (sql[i] == close && i + 1 < sql.Length && sql[i + 1] == close)
                    {
                        current.Append(sql[i++]);
                        current.Append(sql[i++]);
                        continue;
                    }
                    if (sql[i] == close) { current.Append(sql[i++]); break; }
                    current.Append(sql[i++]);
                }
                continue;
            }

            // dollar-quoted body: $$ ... $$ or $tag$ ... $tag$
            if (c == '$')
            {
                var close = sql.IndexOf('$', i + 1);
                if (close > i)
                {
                    var tag = sql[i..(close + 1)];
                    var end = sql.IndexOf(tag, close + 1, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        var body = sql[i..(end + tag.Length)];
                        line += body.Count(ch => ch == '\n');
                        current.Append(body);
                        i = end + tag.Length;
                        continue;
                    }
                }
            }

            // SQL Server batch separator: a line containing only GO
            if (dialect.UsesGoBatchSeparator && c is 'g' or 'G' && IsGoLine(sql, i, out var afterGo))
            {
                Flush(i - 1);
                line++;
                i = afterGo;
                continue;
            }

            if (c == ';')
            {
                Flush(i);
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        Flush(sql.Length);
        return statements;
    }

    /// True when position `i` starts a standalone GO line: only whitespace before it on the line
    /// and nothing but whitespace after it until the newline.
    private static bool IsGoLine(string sql, int i, out int afterGo)
    {
        afterGo = i;
        var lineStart = sql.LastIndexOf('\n', Math.Max(i - 1, 0));
        var before = sql[(lineStart + 1)..i];
        if (before.Trim().Length != 0) return false;
        if (i + 2 > sql.Length) return false;
        if (!sql.AsSpan(i, 2).Equals("GO", StringComparison.OrdinalIgnoreCase)) return false;

        var j = i + 2;
        while (j < sql.Length && sql[j] is ' ' or '\t' or '\r') j++;
        if (j < sql.Length && sql[j] != '\n') return false;

        afterGo = j < sql.Length ? j + 1 : j;
        return true;
    }
}
