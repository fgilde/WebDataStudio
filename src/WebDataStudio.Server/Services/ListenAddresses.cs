using System.Net;

namespace WebDataStudio.Server.Services;

/// Whether the studio is reachable from anywhere but this machine. An open studio on loopback is a
/// tool on a laptop; the same studio on a published port hands whoever reaches it everything,
/// the admin surface included.
public static class ListenAddresses
{
    /// True when every address in <paramref name="urls"/> (the `ASPNETCORE_URLS` shape, `;`-separated)
    /// is loopback. Nothing configured is Kestrel's own default, http://localhost:5000.
    public static bool LoopbackOnly(string? urls) =>
        string.IsNullOrWhiteSpace(urls)
        || urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(url => BindingAddress.Parse(url).Host.Trim('[', ']') is var host
                        && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))));
}
