using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Export;
using WebDataStudio.Server.Drivers.PostgreSql;
using static WebDataStudio.Server.Tests.Export.DelimitedExporterTests;

namespace WebDataStudio.Server.Tests.Export;

/// An export format somebody wrote themselves. DataGrip's extractors are Groovy, which makes an
/// export format a program; these are three pieces of text with placeholders and nothing to run.
public class TemplateExporterTests
{
    private static ExportTemplate Template(string row, string? header = null, string? footer = null,
        string separator = ", ") =>
        new("mine", "Mine", "txt", "text/plain", header, row, footer, separator);

    private static Task<string> RunAsync(ExportTemplate template, ExportOptions? options = null) =>
        ExportAsync(new TemplateExporter(template),
            options ?? ExportOptions.Default with { TableName = "people" });

    [Fact]
    public async Task A_template_writes_its_header_its_rows_and_its_footer()
    {
        var text = await RunAsync(Template(
            header: "-- {{table}}: {{columns}}\n", row: "{{index}}: {{values}}\n", footer: "-- done\n"));

        Assert.Equal(
            "-- people: id, name\n1: 1, ada\n2: 2, \n3: 3, say \"hi\"\n-- done\n",
            text);
    }

    [Fact]
    public async Task A_column_is_addressed_by_name()
    {
        var text = await RunAsync(Template("{{col.name}} has id {{col.id}}\n"));

        Assert.Equal("ada has id 1\n has id 2\nsay \"hi\" has id 3\n", text);
    }

    [Fact]
    public async Task A_column_the_result_does_not_have_says_so_in_the_output()
    {
        // Better than failing halfway through a file somebody is already downloading.
        var text = await RunAsync(Template("{{col.city}}\n"));

        Assert.Contains("unknown column city", text);
    }

    [Fact]
    public async Task The_comma_placeholder_is_dropped_on_the_last_row()
    {
        var text = await RunAsync(Template(
            header: "INSERT INTO {{table}} ({{columns}}) VALUES\n",
            row: "  ({{values|sql}}){{comma}}\n", footer: ";\n"));

        Assert.Equal(
            "INSERT INTO people (id, name) VALUES\n  ('1', 'ada'),\n  ('2', ''),\n  ('3', 'say \"hi\"')\n;\n",
            text);
        Assert.DoesNotContain("comma", text);
    }

    [Theory]
    [InlineData("json", "\"say \\\"hi\\\"\"")]
    [InlineData("csv", "\"say \"\"hi\"\"\"")]
    [InlineData("html", "say &quot;hi&quot;")]
    [InlineData("upper", "SAY \"HI\"")]
    public async Task A_filter_escapes_the_value_the_way_that_format_needs(string filter, string expected)
    {
        var text = await RunAsync(Template($"{{{{col.name|{filter}}}}}\n"));

        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task The_sql_filter_quotes_with_the_target_dialect()
    {
        var text = await RunAsync(Template("{{col.name|sql}}\n"),
            ExportOptions.Default with { Dialect = new PostgreSqlDriver().Dialect });

        Assert.Contains("'say \"hi\"'", text);
    }

    [Fact]
    public async Task A_separator_of_its_own_joins_the_values()
    {
        var text = await RunAsync(Template("{{values}}\n", separator: " | "));

        Assert.Contains("1 | ada", text);
    }

    [Fact]
    public async Task An_unknown_placeholder_is_left_alone_rather_than_swallowed()
    {
        // A typo in a template should be visible in the output, not silently produce nothing.
        var text = await RunAsync(Template("{{whatever}}\n"));

        Assert.Contains("{{whatever}}", text);
    }
}

/// Where the templates come from, and which of them can be changed here.
public class ExportTemplateStoreTests
{
    private static readonly ExportTemplate Mine =
        new("mine", "Mine", "txt", "text/plain", null, "{{values}}\n", null);

    private static ExportTemplates Store(string? directory, ref string? stored)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WDS_EXPORT_TEMPLATES_DIR"] = directory,
            }).Build();

        var box = new StoredJson { Value = stored };
        var templates = new ExportTemplates(config, () => box.Value, json => box.Value = json);
        stored = box.Value;

        return templates;
    }

    private sealed class StoredJson { public string? Value { get; set; } }

    [Fact]
    public void A_template_saved_here_comes_back()
    {
        string? stored = null;
        var box = new StoredJson();
        var templates = new ExportTemplates(new ConfigurationBuilder().Build(),
            () => box.Value, json => box.Value = json);

        templates.Save(Mine);

        Assert.Equal("Mine", templates.Find("mine")?.Label);
        Assert.Contains("mine", box.Value);
        _ = stored;
    }

    [Fact]
    public void And_can_be_deleted_again()
    {
        var box = new StoredJson();
        var templates = new ExportTemplates(new ConfigurationBuilder().Build(),
            () => box.Value, json => box.Value = json);

        templates.Save(Mine);
        templates.Delete("mine");

        Assert.Null(templates.Find("mine"));
    }

    [Fact]
    public void A_template_the_deployment_mounted_is_read_only_here()
    {
        var directory = Directory.CreateTempSubdirectory("wds-templates").FullName;

        try
        {
            File.WriteAllText(Path.Combine(directory, "ours.json"), """
                {
                  "id": "ours", "label": "Ours", "extension": "txt", "contentType": "text/plain",
                  "row": "{{values}}\n"
                }
                """);

            var box = new StoredJson();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WDS_EXPORT_TEMPLATES_DIR"] = directory,
                }).Build();

            var templates = new ExportTemplates(config, () => box.Value, json => box.Value = json);

            Assert.Equal("Ours", templates.Find("ours")?.Label);

            // It belongs to the deployment; a copy under another name is the honest way to change it.
            Assert.Throws<InvalidOperationException>(() =>
                templates.Save(Mine with { Id = "ours" }));
        }
        finally
        {
            TestDirectory.Remove(directory);
        }
    }

    [Fact]
    public void A_template_is_an_export_format_like_any_other()
    {
        var box = new StoredJson();
        var templates = new ExportTemplates(new ConfigurationBuilder().Build(),
            () => box.Value, json => box.Value = json);

        templates.Save(Mine);

        var registry = new ExporterRegistry(templates);

        Assert.Contains("template:mine", registry.All().Select(exporter => exporter.Format));
        Assert.Equal("Mine", registry.Get("template:mine").Label);
        Assert.Throws<NotSupportedException>(() => registry.Get("template:nope"));
    }

    [Fact]
    public void A_studio_with_no_templates_still_has_every_built_in_format()
    {
        var registry = new ExporterRegistry();

        Assert.Contains("csv", registry.All().Select(exporter => exporter.Format));
        Assert.Equal("CSV", registry.Get("csv").Label);
    }
}
