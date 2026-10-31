using Gatto.Core.Commands;
using Gatto.Repl.Commands;
using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ListingPanelTests
{
    private static readonly Func<int, Task<string>> NoPick = _ => Task.FromResult("");

    private static Listing Two(IReadOnlyList<string>? header = null) => new("snapshot", "restore",
        [
            new("write", ["12 B", "14:02", "a.txt"], "restore puts back 12 B from 14:02"),
            new("edit", ["3.1 KB", "13:50", "src/long/path/b.cs"], "restore puts back 3.1 KB from 13:50"),
        ], NoPick, header);

    [Fact]
    public void THE_FIELDS_TITLE_THE_NAME_LESS_ITS_SLASH_AND_COUNT_THE_UNIT()
    {
        var fields = ListingPanel.Fields("/undo", Two(), "coder");
        Assert.Equal("undo", fields.Title);
        Assert.Equal("2 snapshots", fields.Count);
        Assert.Equal("coder", fields.Role);
        Assert.True(fields.DigitsMove);
        Assert.Equal(["Enter restore", "Esc close"], fields.Keys.Select(k => k.Key + " " + k.Verb));

        var one = new Listing("snapshot", "restore", [new("write")], NoPick);
        Assert.Equal("1 snapshot", ListingPanel.Fields("/undo", one, null).Count);
    }

    [Fact]
    public void THE_HEADER_SPLITS_INTO_THE_LABEL_WORD_AND_THE_COLUMN_WORDS_AND_NULL_LEAVES_BOTH_NULL()
    {
        var fields = ListingPanel.Fields("/undo", Two(["tool", "size", "when", "file"]), null);
        Assert.Equal("tool", fields.LabelHeader);
        Assert.Equal(["size", "when", "file"], fields.Header);

        var bare = ListingPanel.Fields("/undo", Two(), null);
        Assert.Null(bare.LabelHeader);
        Assert.Null(bare.Header);
    }

    [Fact]
    public void THE_SENTENCE_ROW_READS_EACH_ROW_S_SENTENCE()
    {
        var under = Assert.IsType<PanelUnder.Sentence>(ListingPanel.Fields("/undo", Two(), null).Under);
        Assert.Equal("restore puts back 12 B from 14:02", under.Text(0));
        Assert.Equal("restore puts back 3.1 KB from 13:50", under.Text(1));
        Assert.Null(under.Text(2));
    }

    [Fact]
    public void THE_OPTIONS_CARRY_THE_LABEL_AS_TEXT_THE_INDEX_AS_KEY_AND_THE_CELLS()
    {
        var listing = new Listing("snapshot", "restore", [new("write", ["a"]), new("write", ["b"])], NoPick);
        var options = ListingPanel.Options(listing);
        Assert.Equal(["write", "write"], options.Select(o => o.Label));
        Assert.Equal(["0", "1"], options.Select(o => o.Key));
        Assert.Equal(["b"], options[1].Columns);
    }

    [Fact]
    public void PLAIN_PRINTS_THE_TITLE_THE_HEADER_NUMBERED_ROWS_AND_THE_TYPED_FORM()
    {
        var lines = ListingPanel.Plain("/undo", Two(["tool", "size", "when", "file"]), GlyphSet.Unicode);
        Assert.Equal(
        [
            "undo · 2 snapshots",
            "    tool   size    when   file",
            "1.  write  12 B    14:02  a.txt",
            "2.  edit   3.1 KB  13:50  src/long/path/b.cs",
            "type /undo <n> to restore",
        ], lines);
    }

    [Fact]
    public void PLAIN_PUTS_A_HEADER_WORD_OVER_ITS_CELL_AND_A_SHORT_ROW_LEAVES_NO_TRAIL()
    {
        var listing = new Listing("snapshot", "restore",
            [new("write", ["12 B", "14:02", "", "a.txt"]), new("shell?", ["1 B", "10-29 09:00", "10-29 08:58", "x.txt"]), new("edit", ["2 B"])],
            NoPick, ["tool", "size", "when", "began", "file"]);
        var lines = ListingPanel.Plain("/undo", listing, GlyphSet.Ascii);
        Assert.Equal("undo . 3 snapshots", lines[0]);
        var header = lines[1];
        Assert.Equal(header.IndexOf("file", StringComparison.Ordinal), lines[2].IndexOf("a.txt", StringComparison.Ordinal));
        Assert.Equal(header.IndexOf("file", StringComparison.Ordinal), lines[3].IndexOf("x.txt", StringComparison.Ordinal));
        Assert.Equal(header.IndexOf("began", StringComparison.Ordinal), lines[3].IndexOf("10-29 08:58", StringComparison.Ordinal));
        Assert.Equal("3.  edit    2 B", lines[4]);
        Assert.All(lines, l => Assert.Equal(l.TrimEnd(), l));
    }

    [Fact]
    public void PLAIN_PRINTS_AN_ESCAPE_BYTE_IN_A_LABEL_INERT()
    {
        var listing = new Listing("snapshot", "restore", [new("wr\u001b[31mite", ["a\u001bb"])], NoPick);
        var lines = ListingPanel.Plain("/undo", listing, GlyphSet.Unicode);
        Assert.DoesNotContain(lines, l => l.Contains('\u001b'));
        Assert.Equal("1.  wr[31mite  ab", lines[1]);
    }

    [Fact]
    public void PLAIN_NUMBERS_TEN_ROWS_RIGHT_ALIGNED()
    {
        var listing = new Listing("snapshot", "restore", [.. Enumerable.Range(0, 10).Select(i => new ListingRow("write", [$"f{i}"]))], NoPick);
        var lines = ListingPanel.Plain("/undo", listing, GlyphSet.Unicode);
        Assert.Equal(" 1.  write  f0", lines[1]);
        Assert.Equal("10.  write  f9", lines[10]);
    }
}
