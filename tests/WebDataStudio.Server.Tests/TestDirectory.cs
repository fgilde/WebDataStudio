namespace WebDataStudio.Server.Tests;

/// Cleanup for the temporary directory a test writes its SQLite files into.
///
/// The obvious way to make the delete succeed on Windows is
/// <c>SqliteConnection.ClearAllPools()</c> — but that closes pooled connections for the whole
/// process, including the ones another test class is using at that moment, which showed up as one
/// suite failing with a 502 only when the full suite ran. A leftover temp directory is harmless;
/// closing somebody else's connection is not.
public static class TestDirectory
{
    public static void Remove(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // The operating system reclaims it; a test must not fail over its own scratch space.
        }
    }

    /// The same tolerance for a single scratch file.
    public static void RemoveFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
