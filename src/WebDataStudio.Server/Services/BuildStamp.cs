using System.Reflection;

namespace WebDataStudio.Server.Services;

/// When this build was made. "Which build is running" is a question the studio has to be able to
/// answer about itself, and the file it came out of is the only thing that knows.
public static class BuildStamp
{
    /// The timestamp of whatever file this assembly is in, or now when there is no telling.
    ///
    /// A single-file publish — which is what the desktop download is — reports an **empty**
    /// <see cref="Assembly.Location"/>, because the assembly is inside the bundle rather than on
    /// disk. Passing that to File.GetLastWriteTimeUtc throws, and it threw before anything was
    /// listening: the download crashed on start with "The path is empty".
    public static DateTime Of(Assembly assembly, string? processPath = null)
    {
        var candidates = new[]
        {
            assembly.Location,
            // The executable itself. For a single-file build that is the bundle, and its timestamp
            // is when it was published — which is the answer that was wanted all along.
            processPath ?? Environment.ProcessPath,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;

            try
            {
                if (File.Exists(candidate)) return File.GetLastWriteTimeUtc(candidate);
            }
            catch (Exception)
            {
                // A path this process cannot stat is not worth failing to start over.
            }
        }

        return DateTime.UtcNow;
    }
}
