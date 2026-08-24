#if WDS_DESKTOP
using Photino.NET;

namespace WebDataStudio.Server.Services;

/// The window the downloaded build lives in. Compiled into the desktop download only: the container
/// has no display, and the native libraries behind this are not something an image should carry.
///
/// This is a window of the operating system's own — WebView2 on Windows, WKWebView on macOS,
/// WebKitGTK on Linux — so there is no address bar, no tab strip and no second browser to install.
/// When it cannot open (a Linux without WebKitGTK is the realistic case) the caller falls back to
/// asking a browser for a window, and then to a plain tab.
public static class DesktopShell
{
    /// Opens the window and blocks until somebody closes it. Must be called from the main thread:
    /// every one of these platforms insists that a window belongs to the thread that made it.
    ///
    /// Returns false when no window could be created, so the caller can fall back rather than leave
    /// the user with a server and nothing to look at.
    /// How long the window gets to fetch the page before it is judged to have failed. Generous: a
    /// cold WebView2 on a slow disk takes a moment.
    private static readonly TimeSpan LoadDeadline = TimeSpan.FromSeconds(12);

    public static bool Run(string url, string title, ILogger logger)
    {
        try
        {
            var window = new PhotinoWindow();

            // The window's own account of itself, in the log. Without this a window that opens but
            // shows nothing is indistinguishable from one that never opened — which is exactly the
            // state a missing WebView2 or WebKitGTK leaves behind.
            window.WindowCreating += (_, _) => logger.LogInformation("desktop window: creating");
            window.WindowCreated += (_, _) => logger.LogInformation("desktop window: created");
            window.WindowClosing += (_, _) =>
            {
                logger.LogInformation("desktop window: closing");
                return false;
            };

            window
                .SetTitle(title)
                .SetUseOsDefaultSize(false)
                .SetSize(1500, 950)
                .SetUseOsDefaultLocation(false)
                .Center()
                .SetResizable(true)
                // The studio is one page: a link that leaves it belongs in the user's own browser,
                // not in a window with no address bar to show where it went.
                .SetContextMenuEnabled(false)
                .Load(new Uri(url));

            // A window that never loads anything is the failure that cannot be caught: it is
            // created, it reports itself created, and it shows nothing — a missing WebView2 runtime
            // and a WebView2 that cannot start in this session both look like that. So the server
            // is asked whether it was ever read, and the window is closed if it was not.
            var loaded = false;
            using var watchdog = new Timer(_ =>
            {
                if (FirstRequest.Any) { loaded = true; return; }

                logger.LogInformation(
                    "the desktop window opened but fetched nothing in {Seconds} seconds; " +
                    "falling back to a browser", LoadDeadline.TotalSeconds);

                // Closing has to happen on the window's own thread.
                window.Invoke(window.Close);
            }, null, LoadDeadline, Timeout.InfiniteTimeSpan);

            window.WaitForClose();

            // Loaded once and then closed by a person: that is a window that worked.
            return loaded || FirstRequest.Any;
        }
        catch (Exception e)
        {
            // Photino throws for a missing WebView2 runtime on Windows and a missing libwebkit2gtk
            // on Linux. Both are somebody else's package, so this says what happened and lets the
            // browser have a turn.
            logger.LogInformation(
                "no native window ({Reason}); opening the studio in a browser instead", e.Message);
            return false;
        }
    }
}
#endif
