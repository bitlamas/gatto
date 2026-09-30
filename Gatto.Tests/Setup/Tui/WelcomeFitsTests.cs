using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup;

namespace Gatto.Tests.Setup.Tui;

//the welcome fits the ladder at 30 rows, and the fit takes structural blanks, then paragraph breaks, then the hint
public class WelcomeFitsTests
{
    private const string Hint = "Tab moves between the parts of a screen";
    private const string FirstParagraphEnd = "on your computer, showing you every step.";
    private const string SecondParagraphStart = "The thinking happens on this machine too";
    private const string Question = "Ready to set gatto up?";

    //the welcome as a walk reaches it, at one terminal size
    private static IReadOnlyList<string> Welcome(int height, int width)
    {
        var rig = new WizardRig(width);
        rig.Surface.Height = height;
        var home = Directory.CreateTempSubdirectory("gatto-welcome-").FullName;
        try
        {
            Gatto.Core.Home.GattoHome.EnsureInitialized(home);
            SetupRunner.Run(new SetupFlow(new WizardProbes()),
                rig.TuiFace([.. WalkOpening.Keys, WizardRig.Esc, WizardRig.Esc]), home);
            //the first painted frame, indexed rather than searched, a search for the welcome's words would pass on any frame that quotes them
            return rig.PaintedFrames[0];
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    private static int IndexOf(IReadOnlyList<string> frame, string text) =>
        frame.ToList().FindIndex(r => r.Contains(text, StringComparison.Ordinal));

    private static bool Blank(IReadOnlyList<string> frame, int i) =>
        i >= 0 && i < frame.Count && frame[i].Trim().Length == 0;

    //the ladder's promise

    //at 30 rows the welcome fits with the hint line still on it, at every ladder width
    [Theory]
    [InlineData(52)]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void THE_WELCOME_FITS_EVERY_LADDER_WIDTH_AT_THIRTY_ROWS(int width)
    {
        var frame = Welcome(30, width);

        Assert.True(frame.Count <= 30,
            $"the welcome is {frame.Count} rows on a {width}x30 terminal, so "
            + $"{frame.Count - 30} row(s) scroll the header off the top");
        Assert.Contains(frame, r => r.Contains(Hint, StringComparison.Ordinal));
    }

    //the order

    //structural blanks go first, so the space around the structure goes and the paragraph breaks stay. two heights, so a rule that held at one is not proven
    [Theory]
    [InlineData(27)]
    [InlineData(26)]
    public void THE_PARAGRAPH_BREAKS_SURVIVE_WHILE_THE_STRUCTURAL_BLANKS_GO(int height)
    {
        var frame = Welcome(height, 80);

        Assert.True(frame.Count <= height, $"the frame is {frame.Count} rows in {height}");

        //the break between two prose paragraphs is a paragraph blank and survives
        var end = IndexOf(frame, FirstParagraphEnd);
        var next = IndexOf(frame, SecondParagraphStart);
        Assert.True(end >= 0 && next == end + 2,
            $"the two prose paragraphs are {next - end} rows apart, so their break was taken:\n"
            + string.Join("\n", frame));

        //the space under the question is structural and is gone
        var q = IndexOf(frame, Question);
        Assert.True(q >= 0, "the welcome lost its question");
        Assert.False(Blank(frame, q + 1),
            "the structural blank under the question survived while the frame was over");

        Assert.Contains(frame, r => r.Contains(Hint, StringComparison.Ordinal));
    }

    //the hint goes last, and only if the frame is still over. the pair of widths at one height is the oracle, 80 keeps the hint and 52 has nothing else left
    [Fact]
    public void THE_HINT_GOES_ONLY_WHEN_NOTHING_ELSE_IS_LEFT_TO_TAKE()
    {
        var roomy = Welcome(24, 80);
        var tight = Welcome(24, 52);

        Assert.Contains(roomy, r => r.Contains(Hint, StringComparison.Ordinal));
        Assert.DoesNotContain(tight, r => r.Contains(Hint, StringComparison.Ordinal));
        //the strip row is blank here, so skip the head of the frame
        Assert.DoesNotContain(tight.Skip(3), r => r.Trim().Length == 0);
    }

    //with room to give, the hint stays while blanks are still being taken. a fit that dropped the hint first would still satisfy the test above
    [Fact]
    public void AND_AT_A_HEIGHT_WITH_BLANKS_LEFT_THE_HINT_IS_STILL_THERE()
    {
        var frame = Welcome(25, 80);

        Assert.Contains(frame, r => r.Contains(Hint, StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Trim().Length == 0);
    }

    //the first closing line is gone and the second is verbatim, asserted on a roomy terminal where nothing was dropped for fit
    [Fact]
    public void THE_FIRST_CLOSING_LINE_IS_GONE_AND_THE_SECOND_IS_VERBATIM()
    {
        var frame = Welcome(200, 100);

        Assert.DoesNotContain(frame, r => r.Contains("Esc backs out", StringComparison.Ordinal));
        Assert.Contains(frame,
            r => r.Contains("Inside, Tab moves between the parts of a screen,", StringComparison.Ordinal)
              && r.Contains("shows where the keys are.", StringComparison.Ordinal));
    }
}
