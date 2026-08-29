using System.Text.Json;

namespace WebDataStudio.Server.Services;

/// The mask policy per connection. Stored in the workspace so it survives a restart, and read
/// through a cache because every browse and every query asks for it.
///
/// A studio whose storage is unavailable falls back to the default policy — masking on — rather
/// than to "show everything", because the safe direction of that failure is obvious.
public sealed class MaskPolicyStore(WorkspaceStore workspace, IConfiguration config)
{
    private const string Prefix = "mask-policy:";

    private sealed record Stored(bool MaskByDefault, string[] Extra, string[] Never);

    /// The deployment's own answer, from the environment: `WDS_MASK_EXTRA` names columns to mask
    /// whatever the word list thinks, `WDS_MASK_NEVER` names columns to leave alone, and
    /// `WDS_MASK_DEFAULT=false` turns the heuristic off and leaves only those two lists.
    ///
    /// This is the baseline. What somebody set from the column menu wins over it for that column,
    /// because they were looking at the data at the time.
    /// What a deployment can ship as a file instead of as three variables: the same three things,
    /// in a shape a long list is bearable in.
    private sealed record MaskFile(bool? MaskByDefault, string[]? Extra, string[]? Never);

    public MaskPolicy Baseline
    {
        get
        {
            var file = ShippedFiles.ReadOne<MaskFile>(config["WDS_MASK_FILE"], what: "masking file");

            var byDefault = !string.Equals(config["WDS_MASK_DEFAULT"], "false",
                                StringComparison.OrdinalIgnoreCase)
                            && file?.MaskByDefault != false;

            // Both sources count: a file and a variable are two ways of saying the same thing, and a
            // deployment that uses both means both.
            var extra = Names(config["WDS_MASK_EXTRA"]);
            var never = Names(config["WDS_MASK_NEVER"]);

            foreach (var column in file?.Extra ?? []) extra.Add(column);
            foreach (var column in file?.Never ?? []) never.Add(column);

            return new MaskPolicy(byDefault, extra, never);
        }
    }

    public MaskPolicy For(string connectionId)
    {
        var baseline = Baseline;

        try
        {
            var json = workspace.LoadItem($"{Prefix}{connectionId}");
            if (json is null) return baseline;

            var stored = JsonSerializer.Deserialize<Stored>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (stored is null) return baseline;

            var extra = new HashSet<string>(stored.Extra ?? [], StringComparer.OrdinalIgnoreCase);
            var never = new HashSet<string>(stored.Never ?? [], StringComparer.OrdinalIgnoreCase);

            // The union of both, with the stored side winning where the two disagree: "never mask
            // this" clicked in the UI has to beat WDS_MASK_EXTRA, and the other way round.
            foreach (var column in baseline.Extra)
                if (!never.Contains(column)) extra.Add(column);

            foreach (var column in baseline.Never)
                if (!extra.Contains(column)) never.Add(column);

            return new MaskPolicy(stored.MaskByDefault, extra, never);
        }
        catch (Exception)
        {
            return baseline;
        }
    }

    private static HashSet<string> Names(string? value) =>
        new((value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    public void Save(string connectionId, MaskPolicy policy) =>
        workspace.SaveItem($"{Prefix}{connectionId}", JsonSerializer.Serialize(
            new Stored(policy.MaskByDefault, [.. policy.Extra], [.. policy.Never])));
}
