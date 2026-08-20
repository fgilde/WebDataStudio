using System.Text.Json;

namespace WebDataStudio.Server.Services;

/// The mask policy per connection. Stored in the workspace so it survives a restart, and read
/// through a cache because every browse and every query asks for it.
///
/// A studio whose storage is unavailable falls back to the default policy — masking on — rather
/// than to "show everything", because the safe direction of that failure is obvious.
public sealed class MaskPolicyStore(WorkspaceStore workspace)
{
    private const string Prefix = "mask-policy:";

    private sealed record Stored(bool MaskByDefault, string[] Extra, string[] Never);

    public MaskPolicy For(string connectionId)
    {
        try
        {
            var json = workspace.LoadItem($"{Prefix}{connectionId}");
            if (json is null) return MaskPolicy.Default;

            var stored = JsonSerializer.Deserialize<Stored>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (stored is null) return MaskPolicy.Default;

            return new MaskPolicy(
                stored.MaskByDefault,
                new HashSet<string>(stored.Extra ?? [], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(stored.Never ?? [], StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return MaskPolicy.Default;
        }
    }

    public void Save(string connectionId, MaskPolicy policy) =>
        workspace.SaveItem($"{Prefix}{connectionId}", JsonSerializer.Serialize(
            new Stored(policy.MaskByDefault, [.. policy.Extra], [.. policy.Never])));
}
