using System.Text.RegularExpressions;

namespace WebDataStudio.Server.Services;

/// Hides the password in a connection string so it can be shown on screen. The full string is a
/// separate, deliberate request — see the properties endpoint.
public static partial class ConnectionSecret
{
    private const string Mask = "••••••••";

    /// Key-value form: `Password=x`, `Pwd=x`, and the passphrase of a client certificate.
    [GeneratedRegex(@"(?<key>\b(?:password|pwd|passphrase)\s*=\s*)(?<value>[^;]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValuePassword();

    /// URL form: `postgres://user:secret@host`.
    [GeneratedRegex(@"(?<prefix>://[^:/?#@]+:)(?<value>[^@/?#]*)(?<suffix>@)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlPassword();

    /// The connection string with every password replaced by a mask of fixed length, so the real
    /// length cannot be read off the screen either.
    public static string Hide(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return connectionString;

        var masked = KeyValuePassword().Replace(connectionString,
            match => match.Groups["value"].Value.Length == 0
                ? match.Value
                : match.Groups["key"].Value + Mask);

        return UrlPassword().Replace(masked,
            match => match.Groups["value"].Value.Length == 0
                ? match.Value
                : match.Groups["prefix"].Value + Mask + match.Groups["suffix"].Value);
    }

    /// True when the string carries a password at all — the UI only offers to reveal one then.
    public static bool HasPassword(string connectionString) =>
        !string.IsNullOrEmpty(connectionString)
        && !string.Equals(Hide(connectionString), connectionString, StringComparison.Ordinal);
}
