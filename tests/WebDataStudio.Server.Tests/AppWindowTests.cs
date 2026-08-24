using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// How the desktop build decides to show itself. The launching is the operating system's business;
/// what is decided before that is not, and this is where it is checked.
public class AppWindowTests
{
    private static readonly string[] Browsers = ["/opt/none", "/usr/bin/chromium", "/usr/bin/edge"];

    [Fact]
    public void The_first_browser_that_is_actually_there_is_the_one_used()
    {
        var launch = AppWindow.Command("http://localhost:8080", "/data/app-window",
            path => path != "/opt/none", Browsers);

        Assert.NotNull(launch);
        Assert.Equal("/usr/bin/chromium", launch!.File);
    }

    [Fact]
    public void The_url_is_asked_for_as_an_app_rather_than_as_a_page()
    {
        var launch = AppWindow.Command("http://localhost:8080", "/data/app-window",
            _ => true, Browsers);

        // --app is the whole point: it is what leaves the address bar off.
        Assert.Contains("--app=http://localhost:8080", launch!.Arguments);
    }

    [Fact]
    public void A_profile_of_its_own_keeps_it_from_becoming_a_tab()
    {
        // A browser that is already running answers --app by opening a tab in itself unless it is
        // told to be a separate session.
        var launch = AppWindow.Command("http://localhost:8080", "/data/app-window",
            _ => true, Browsers);

        Assert.Contains("--user-data-dir=/data/app-window", launch!.Arguments);
    }

    [Fact]
    public void Nothing_installed_is_an_answer_rather_than_a_guess()
    {
        Assert.Null(AppWindow.Command("http://localhost:8080", "/data/app-window",
            _ => false, Browsers));
    }

    [Fact]
    public void The_window_does_not_ask_to_become_the_default_browser()
    {
        var launch = AppWindow.Command("http://localhost:8080", "/data/app-window",
            _ => true, Browsers);

        Assert.Contains("--no-first-run", launch!.Arguments);
        Assert.Contains("--no-default-browser-check", launch.Arguments);
    }

    [Fact]
    public void The_candidates_are_the_ones_this_platform_could_have()
    {
        var candidates = AppWindow.Candidates();

        Assert.NotEmpty(candidates);
        // Whatever the platform, every candidate is an absolute path: a bare name would be resolved
        // through PATH, and PATH is not something a downloaded binary should trust for this.
        Assert.All(candidates, path => Assert.True(Path.IsPathRooted(path), path));
    }
}
