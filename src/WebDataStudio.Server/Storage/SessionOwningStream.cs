namespace WebDataStudio.Server.Storage;

/// A stream that hands its session back when it is closed.
///
/// A download is the one response whose body outlives the handler: the bytes are still being read
/// from the bucket long after the endpoint returned. The session it was read through has to stay
/// open until then — and it has to be given back *after* then, or the connection quietly loses one
/// of its slots per file. Four downloads on a connection that allows four sessions, and everything
/// on it waits for a slot that is never coming back.
///
/// ASP.NET disposes the stream it wrote, whether the response finished or the client went away, so
/// this is the one place both cases meet.
public sealed class SessionOwningStream(Stream inner, IAsyncDisposable session) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        inner.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        inner.ReadAsync(buffer, offset, count, ct);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;

        inner.Dispose();

        // The session's own disposal is asynchronous; this path is the synchronous one ASP.NET
        // takes for a response it did not stream asynchronously.
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await session.DisposeAsync();
    }
}
