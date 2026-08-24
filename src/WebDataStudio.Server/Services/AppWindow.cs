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

    /// The browsers that take `--app=`, in the order they are tried.
    ///
    /// Chrome and Brave before Edge, and not for taste: Edge decorates a fresh profile with its own
    /// offers — sign in, set up, a sidebar — and an app window is a poor place to be sold something.
    /// Edge stays on the list because on Windows it is the one that is certainly installed.
    public static IReadOnlyList<string> Candidates() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ?
            [
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            ]
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ?
                [
                    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
                    "/Applications/Chromium.app/Contents/MacOS/Chromium",
                    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
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

                // This is one window showing one page, not a browser somebody is setting up. A
                // fresh profile otherwise arrives with a welcome tab, an extensions tour and an
                // offer to become the default browser — none of which belong in front of a studio.
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-extensions",
                "--disable-default-apps",
                "--disable-sync",
                "--no-service-autorun",
                // Everything a browser would otherwise like to tell you about itself while you are
                // trying to read a table. The msEdge* names are Edge's own.
                "--disable-features=Translate,MediaRouter,OptimizationHints," +
                "InterestFeedContentSuggestions,msEdgeWelcomePage,msFirstRunExperience," +
                "msEdgeSplashScreen,msEdgeSidebar,msEdgeShoppingAssistant,msEdgeCollections," +
                "msEdgeDiscoverFeature,msIdentityFlyout,msImplicitSignin",
                "--disable-background-mode",
                "--disable-component-update",
            ]);
        }

        return null;
    }

    /// The file Chromium looks for to decide whether this profile has been used before. Writing it
    /// ourselves is what actually stops the first-run pages: the switches ask for silence, the
    /// sentinel means there is nothing to be noisy about.
    ///
    /// Returns false when the directory cannot be prepared, which is not worth failing over — a
    /// welcome tab is a blemish, not a broken studio.
    public static bool QuietenFirstRun(string profileDirectory)
    {
        try
        {
            Directory.CreateDirectory(profileDirectory);

            var sentinel = Path.Combine(profileDirectory, "First Run");
            if (!File.Exists(sentinel)) File.WriteAllText(sentinel, "");

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// Opens the studio the best way this machine allows. Returns what it did, for the log.
    public static string Open(string url, string profileDirectory, ILogger logger)
    {
        QuietenFirstRun(profileDirectory);

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
