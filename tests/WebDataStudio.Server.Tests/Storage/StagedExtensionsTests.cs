using WebDataStudio.Server.Drivers.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// The image carries DuckDB's storage extensions, because a studio in a private network cannot
/// download them. That is a property of the Dockerfile, so it is checked there — a session that
/// silently reached for the internet would time out with nothing useful to say.
public class StagedExtensionsTests
{
    private static string Dockerfile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WebDataStudio.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "Dockerfile"));
    }

    [Fact]
    public void The_image_stages_the_extensions_and_says_where_they_are()
    {
        var text = Dockerfile();

        Assert.Contains("--install-storage-extensions", text);
        Assert.Contains($"{DuckDbExtensions.DirectoryVariable}=/opt/duckdb/extensions", text);
    }

    [Fact]
    public void The_directory_is_read_from_the_environment_so_the_image_decides_it()
    {
        var previous = Environment.GetEnvironmentVariable(DuckDbExtensions.DirectoryVariable);
        Environment.SetEnvironmentVariable(DuckDbExtensions.DirectoryVariable, "/somewhere/else");

        try
        {
            Assert.Equal("/somewhere/else", DuckDbExtensions.BundledDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DuckDbExtensions.DirectoryVariable, previous);
        }
    }
}
