namespace WebDataStudio.Server.Services;

/// The icon a deployment wants instead of ours, from `WDS_ICON`.
///
/// Two shapes, because both are what people have: a URL somewhere else, which the browser can fetch
/// itself, and a file mounted into the container, which it cannot — so the studio serves that one at
/// `/api/brand/icon`. Which of the two a value is, is decided by whether it is a file that exists,
/// not by guessing at its shape: a container path and a root-relative URL look identical.
public sealed class StudioBrand
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
    };

    public StudioBrand(IConfiguration configuration)
    {
        var value = configuration["WDS_ICON"]?.Trim();
        if (string.IsNullOrEmpty(value)) return;

        // A path that is not there is a typo in a deployment, not a reason to refuse to start. The
        // value travels as given and the browser falls back to the shipped icon.
        if (File.Exists(value))
        {
            FilePath = Path.GetFullPath(value);
            ContentType = Types.GetValueOrDefault(Path.GetExtension(FilePath), "application/octet-stream");
            Icon = "/api/brand/icon";
        }
        else
        {
            Icon = value;
        }
    }

    /// What the browser is told to load, or null when this studio uses the shipped icon.
    public string? Icon { get; }

    /// The file to serve at `/api/brand/icon`, or null when there is nothing to serve.
    public string? FilePath { get; }

    public string ContentType { get; } = "application/octet-stream";
}
