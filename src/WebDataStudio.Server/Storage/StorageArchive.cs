using System.IO.Compression;
using System.Text;

namespace WebDataStudio.Server.Storage;

/// How much of a prefix one download may take with it. A bucket has no size a listing can be asked
/// for, so the limits are counted while the zip is being written.
public sealed record ArchiveLimits(int MaxObjects, long MaxBytes)
{
    public static ArchiveLimits FromConfiguration(IConfiguration config) => new(
        int.TryParse(config["WDS_STORAGE_ARCHIVE_MAX_OBJECTS"], out var objects) && objects > 0
            ? objects
            : 2000,
        long.TryParse(config["WDS_STORAGE_ARCHIVE_MAX_BYTES"], out var bytes) && bytes > 0
            ? bytes
            : 2L * 1024 * 1024 * 1024);
}

/// What a prefix download did: how much went in, and where it stopped if it did.
public sealed record ArchiveResult(int Objects, long Bytes, string? Stopped);

/// A folder in a bucket, taken with you.
///
/// "Save this one file" is a download; "give me this folder" was a click per file. This walks the
/// prefix a page at a time and writes each object straight into a zip on the response, so a hundred
/// files cost a hundred reads and no disk.
///
/// A zip has no length before it is written, so the response cannot say how long it will be and the
/// limits cannot be checked in advance. Whatever stops the walk early is written into the zip as
/// `TRUNCATED.txt` — the same choice the backup path makes, and for the same reason: half an answer
/// that says it is half is better than a file nobody can trust.
public static class StorageArchive
{
    /// One page per call, so the walk is bounded whatever the provider does.
    private const int PageSize = 500;

    public static async Task<ArchiveResult> WriteAsync(IObjectStore store, string prefix,
        Stream target, ArchiveLimits limits, CancellationToken ct)
    {
        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);

        var root = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
        var queue = new Queue<string>();
        queue.Enqueue(root);

        var objects = 0;
        long bytes = 0;
        string? stopped = null;

        while (queue.Count > 0 && stopped is null)
        {
            var folder = queue.Dequeue();
            string? cursor = null;

            do
            {
                var page = await store.ListAsync(folder, cursor, PageSize, ct);
                cursor = page.Cursor;

                foreach (var entry in page.Entries)
                {
                    if (entry.IsPrefix)
                    {
                        // Depth-first would need a stack and buy nothing: what matters is that every
                        // folder under the prefix is visited once.
                        queue.Enqueue(entry.Key.EndsWith('/') ? entry.Key : entry.Key + "/");
                        continue;
                    }

                    if (objects >= limits.MaxObjects)
                    {
                        stopped = $"stopped after {limits.MaxObjects} objects "
                                  + "(WDS_STORAGE_ARCHIVE_MAX_OBJECTS)";
                        break;
                    }

                    if (entry.SizeBytes is { } size && bytes + size > limits.MaxBytes)
                    {
                        stopped = $"stopped at {limits.MaxBytes / 1024 / 1024} MB "
                                  + "(WDS_STORAGE_ARCHIVE_MAX_BYTES)";
                        break;
                    }

                    // The path inside the zip is the key without the prefix that was asked for, so
                    // unzipping gives back the folder and not the whole bucket's key space.
                    var name = Name(entry.Key, root);
                    if (name.Length == 0) continue;

                    var item = zip.CreateEntry(name, CompressionLevel.Fastest);

                    await using var source = await store.OpenReadAsync(entry.Key, ct);
                    await using var into = item.Open();
                    bytes += await source.CopyToCountingAsync(into, ct);

                    objects++;
                }
            }
            while (cursor is { Length: > 0 } && stopped is null);
        }

        if (stopped is not null)
        {
            var note = zip.CreateEntry("TRUNCATED.txt", CompressionLevel.Fastest);
            await using var writer = new StreamWriter(note.Open(), Encoding.UTF8);
            await writer.WriteLineAsync($"This archive is not complete: {stopped}.");
            await writer.WriteLineAsync($"It holds {objects} object(s), {bytes} byte(s).");
        }

        return new ArchiveResult(objects, bytes, stopped);
    }

    /// The name inside the zip: `exports/2026/june.csv` under the prefix `exports/` is `2026/june.csv`.
    private static string Name(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;

    private static async Task<long> CopyToCountingAsync(this Stream from, Stream to,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }

        return total;
    }
}
