using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//a list-valued row may only break between items, or a task parts from its number. the layout belongs to WizardRows, so SoftWrap keeps its plain wrapping
public class ItemRowWrapTests
{
    private static readonly string[] Tasks =
    [
        "1) run a command", "2) use two tools in order", "3) edit a file",
        "4) recover from a wrong path", "5) not invent a missing file's contents",
    ];

    private static WizardRow Row(int hang = 14) =>
        new("tasks".PadRight(hang), RowTone.Aside, Hang: hang, Items: Tasks);

    private static IReadOnlyList<string> Lines(int width) =>
        [.. WizardRows.Wrap(Row(), width, WizardRows.FrameMargin, Gatto.Terminal.GlyphSet.Unicode).Select(l => l.Lead + l.Text)];

    //every piece between separators must be a whole task, at every width. an oracle built from an imagined wrap passes on shapes the real wrapper never makes
    [Theory]
    [InlineData(78)]
    [InlineData(80)]
    [InlineData(92)]
    [InlineData(100)]
    [InlineData(120)]
    public void EVERY_LINE_IS_WHOLE_TASKS(int width)
    {
        foreach (var line in Lines(width))
        {
            var content = line.TrimStart();
            if (content.StartsWith("tasks", StringComparison.Ordinal))
                content = content["tasks".Length..].TrimStart();

            foreach (var piece in content.Split(" · ", StringSplitOptions.TrimEntries))
                Assert.Contains(piece, Tasks);
        }
    }

    //the whole-items rule alone would pass for a renderer that never packs, so pin the three tasks on the first line at width 100
    [Fact]
    public void ITEMS_ARE_PACKED_NOT_ONE_PER_LINE()
    {
        var lines = Lines(100);

        Assert.Equal(2, lines.Count);
        Assert.EndsWith("3) edit a file", lines[0], StringComparison.Ordinal);
        Assert.Contains("5) not invent a missing file's contents", lines[1], StringComparison.Ordinal);
    }

    //a separator at a break is dropped, the same rule SoftWrap applies to a space
    [Fact]
    public void A_BREAK_DOES_NOT_LEAVE_A_DANGLING_SEPARATOR()
    {
        foreach (var line in Lines(100))
            Assert.DoesNotContain("·", line.TrimEnd()[^1..], StringComparison.Ordinal);
    }

    //continuation lines align under the value column, or the second line reads as a new fact without a label
    [Fact]
    public void CONTINUATIONS_HANG_UNDER_THE_VALUE_COLUMN()
    {
        var lines = Lines(100);

        var column = lines[0].IndexOf("1)", StringComparison.Ordinal);
        Assert.Equal(column, lines[1].IndexOf("4)", StringComparison.Ordinal));
    }

    //an item wider than the row overflows whole, so nobody reintroduces the break as a fix
    [Fact]
    public void AN_ITEM_TOO_WIDE_FOR_A_ROW_IS_NOT_BROKEN()
    {
        var giant = new string('x', 200);
        var row = new WizardRow("tasks".PadRight(14), RowTone.Aside, Hang: 14, Items: ["1) a", giant]);

        var lines = WizardRows.Wrap(row, 100, WizardRows.FrameMargin, Gatto.Terminal.GlyphSet.Unicode).Select(l => l.Text).ToList();

        Assert.Contains(lines, l => l.Contains(giant, StringComparison.Ordinal));
    }

    //a row without items still wraps the ordinary way, since every row in the product passes through the same method
    [Fact]
    public void A_ROW_WITHOUT_ITEMS_STILL_WRAPS_THE_ORDINARY_WAY()
    {
        var prose = new WizardRow(new string('a', 40) + " " + new string('b', 40) + " " + new string('c', 40));

        var lines = WizardRows.Wrap(prose, 60, WizardRows.FrameMargin, Gatto.Terminal.GlyphSet.Unicode).Select(l => l.Text).ToList();

        Assert.True(lines.Count > 1, "a long sentence still wraps");
    }
}
