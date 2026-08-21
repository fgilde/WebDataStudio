using System.Text.Json;
using WebDataStudio.Server.Editing;

namespace WebDataStudio.Server.Services;

/// The undo stack per connection, kept in the workspace store so it survives a reload of the page
/// and a restart of the studio.
///
/// A studio whose storage is unavailable has no undo — it says so rather than offering a button
/// that would silently do nothing.
public sealed class UndoStore(WorkspaceStore workspace)
{
    /// Deep enough for a session of editing, shallow enough that the workspace row stays small.
    public const int Depth = 20;

    private const string Prefix = "undo:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<UndoEntry> List(string connectionId)
    {
        try
        {
            var json = workspace.LoadItem($"{Prefix}{connectionId}");
            return json is null ? [] : JsonSerializer.Deserialize<List<UndoEntry>>(json, Json) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public UndoEntry? Newest(string connectionId, string objectRef) =>
        List(connectionId).FirstOrDefault(e => e.ObjectRef == objectRef);

    /// True when the step was recorded. False means this change cannot be undone, which the caller
    /// reports rather than hides.
    public bool Push(string connectionId, UndoEntry entry)
    {
        try
        {
            var entries = List(connectionId).ToList();
            entries.Insert(0, entry);
            Write(connectionId, entries.Take(Depth));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// Removes one entry once its inverse has actually been applied, so undoing twice cannot undo
    /// a step that is already undone.
    public void Consume(string connectionId, string entryId)
    {
        try
        {
            Write(connectionId, List(connectionId).Where(e => e.Id != entryId));
        }
        catch (Exception)
        {
            // Storage gone; the entry stays and its apply will fail on its own terms.
        }
    }

    private void Write(string connectionId, IEnumerable<UndoEntry> entries) =>
        workspace.SaveItem($"{Prefix}{connectionId}", JsonSerializer.Serialize(entries.ToList(), Json));
}
