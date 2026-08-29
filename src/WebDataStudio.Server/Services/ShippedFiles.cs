using System.Text.Json;

namespace WebDataStudio.Server.Services;

/// What a deployment brings with it, read from JSON.
///
/// The studio already read four of these from a folder — saved queries, export templates, quality
/// rules, seed scripts. These are the rest of them: connections, the masking baseline, dashboards,
/// snippets and the preferences a studio starts with. Same deal every time: the file belongs to the
/// deployment, the studio reads it and cannot change it, and a broken file is a line in the log
/// rather than a studio that will not start.
public static class ShippedFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// Every item of every file the setting names. A file holding a list contributes its items; one
    /// holding a single object contributes that object.
    public static IReadOnlyList<T> Read<T>(string? setting, ILogger? log = null, string what = "file")
    {
        var items = new List<T>();

        foreach (var file in ConfiguredPaths.Files(setting, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text)) continue;

                using var document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                if (document.RootElement.ValueKind == JsonValueKind.Array)
                    items.AddRange(document.RootElement.Deserialize<List<T>>(Json) ?? []);
                else if (document.RootElement.Deserialize<T>(Json) is { } single)
                    items.Add(single);
            }
            catch (Exception e)
            {
                // One bad file must not take the others with it, and must never stop the studio.
                log?.LogWarning(e, "could not read the {What} {File}", what, file);
            }
        }

        return items;
    }

    /// The first object the setting names, for something a deployment has one of.
    public static T? ReadOne<T>(string? setting, ILogger? log = null, string what = "file")
        where T : class =>
        Read<T>(setting, log, what).FirstOrDefault();
}
