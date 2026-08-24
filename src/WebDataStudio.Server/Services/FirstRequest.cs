namespace WebDataStudio.Server.Services;

/// Whether anything has actually asked this server for a page yet.
///
/// The desktop build watches this: a native window that was created but never fetched anything is
/// an empty frame, and an empty frame is worse than a browser tab. What it cannot do is ask the
/// window whether it rendered — so it asks the server whether it was read.
public static class FirstRequest
{
    private static long _count;

    public static void Seen() => Interlocked.Increment(ref _count);

    public static bool Any => Interlocked.Read(ref _count) > 0;
}
