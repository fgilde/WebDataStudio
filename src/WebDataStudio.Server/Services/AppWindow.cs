using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WebDataStudio.Server.Services;

/// How the desktop build shows itself. A downloaded binary that opens a browser tab looks like a
/// website with an address bar over it; the same page in a Chromium "app" window looks like an
/// application, which is what somebody who downloaded an application expects.
///
/// No new dependency and no bundled browser: this asks the browser that is already installed for a
/// window without the chrome. When there is none, the studio opens a normal tab and says so — a
/// missing window is a cosmetic loss, not a reason to fail to start.
public static class AppWindow
{
    /// What to run, or null when nothing suitable is installed.
    public sealed record Launch(string File, IReadOnlyList<string> Arguments);

    /// The browsers that take `--app=`, in the order they are tried. Edge first on Windows because
    /// it is the one that is certainly there.
    public static IReadOnlyList<string> Candidates() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ?
            [
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
            ]
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ?
                [
                    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                    "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
                    "/Applications/Chromium.app/Contents/MacOS/Chromium",
                ]
                :
                [
                    "/usr/bin/google-chrome",
                    "/usr/bin/chromium",
                    "/usr/bin/chromium-browser",
                    "/usr/bin/microsoft-edge",
                    "/usr/bin/brave-browser",
                ];

    /// The command that opens `url` as its own window, or null when no browser was found.
    ///
    /// <paramref name="exists"/> is how the filesystem is asked, so this can be tested without one.
    /// <paramref name="profileDirectory"/> keeps the window out of the browser's normal session: a
    /// running browser would otherwise answer `--app=` by opening a tab in itself, which is the very
    /// thing being avoided.
    public static Launch? Command(string url, string profileDirectory,
        Func<string, bool>? exists = null, IReadOnlyList<string>? candidates = null)
    {
        exists ??= File.Exists;

        foreach (var browser in candidates ?? Candidates())
        {
            if (!exists(browser)) continue;

            return new Launch(browser,
            [
                $"--app={url}",
                $"--user-data-dir={profileDirectory}",
                "--window-size=1500,950",
                // The studio is one window, not a place to browse from: no first-run pages, no
                // "make me your default", no session restore prompt after a crash.
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-features=Translate,MediaRouter",
            ]);
        }

        return null;
    }

    /// Opens the studio the best way this machine allows. Returns what it did, for the log.
    public static string Open(string url, string profileDirectory, ILogger logger)
    {
        var launch = Command(url, profileDirectory);

        if (launch is not null)
        {
            try
            {
                var start = new ProcessStartInfo(launch.File) { UseShellExecute = false };
                foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);

                Process.Start(start);
                return $"opened {url} as an app window";
            }
            catch (Exception e)
            {
                // Installed but unwilling. A tab is still better than nothing.
                logger.LogDebug("could not open an app window with {Browser}: {Reason}",
                    launch.File, e.Message);
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return $"opened {url} in your browser";
        }
        catch (Exception e)
        {
            return $"open {url} in your browser ({e.Message})";
        }
    }
}
