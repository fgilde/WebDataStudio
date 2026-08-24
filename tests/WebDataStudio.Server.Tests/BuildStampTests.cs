using System.Reflection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// "Which build is running" has to survive being asked from a single-file download, which is where
/// it used to crash the whole process before anything was listening.
public class BuildStampTests
{
    [Fact]
    public void An_assembly_on_disk_reports_its_own_timestamp()
    {
        var assembly = typeof(BuildStamp).Assembly;

        Assert.Equal(File.GetLastWriteTimeUtc(assembly.Location), BuildStamp.Of(assembly));
    }

    [Fact]
    public void A_single_file_build_falls_back_to_the_executable()
    {
        // A single-file publish reports an empty Location: the assembly is inside the bundle.
        var bundled = new EmptyLocation();
        var exe = typeof(BuildStamp).Assembly.Location;

        Assert.Equal(File.GetLastWriteTimeUtc(exe), BuildStamp.Of(bundled, exe));
    }

    [Fact]
    public void Nothing_to_read_is_answered_with_now_rather_than_an_exception()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);

        // This is the case that took the download down: an empty location and no process path.
        var stamp = BuildStamp.Of(new EmptyLocation(), "");

        Assert.InRange(stamp, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void A_path_that_is_not_there_is_not_an_error_either()
    {
        var stamp = BuildStamp.Of(new EmptyLocation(), Path.Combine(Path.GetTempPath(), "wds-nope"));

        Assert.InRange(stamp, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    /// Stands in for a bundled assembly, which is the one shape this cannot be tested with for real.
    private sealed class EmptyLocation : Assembly
    {
        public override string Location => "";
    }
}
