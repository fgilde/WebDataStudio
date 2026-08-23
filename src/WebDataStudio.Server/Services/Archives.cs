using System.Text;
using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// What an archive is, from the outside: a name, what is in it, and how big it got.
public sealed record ArchiveInfo(
    string Name, IReadOnlyList<ArchiveColumn> Columns, long Rows, long SizeBytes,
    DateTimeOffset SavedAt, string? Source);

public sealed record ArchiveColumn(string Name, string DataType);

/// One page out of an archive, shaped like a page of a table so the same grid can draw it.
public sealed record ArchivePage(
    IReadOnlyList<ArchiveColumn> Columns, IReadOnlyList<object?[]> Rows, long Total, int Offset);

/// Results kept as files on the studio's own disk: DbGate calls them archives, and they answer
/// "what did this look like last Tuesday" without a second database to put it in.
///
/// The format is NDJSON, so it is readable with anything: the first line is the header (columns,
/// when it was written, where it came from), every line after it is one row as a JSON array. That
/// keeps a large archive streamable in both directions — nothing here ever holds a whole file.
public sealed class Archives
{
    private readonly string _root;

    /// Why the directory is unusable, or null. Like the other stores: a path that cannot be written
    /// is a state to report, not a reason to refuse to start.
    public string? Error { get; }

    public bool Available => Error is null;

    public string Path => _root;

    public Archives(string root)
    {
        _root = root;

        try { Directory.CreateDirectory(root); }
        catch (Exception e) { Error = e.Message; }
    }

    /// A name that is a file name and nothing else: no directory, no traversal, no surprise.
    public static string Sanitize(string name)
    {
        var clean = new StringBuilder(name.Length);

        foreach (var c in name.Trim())
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.') clean.Append(c == ' ' ? '-' : c);

        var result = clean.ToString().Trim('.', '-');
        if (result.Length == 0) throw new ArgumentException("an archive needs a name");

        return result.Length > 80 ? result[..80] : result;
    }

    private string FileOf(string name) =>
        System.IO.Path.Combine(_root, Sanitize(name) + ".ndjson");

    public IReadOnlyList<ArchiveInfo> List()
    {
        if (!Available) return [];

        var archives = new List<ArchiveInfo>();

        foreach (var file in Directory.EnumerateFiles(_root, "*.ndjson"))
        {
            var info = ReadHeader(file);
            if (info is not null) archives.Add(info);
        }

        return [.. archives.OrderByDescending(archive => archive.SavedAt)];
    }

    public ArchiveInfo? Find(string name) =>
        Available && File.Exists(FileOf(name)) ? ReadHeader(FileOf(name)) : null;

    /// Reads the header line only. A file whose header cannot be read is not an archive, and saying
    /// so is better than listing something that cannot be opened.
    private static ArchiveInfo? ReadHeader(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            var first = reader.ReadLine();
            if (first is null) return null;

            var header = JsonSerializer.Deserialize<Header>(first);
            if (header?.Columns is null) return null;

            var length = new FileInfo(file).Length;

            return new ArchiveInfo(
                System.IO.Path.GetFileNameWithoutExtension(file),
                [.. header.Columns.Select(column => new ArchiveColumn(column.Name, column.DataType))],
                header.Rows, length, header.SavedAt, header.Source);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record Header(
        List<ArchiveColumn> Columns, long Rows, DateTimeOffset SavedAt, string? Source);

    /// Writes an archive from a stream of result chunks. The row count is only known at the end, so
    /// the header is written twice: once to reserve the line, and once over it when the count is in.
    public async Task<ArchiveInfo> SaveAsync(string name, string? source,
        IAsyncEnumerable<ResultChunk> chunks, int maxRows, CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException($"the archive directory is not usable: {Error}");

        var file = FileOf(name);
        var temp = file + ".writing";
        var columns = new List<ArchiveColumn>();
        long rows = 0;

        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream))
            {
                // The header is rewritten at the end, so it has to keep its length: a fixed-width
                // count leaves room for it without moving the rows.
                var placeholder = writer.BaseStream.Position;
                await writer.WriteLineAsync(new string(' ', HeaderWidth));

                await foreach (var chunk in chunks.WithCancellation(ct))
                {
                    switch (chunk)
                    {
                        case ResultChunk.Error error:
                            throw new InvalidOperationException(error.Text);

                        case ResultChunk.Columns meta:
                            columns.Clear();
                            columns.AddRange(meta.Items.Select(c => new ArchiveColumn(c.Name, c.DataType)));
                            break;

                        case ResultChunk.Rows batch:
                            foreach (var row in batch.Items)
                            {
                                if (rows >= maxRows) break;
                                await writer.WriteLineAsync(JsonSerializer.Serialize(row));
                                rows++;
                            }
                            break;
                    }
                }

                await writer.FlushAsync(ct);

                // Back over the placeholder with the real header.
                var header = JsonSerializer.Serialize(new Header(columns, rows, DateTimeOffset.UtcNow, source));

                // Padded to the same number of *bytes* as the placeholder, not characters: a column
                // name outside ASCII is more than one byte, and a header one byte too long would
                // write over the first row.
                var width = Encoding.UTF8.GetByteCount(header);
                if (width > HeaderWidth)
                    throw new InvalidOperationException("the archive header does not fit; too many columns");

                writer.BaseStream.Seek(placeholder, SeekOrigin.Begin);
                await writer.WriteAsync(header + new string(' ', HeaderWidth - width));
                await writer.FlushAsync(ct);
            }

            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        return Find(name) ?? throw new InvalidOperationException("the archive was written but cannot be read");
    }

    /// Room for the header line. Generous: a hundred columns with long names and types still fit,
    /// and the space costs nothing next to the rows.
    private const int HeaderWidth = 64 * 1024;

    public ArchivePage Read(string name, int offset, int limit)
    {
        var info = Find(name) ?? throw new FileNotFoundException($"no archive named '{name}'");
        var rows = new List<object?[]>();

        using var reader = new StreamReader(FileOf(name));
        reader.ReadLine(); // the header

        var index = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (index++ < offset) continue;
            if (rows.Count >= limit) break;

            rows.Add(JsonSerializer.Deserialize<object?[]>(line) ?? []);
        }

        return new ArchivePage(info.Columns, rows, info.Rows, offset);
    }

    /// Every row, for the paths that stream one out again — an export, or a comparison.
    public IEnumerable<object?[]> ReadAll(string name)
    {
        using var reader = new StreamReader(FileOf(name));
        reader.ReadLine();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            yield return JsonSerializer.Deserialize<object?[]>(line) ?? [];
        }
    }

    public bool Delete(string name)
    {
        var file = FileOf(name);
        if (!File.Exists(file)) return false;

        File.Delete(file);
        return true;
    }
}
