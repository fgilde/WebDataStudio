using System.Text.RegularExpressions;

namespace WebDataStudio.Server.Tests;

/// The spec's feature inventory stops being a promise and becomes a checked artefact: every id it
/// names has to appear in docs/features.md with a status somebody can act on.
public class FeatureCoverageTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WebDataStudio.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static readonly Regex Row = new(@"^\| (F\d+\.\d+) \| (.*?) \|\s*$", RegexOptions.Multiline);

    private static Dictionary<string, string> SpecFeatures() =>
        Row.Matches(File.ReadAllText(Path.Combine(RepositoryRoot(),
                "docs", "superpowers", "specs", "2026-08-18-webdatastudio-design.md")))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static Dictionary<string, string[]> DocumentedFeatures() =>
        File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "features.md"))
            .Where(line => Regex.IsMatch(line, @"^\| F\d+\.\d+ \|"))
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries))
            // The split leaves an empty leading and trailing cell from the outer pipes.
            .ToDictionary(cells => cells[1], cells => cells);

    [Fact]
    public void The_spec_still_lists_features()
    {
        // A rename of the spec file would otherwise make this whole suite pass vacuously.
        Assert.True(SpecFeatures().Count > 50);
    }

    [Fact]
    public void Every_feature_in_the_spec_is_documented()
    {
        var documented = DocumentedFeatures();
        var missing = SpecFeatures().Keys.Where(id => !documented.ContainsKey(id)).ToList();

        Assert.True(missing.Count == 0,
            $"docs/features.md is missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_documented_feature_has_a_usable_status()
    {
        foreach (var (id, cells) in DocumentedFeatures())
        {
            var status = cells[3];

            Assert.True(
                status == "done"
                || status.StartsWith("partial: ", StringComparison.Ordinal)
                || status.StartsWith("not-supported: ", StringComparison.Ordinal),
                $"{id} has the status '{status}', which is neither done, partial: … nor not-supported: …");

            // "partial" without saying what is missing is the status that rots.
            if (status != "done") Assert.True(status.Split(':', 2)[1].Length > 10,
                $"{id} says '{status}' without explaining what is missing");
        }
    }

    [Fact]
    public void Every_documented_feature_names_engines_and_a_place_in_the_ui()
    {
        foreach (var (id, cells) in DocumentedFeatures())
        {
            Assert.False(string.IsNullOrWhiteSpace(cells[4]), $"{id} names no engines");
            Assert.False(string.IsNullOrWhiteSpace(cells[5]), $"{id} says nowhere in the UI it lives");
        }
    }

    [Fact]
    public void No_documented_feature_is_unknown_to_the_spec()
    {
        var spec = SpecFeatures();
        var extra = DocumentedFeatures().Keys.Where(id => !spec.ContainsKey(id)).ToList();

        Assert.True(extra.Count == 0,
            $"docs/features.md lists ids the spec does not: {string.Join(", ", extra)}");
    }
}
