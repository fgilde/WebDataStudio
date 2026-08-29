namespace WebDataStudio.Server.Services;

/// Where the rich file viewer comes from.
///
/// A bucket holds more than the browser shows by itself: a spreadsheet, a Word document, a
/// markdown file, an archive. MudEx's file display renders all of them, and it is a web component
/// with a WebAssembly runtime behind it — far too much to bundle into a studio that is mostly a
/// grid, so it is fetched when somebody first asks to look at a file, and not before.
///
/// The default is the public CDN. A studio with no way out to the internet points this at its own
/// copy instead; setting it to nothing switches the viewer off, and the built-in preview — images,
/// PDF, video, audio, text — is what remains.
public sealed record FileViewerOptions(string? ScriptUrl)
{
    public const string Default = "https://cdn.jsdelivr.net/npm/mudex-webcomponents/mudex.js";

    public bool Enabled => ScriptUrl is { Length: > 0 };

    public static FileViewerOptions FromConfiguration(IConfiguration config)
    {
        var setting = config["WDS_FILE_VIEWER_URL"];

        // Unset means the default; set-but-empty means somebody said no on purpose.
        return setting is null
            ? new FileViewerOptions(Default)
            : new FileViewerOptions(setting.Trim() is { Length: > 0 } url ? url : null);
    }
}
