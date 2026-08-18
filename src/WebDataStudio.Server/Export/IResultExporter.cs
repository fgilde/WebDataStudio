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

    Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks, ExportOptions options, CancellationToken ct);
}
