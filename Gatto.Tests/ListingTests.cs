using Gatto.Core.Commands;

namespace Gatto.Tests;

public class ListingTests
{
    private static readonly Func<int, Task<string>> NoPick = _ => Task.FromResult("");

    private static ListingRow Row(params string[] cells) => new("write", cells);

    [Fact]
    public void A_LISTING_WITH_NO_ROWS_THROWS_ITS_MESSAGE()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new Listing("snapshot", "restore", [], NoPick));
        Assert.Equal("a listing needs a row; answer text for an empty list", ex.Message);
    }

    [Fact]
    public void A_HEADER_ONE_WORD_LONGER_THAN_THE_COLUMNS_CONSTRUCTS_AND_AN_EQUAL_ONE_THROWS_NAMING_THE_COUNTS()
    {
        var listing = new Listing("snapshot", "restore", [Row("12 B", "14:02", "a.txt")], NoPick, ["tool", "size", "when", "file"]);
        Assert.Equal(["tool", "size", "when", "file"], listing.Header);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new Listing("snapshot", "restore", [Row("12 B", "14:02", "a.txt")], NoPick, ["size", "when", "file"]));
        Assert.Equal("a listing header names every cell, the label first: 3 words for 3 columns", ex.Message);
    }

    [Fact]
    public void A_HEADER_OVER_RAGGED_ROWS_IS_MEASURED_AGAINST_THE_WIDEST()
    {
        ListingRow[] rows = [Row("12 B", "14:02", "a.txt"), Row("12 B", "14:02", "10-29 09:00", "b.txt"), new("shell?")];
        var listing = new Listing("snapshot", "restore", rows, NoPick, ["tool", "size", "when", "began", "file"]);
        Assert.Equal(3, listing.Rows.Count);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new Listing("snapshot", "restore", rows, NoPick, ["tool", "size", "when", "file"]));
        Assert.Equal("a listing header names every cell, the label first: 4 words for 4 columns", ex.Message);
    }

    [Theory]
    [InlineData("", "restore")]
    [InlineData(" ", "restore")]
    [InlineData("snapshot", "")]
    public void A_BLANK_UNIT_OR_VERB_THROWS(string unit, string verb) =>
        Assert.Throws<InvalidOperationException>(() => new Listing(unit, verb, [Row()], NoPick));

    [Fact]
    public void A_NULL_PICK_ROW_LABEL_CELL_OR_HEADER_WORD_THROWS()
    {
        Assert.Throws<InvalidOperationException>(() => new Listing("snapshot", "restore", [Row()], null!));
        Assert.Throws<InvalidOperationException>(() => new Listing("snapshot", "restore", [Row(), null!], NoPick));
        Assert.Throws<InvalidOperationException>(() => new Listing("snapshot", "restore", [new(null!)], NoPick));
        Assert.Throws<InvalidOperationException>(() => new Listing("snapshot", "restore", [new("write", ["a", null!])], NoPick));
        Assert.Throws<InvalidOperationException>(() => new Listing("snapshot", "restore", [Row("a")], NoPick, ["tool", null!]));
    }

    [Fact]
    public void THE_ROWS_ARE_A_COPY_SO_THE_CALLER_S_LIST_CANNOT_EMPTY_THEM()
    {
        var rows = new List<ListingRow> { Row() };
        var listing = new Listing("snapshot", "restore", rows, NoPick);
        rows.Clear();
        Assert.Single(listing.Rows);
    }

    [Fact]
    public void A_STRING_AND_A_LISTING_EACH_CONVERT_TO_THEIR_ARM()
    {
        CommandAnswer text = "no snapshots for this folder";
        Assert.Equal("no snapshots for this folder", Assert.IsType<CommandAnswer.Text>(text).Value);

        var listing = new Listing("snapshot", "restore", [Row()], NoPick);
        CommandAnswer list = listing;
        Assert.Same(listing, Assert.IsType<CommandAnswer.List>(list).Value);
    }

    [Fact]
    public async Task OF_TEXT_WRAPS_A_TEXT_HANDLER_AND_READS_ITS_NULL_AS_EMPTY()
    {
        var row = CommandRow.OfText("/undo", "", "x", (arg, _) => Task.FromResult(arg == "" ? null! : "got " + arg), "undo.csx", "undo");
        Assert.Equal("got 2", Assert.IsType<CommandAnswer.Text>(await row.Handler("2", CancellationToken.None)).Value);
        Assert.Equal("", Assert.IsType<CommandAnswer.Text>(await row.Handler("", CancellationToken.None)).Value);
        Assert.Equal(("undo.csx", "undo"), (row.SourceFile, row.Origin));
    }

    [Fact]
    public async Task A_CONDITIONAL_OF_TEXT_OR_A_LISTING_IS_TARGET_TYPED_TO_THE_ANSWER()
    {
        var listing = new Listing("snapshot", "restore", [Row()], NoPick);
        foreach (var empty in new[] { true, false })
        {
            var answer = await Task.FromResult<CommandAnswer>(empty ? "no snapshots for this folder" : listing);
            Assert.IsType(empty ? typeof(CommandAnswer.Text) : typeof(CommandAnswer.List), answer);
        }
    }
}
