namespace WebDataStudio.Server.Admin;

/// A write-through wrapper that only remembers how much went past. The backup endpoint needs to
/// know whether the response has already started before it can turn a tool failure into an error.
public sealed class CountingStream(Stream inner) : Stream
{
    public long Written { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Written += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        Written += buffer.Length;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
        Written += count;
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // The response body is owned by Kestrel; disposing the wrapper must not close it.
    protected override void Dispose(bool disposing) { }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
