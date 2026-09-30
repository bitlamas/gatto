using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//keep the fixture path free of spaces, the row has to be one unbreakable token to wrap after its padding
public class SummaryContinuationTests
{
    private const string LongPath =
        @"C:\Users\somebody\AppData\Local\Temp\gatto-dump-1yerbv25.c3e\home1\Downloads\";

    private static IReadOnlyList<string> Lines(WizardRow row, int width) =>
        [.. WizardRows.Wrap(row, width, margin: 2, Gatto.Terminal.GlyphSet.Unicode).Select(s => s.Lead + s.Text)];

    [Fact]
    public void A_LABELLESS_ROW_DOES_NOT_OPEN_WITH_AN_EMPTY_LINE()
    {
        var lines = Lines(SetupFlow.SummaryFacts.Row("", LongPath), 80);

        Assert.NotEmpty(lines);
        Assert.False(string.IsNullOrWhiteSpace(lines[0]),
            "the first line is blank; the row wrapped after its own padding:\n"
            + string.Join("\n", lines.Select(l => "[" + l + "]")));
    }

    //a wholly blank row is spacing, so the drop must not fire on it
    [Fact]
    public void A_ROW_THAT_IS_DELIBERATELY_BLANK_SURVIVES()
    {
        var lines = Lines(new WizardRow(""), 80);

        Assert.Single(lines);
        Assert.True(string.IsNullOrWhiteSpace(lines[0]));
    }

    //a path that fits is left alone, only a value too long for its column wraps
    [Fact]
    public void A_PATH_THAT_FITS_STILL_RENDERS_AS_ONE_LINE()
    {
        var lines = Lines(SetupFlow.SummaryFacts.Row("", @"C:\home\weights\"), 80);

        Assert.Single(lines);
        Assert.Contains(@"C:\home\weights\", lines[0]);
    }
}
