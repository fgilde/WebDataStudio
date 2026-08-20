using System.Text.RegularExpressions;

namespace WebDataStudio.Server.Services;

/// Which columns are masked, and where that decision comes from.
public sealed record MaskPolicy(
    bool MaskByDefault,
    IReadOnlySet<string> Extra,
    IReadOnlySet<string> Never)
{
    public static MaskPolicy Default { get; } =
        new(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// What a column called `password` almost certainly holds. A studio that shows every secret in
/// plain text on a shared screen is a studio people stop opening in front of others — and an export
/// that carries them out of the building is worse.
///
/// The heuristic is deliberately narrow: it matches whole words inside a name, so `password` and
/// `userPassword` are secrets while `password_changed_at` is a timestamp and stays visible. A false
/// positive costs one click to reveal; a false negative leaks.
public static partial class SensitiveColumns
{
    /// The mask itself is fixed-length, so it cannot leak the length of what it hides.
    public const string Mask = "••••••••";

    private static readonly string[] Words =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "privatekey",
        "private_key", "credential", "credentials", "iban", "bic", "ssn", "socialsecurity",
        "creditcard", "credit_card", "cardnumber", "card_number", "cvv", "cvc", "pin",
    ];

    /// Words that make a name a *fact about* a secret rather than the secret: a timestamp, a flag,
    /// a hash algorithm's name, an attempt counter.
    private static readonly string[] Qualifiers =
    [
        "changed", "updated", "created", "expires", "expiry", "expired", "attempts", "count",
        "required", "reset", "algorithm", "algo", "policy", "history", "at", "on", "date", "flag",
    ];

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex Separators();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    /// True when the column's name says it holds a secret. Case and separators are ignored, so
    /// `PasswordHash`, `password_hash` and `PASSWORD-HASH` all match.
    public static bool IsSensitive(string column)
    {
        var parts = Split(column);
        if (parts.Count == 0) return false;

        // A word can be spelled across separators — api_key, card number, private key — so a run of
        // up to three parts is joined before it is compared.
        for (var start = 0; start < parts.Count; start++)
        {
            for (var length = Math.Min(3, parts.Count - start); length >= 1; length--)
            {
                var candidate = string.Concat(parts.Skip(start).Take(length));
                if (!Words.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue;

                // `password_changed_at` is when it changed, not the password. `password_hash` is
                // still the secret: a hash is a credential for anything that accepts it.
                var qualified = parts
                    .Skip(start + length)
                    .Any(part => Qualifiers.Contains(part, StringComparer.OrdinalIgnoreCase));

                return !qualified;
            }
        }

        return false;
    }

    /// The policy's answer for one column: the explicit lists win over the heuristic, because
    /// somebody who wrote them down knows their schema better than a word list does.
    public static bool ShouldMask(string column, MaskPolicy policy)
    {
        if (policy.Never.Contains(column)) return false;
        if (policy.Extra.Contains(column)) return true;

        return policy.MaskByDefault && IsSensitive(column);
    }

    private static List<string> Split(string column) =>
        [.. Separators()
            .Split(CamelBoundary().Replace(column, " "))
            .Where(part => part.Length > 0)];
}
