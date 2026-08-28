using System.Security.Cryptography;
using System.Text;

namespace WebDataStudio.Server.Services;

/// One studio account. `Connections` empty means "all of them"; a non-empty set is a whitelist of
/// connection names or ids.
public sealed record StudioUser(
    string Name, string Secret, string Role, IReadOnlySet<string> Connections)
{
    public bool IsAdmin => Role == UserRoles.Admin;

    /// A viewer never writes, whatever a connection says about itself.
    public bool ReadOnly => Role == UserRoles.Viewer;

    public bool MaySee(string connectionId, string connectionName) =>
        Connections.Count == 0
        || Connections.Contains(connectionId)
        || Connections.Contains(connectionName);
}

public static class UserRoles
{
    public const string Admin = "admin";
    public const string Editor = "editor";
    public const string Viewer = "viewer";

    public static readonly string[] All = [Admin, Editor, Viewer];

    public static string Normalise(string? role)
    {
        var trimmed = role?.Trim().ToLowerInvariant();
        return All.Contains(trimmed) ? trimmed! : Viewer;
    }
}

/// Who may sign in, with which role, and which connections they see.
///
/// Accounts are deployment configuration, not stored state: they come from the environment, so a
/// container rollout is the only way to change them and nobody can grant themselves a role through
/// the UI. `WDS_USERS` holds `name:role:secret[:conn,conn]` entries separated by `;`, and the
/// single-account `WDS_USER`/`WDS_PASSWORD` pair keeps working as an admin.
public sealed class UserStore
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// A hash of nothing anybody knows, verified against when no account matched.
    private static readonly Lazy<string> Decoy = new(() => Hash(Guid.NewGuid().ToString()));

    private readonly List<StudioUser> _users;

    public UserStore(IReadOnlyList<StudioUser> users, bool external = false)
    {
        _users = [.. users];
        External = external;
    }

    /// An identity provider decides who may sign in, rather than a list in the environment.
    public bool External { get; }

    /// Nowhere to sign in from: the studio runs open and never shows a login screen. A provider
    /// counts, or configuring one would leave a studio wide open with a login button on it.
    public bool Anonymous => _users.Count == 0 && !External;

    public IReadOnlyList<StudioUser> All => _users;

    public static UserStore FromConfiguration(IConfiguration config)
    {
        var users = Parse(config["WDS_USERS"]);

        // The single-account variables predate the list and still mean "one admin".
        if (users.Count == 0)
        {
            var legacy = AuthOptions.FromConfiguration(config);
            if (!legacy.Anonymous)
                users.Add(new StudioUser(legacy.Username!, legacy.Password!, UserRoles.Admin,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }

        return new UserStore(users, OidcOptions.FromConfiguration(config).Enabled);
    }

    public static List<StudioUser> Parse(string? value)
    {
        var users = new List<StudioUser>();
        if (string.IsNullOrWhiteSpace(value)) return users;

        foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                               StringSplitOptions.TrimEntries))
        {
            // name:role:secret[:conn,conn] — the secret may contain no colon, which is why a
            // PBKDF2 hash uses '$' as its own separator.
            var parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || parts[0].Length == 0 || parts[2].Length == 0) continue;

            var connections = parts.Length > 3
                ? parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            users.Add(new StudioUser(parts[0], parts[2], UserRoles.Normalise(parts[1]),
                new HashSet<string>(connections, StringComparer.OrdinalIgnoreCase)));
        }

        return users;
    }

    /// The account for these credentials, or null. Every candidate is checked, and an unknown name
    /// still costs one hash, so a wrong name and a wrong password take the same time.
    public StudioUser? Verify(string username, string password)
    {
        StudioUser? found = null;

        foreach (var user in _users)
        {
            var nameOk = FixedTimeEquals(user.Name, username);
            var secretOk = VerifySecret(user.Secret, password);
            if (nameOk && secretOk) found = user;
        }

        // Nothing matched: burn one verification anyway, so an unknown name is not the fast path.
        if (found is null) VerifySecret(Decoy.Value, password);

        return found;
    }

    /// A `pbkdf2$iterations$salt$hash` string is verified as a hash; anything else is compared as a
    /// literal password, which is what the single-account variables have always done.
    public static bool VerifySecret(string secret, string password)
    {
        if (!secret.StartsWith("pbkdf2$", StringComparison.Ordinal))
            return FixedTimeEquals(secret, password);

        var parts = secret.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations)) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// The hash to put in `WDS_USERS`. Also what the tests use, so the format is exercised.
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
