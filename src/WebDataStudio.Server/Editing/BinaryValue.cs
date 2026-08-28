using System.Globalization;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Editing;

/// Bytes, on their way in and out of a cell.
///
/// A binary column leaves the server as `0x…` (AdoDriverBase writes it that way), and this is the
/// other direction: the same text, coming back from an edit, becomes bytes again. Without it a file
/// dropped into a cell would arrive as the *text* "0x89504e47…" — a PNG nobody can open, written
/// without complaint.
public static class BinaryValue
{
    /// The largest value the studio will write into a cell this way. Hex doubles the size on the
    /// wire, and a cell editor is not the place to move a video: 8 MB of bytes is 16 MB of request.
    public const int MaxBytes = 8 * 1024 * 1024;

    /// True for the text form a binary cell travels in.
    public static bool Looks(string? value) =>
        value is { Length: >= 2 }
        && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && value.Length % 2 == 0
        && value.AsSpan(2).ToString().All(Uri.IsHexDigit);

    /// The bytes behind `0x…`, or null when this is not that.
    public static byte[]? Parse(string? value) =>
        Looks(value) ? Convert.FromHexString(value!.AsSpan(2)) : null;

    /// What a person reads in the preview. The whole of a megabyte of hex helps nobody, so it is
    /// the size and the first few bytes — enough to tell a PNG from a PDF.
    public static string Describe(byte[] bytes)
    {
        var head = Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length))).ToLowerInvariant();

        return string.Create(CultureInfo.InvariantCulture,
            $"0x{head}… ({bytes.Length} bytes)");
    }

    /// A binary literal in this engine's own spelling, for the preview and for the SQL exporter.
    public static string Literal(byte[] bytes, SqlDialect dialect)
    {
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();

        return dialect switch
        {
            Drivers.SqlServer.SqlServerDialect => $"0x{hex}",
            Drivers.PostgreSql.PostgreSqlDialect => $"'\\x{hex}'::bytea",
            _ => $"X'{hex}'",
        };
    }
}
