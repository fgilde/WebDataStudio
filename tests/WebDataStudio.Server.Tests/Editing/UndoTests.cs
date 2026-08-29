using WebDataStudio.Server.Editing;

namespace WebDataStudio.Server.Tests.Editing;

/// The inverse of a change set, built from the rows as they were. Pure, so the awkward cases are
/// cheap to state: a generated key, an update that touched one column of many, a row that vanished.
public class UndoTests
{
    private static ChangeSet Set(params RowChange[] changes) =>
        new("conn", "Table:main/people", changes);

    private static Dictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value);

    private static Dictionary<int, IReadOnlyDictionary<string, object?>> Nothing() => [];

    [Fact]
    public void An_update_addressed_by_where_the_row_is_cannot_be_undone()
    {
        // The write moves the address, so the inverse would find nothing — or, after a vacuum,
        // whatever ended up there since. Offering that undo would be worse than not offering one.
        var set = Set(new RowChange("update",
            Row((RowIdentity.AddressColumn, "(0,7)")), Row(("name", "Grace"))));

        var before = new Dictionary<int, IReadOnlyDictionary<string, object?>>
        {
            [0] = Row(("name", "Ada")),
        };

        Assert.Empty(Undo.BuildInverse(set, [RowIdentity.AddressColumn], before));
    }

    [Fact]
    public void A_delete_of_such_a_row_still_comes_back_whole()
    {
        // The inverse of a delete is an insert of the row itself: no address involved.
        var set = Set(new RowChange("delete", Row((RowIdentity.AddressColumn, "(0,7)")), Row()));
        var before = new Dictionary<int, IReadOnlyDictionary<string, object?>>
        {
            [0] = Row(("name", "Ada"), ("city", "London")),
        };

        var change = Assert.Single(Undo.BuildInverse(set, [RowIdentity.AddressColumn], before));
        Assert.Equal("insert", change.Kind);
        Assert.Equal("Ada", change.Values["name"]);
    }

    [Fact]
    public void An_update_goes_back_to_the_values_that_were_there()
    {
        var set = Set(new RowChange("update", Row(("id", 1)), Row(("name", "Grace"))));
        var before = new Dictionary<int, IReadOnlyDictionary<string, object?>>
        {
            [0] = Row(("id", 1), ("name", "Ada"), ("city", "London")),
        };

        var inverse = Undo.BuildInverse(set, ["id"], before);

        var change = Assert.Single(inverse);
        Assert.Equal("update", change.Kind);
        Assert.Equal(1, change.Key["id"]);
        // Only the column that was written: undoing must not overwrite what nobody touched.
        Assert.Equal(new[] { "name" }, change.Values.Keys);
        Assert.Equal("Ada", change.Values["name"]);
    }

    [Fact]
    public void An_inserts_inverse_is_a_delete_by_its_key()
    {
        var set = Set(new RowChange("insert", Row(), Row(("id", 7), ("name", "Ada"))));

        var change = Assert.Single(Undo.BuildInverse(set, ["id"], Nothing()));

        Assert.Equal("delete", change.Kind);
        Assert.Equal(7, change.Key["id"]);
        Assert.Empty(change.Values);
    }

    /// A key the database generated is not in the request. Deleting by a guess would delete
    /// somebody else's row, so the step simply cannot be undone.
    [Fact]
    public void An_insert_without_its_key_cannot_be_undone()
    {
        var set = Set(new RowChange("insert", Row(), Row(("name", "Ada"))));

        Assert.Empty(Undo.BuildInverse(set, ["id"], Nothing()));
    }

    [Fact]
    public void A_deletes_inverse_re_inserts_every_column()
    {
        var set = Set(new RowChange("delete", Row(("id", 3)), Row()));
        var before = new Dictionary<int, IReadOnlyDictionary<string, object?>>
        {
            [0] = Row(("id", 3), ("name", "Ada"), ("city", null)),
        };

        var change = Assert.Single(Undo.BuildInverse(set, ["id"], before));

        Assert.Equal("insert", change.Kind);
        Assert.Equal(3, change.Values["id"]);
        Assert.Equal("Ada", change.Values["name"]);
        Assert.Null(change.Values["city"]);
    }

    /// The row was gone by the time it was read — a concurrent delete, or a key that never matched.
    /// Guessing what to restore is worse than leaving that change out.
    [Fact]
    public void A_row_that_could_not_be_read_is_skipped()
    {
        var set = Set(
            new RowChange("delete", Row(("id", 3)), Row()),
            new RowChange("update", Row(("id", 4)), Row(("name", "x"))));

        Assert.Empty(Undo.BuildInverse(set, ["id"], Nothing()));
    }

    [Fact]
    public void Several_changes_each_get_their_inverse()
    {
        var set = Set(
            new RowChange("update", Row(("id", 1)), Row(("name", "new"))),
            new RowChange("update", Row(("id", 2)), Row(("name", "new"))));
        var before = new Dictionary<int, IReadOnlyDictionary<string, object?>>
        {
            [0] = Row(("id", 1), ("name", "one")),
            [1] = Row(("id", 2), ("name", "two")),
        };

        var inverse = Undo.BuildInverse(set, ["id"], before);

        Assert.Equal(2, inverse.Count);
        Assert.Equal("one", inverse[0].Values["name"]);
        Assert.Equal("two", inverse[1].Values["name"]);
    }

    [Fact]
    public void The_label_counts_the_kinds()
    {
        var set = Set(
            new RowChange("update", Row(("id", 1)), Row(("name", "a"))),
            new RowChange("update", Row(("id", 2)), Row(("name", "b"))),
            new RowChange("delete", Row(("id", 3)), Row()));

        Assert.Equal("1 delete, 2 updates", Undo.Describe(set));
    }
}
