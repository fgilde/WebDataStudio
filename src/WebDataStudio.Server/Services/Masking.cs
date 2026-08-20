using System.Runtime.CompilerServices;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// Applies a mask policy to a result on the way out. Masking here rather than in the browser is the
/// point: a value that never leaves the server cannot be read out of a network tab, a log or a
/// screenshot of the developer tools.
public static class Masking
{
    /// Which of these columns are masked, by index. Empty when nothing is.
    public static HashSet<int> IndexesOf(IReadOnlyList<ColumnMeta> columns, MaskPolicy policy) =>
        [.. columns
            .Select((column, index) => (column, index))
            .Where(entry => SensitiveColumns.ShouldMask(entry.column.Name, policy))
            .Select(entry => entry.index)];

    /// The rows with those columns replaced. A null stays null: "there is nothing here" is not a
    /// secret, and pretending otherwise makes a masked column unreadable for anyone reasoning about
    /// the data.
    public static List<object?[]> Apply(IReadOnlyList<object?[]> rows, HashSet<int> masked)
    {
        if (masked.Count == 0) return [.. rows];

        var result = new List<object?[]>(rows.Count);

        foreach (var row in rows)
        {
            var copy = row.ToArray();
            foreach (var index in masked)
                if (index < copy.Length && copy[index] is not null) copy[index] = SensitiveColumns.Mask;

            result.Add(copy);
        }

        return result;
    }

    /// The same policy applied to a stream of chunks, for the export path. The columns chunk decides
    /// which indexes are hidden; the row chunks that follow it are masked at those indexes.
    public static async IAsyncEnumerable<ResultChunk> Stream(
        IAsyncEnumerable<ResultChunk> chunks, MaskPolicy policy,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var masked = new HashSet<int>();

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns columns:
                    masked.Clear();
                    masked.UnionWith(IndexesOf(columns.Items, policy));
                    yield return columns;
                    break;

                case ResultChunk.Rows rows when masked.Count > 0:
                    yield return rows with { Items = Apply(rows.Items, masked) };
                    break;

                default:
                    yield return chunk;
                    break;
            }
        }
    }

    /// The column list the client gets, with the masked ones marked so the grid can offer a reveal
    /// rather than leaving a user wondering why a value looks like dots.
    public static IReadOnlyList<object> Describe(
        IReadOnlyList<ColumnMeta> columns, HashSet<int> masked) =>
        [.. columns.Select((column, index) => (object)new
        {
            column.Name,
            column.DataType,
            column.Nullable,
            Masked = masked.Contains(index),
        })];
}
