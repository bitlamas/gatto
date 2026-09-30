using Gatto.Cli.Setup;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//a row that fills the width exactly leaves the cursor in the terminal's deferred-wrap state, so the wrapper keeps two cells free
public class WizardRowsMarginTests
{
    //a sweep, because the defect fires only at the widths where a row ends exactly on the edge
    [Theory]
    [InlineData(40)]
    [InlineData(56)]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(88)]
    [InlineData(100)]
    public void NO_WRAPPED_ROW_REACHES_THE_LAST_COLUMN(int width)
    {
        //words of many lengths put the break in a different place at every width, a single long word would only exercise the hard-break path
        var row = new WizardRow(string.Join(" ", Enumerable.Range(1, 40).Select(n =>
            new string((char)('a' + n % 26), 1 + n % 11))));

        foreach (var (lead, text) in WizardRows.Wrap(row, width, margin: 2, Gatto.Terminal.GlyphSet.Unicode))
            Assert.True(UnicodeWidth.Of(lead + text) <= width - 2,
                $"a wrapped row is {UnicodeWidth.Of(lead + text)} cells at width {width}, "
                + $"over the frame's {width - 2}: {lead + text}");
    }

    //a Wide row takes the margin back, so it is the row the guard must report as over the limit
    [Fact]
    public void AND_THE_MEASUREMENT_CAN_SEE_ONE_THAT_DOES()
    {
        var row = new WizardRow(new string('x', 100), Wide: true);

        var widest = WizardRows.Wrap(row, 80, margin: 2, Gatto.Terminal.GlyphSet.Unicode)
            .Max(s => UnicodeWidth.Of(s.Lead + s.Text));

        Assert.True(widest > 80 - 2,
            "a Wide row takes the margin back, so the measurement must report a row over it");
    }
}
