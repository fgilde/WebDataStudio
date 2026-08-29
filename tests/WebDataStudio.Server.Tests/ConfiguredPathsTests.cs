using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// One setting, one or more paths. A deployment used to get either the folder its repository ships
/// or the one an app host wrote — never both.
public class ConfiguredPathsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-paths").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    [Fact]
    public void One_path_is_still_one_path()
    {
        Assert.Equal(["/data/queries"], ConfiguredPaths.Split("/data/queries"));
        Assert.Empty(ConfiguredPaths.Split("   "));
        Assert.Empty(ConfiguredPaths.Split(null));
    }

    [Fact]
    public void Several_are_separated_the_way_a_PATH_is()
    {
        Assert.Equal(["/data/a", "/data/b"], ConfiguredPaths.Split("/data/a;/data/b"));
        Assert.Equal(["/data/a", "/data/b"], ConfiguredPaths.Split(" /data/a ; /data/b "));
        Assert.Equal(["/data/a", "/data/b"], ConfiguredPaths.Split("/data/a:/data/b"));
    }

    /// The reason `:` alone would be wrong.
    [Fact]
    public void A_windows_drive_letter_is_not_a_separator()
    {
        Assert.Equal([@"C:\data\queries"], ConfiguredPaths.Split(@"C:\data\queries"));
        Assert.Equal([@"C:\a", @"D:\b"], ConfiguredPaths.Split(@"C:\a;D:\b"));
    }

    [Fact]
    public void Files_reads_a_file_as_itself_and_a_folder_as_its_contents()
    {
        var folder = Path.Combine(_dir, "folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "b.sql"), "SELECT 2");
        File.WriteAllText(Path.Combine(folder, "a.sql"), "SELECT 1");

        var single = Path.Combine(_dir, "one.sql");
        File.WriteAllText(single, "SELECT 3");

        var files = ConfiguredPaths.Files($"{single};{folder}", "*.sql").ToList();

        // In the order the paths were given, and inside a folder by name.
        Assert.Equal([single, Path.Combine(folder, "a.sql"), Path.Combine(folder, "b.sql")], files);
    }

    [Fact]
    public void A_path_that_is_not_there_is_skipped_rather_than_thrown()
    {
        var folder = Path.Combine(_dir, "real");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.json"), "{}");

        var files = ConfiguredPaths.Files($"{Path.Combine(_dir, "nope")};{folder}", "*.json").ToList();

        Assert.Single(files);
    }

    [Fact]
    public void First_prefers_a_path_that_exists()
    {
        var folder = Path.Combine(_dir, "here");
        Directory.CreateDirectory(folder);

        Assert.Equal(folder, ConfiguredPaths.First($"{Path.Combine(_dir, "gone")};{folder}"));

        // With nothing on disk at all it still answers with the first one, so a message can name it.
        Assert.Equal("/data/a", ConfiguredPaths.First("/data/a;/data/b"));
    }
}
