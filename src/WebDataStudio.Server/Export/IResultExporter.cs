using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Export;

/// Formats a streaming result. Implementations must write as chunks arrive; buffering the whole
/// result defeats the point and is caught by MemoryProfileTests.
public interface IResultExporter
{
    string Format { get; }
    string Label { get; }
    string ContentType { get; }
    string FileExtension { get; }

    /// True for formats whose writer seeks and writes synchronously (zip containers, Parquet
    /// footers). The endpoint stages those through a temp file, because Kestrel's response stream
    /// is neither seekable nor open to synchronous IO.
    bool RequiresSeekableStream => false;

    Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks, ExportOptions options, CancellationToken ct);
}
