using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the picker says current, since the wizard recommends nothing about a publisher it merely searches
public class PublisherMarkWordTests
{
    //the curated publisher is what draws the mark, so a fixture without one gives a picker with no mark
    private static WizardProbes Probes() => new()
    {
        Publishers = ["unsloth", "bartowski", "ggml-org"],
        Curated = "bartowski",
    };

    private static WizardScreen.Choice Picker()
    {
        var flow = new SetupFlow(Probes()) { CanSwitchSource = true };
        flow.StartPastEngine();
        return Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.PublisherAnswer()));
    }

    //exactly one option holds the mark, which is what opens the cursor there, so the count is asserted before the word
    [Fact]
    public void EXACTLY_ONE_PUBLISHER_IS_MARKED()
    {
        var marked = Picker().Options.Where(o => o.Recommended).ToList();

        Assert.Single(marked);
        Assert.Equal("current", Assert.Single(marked).MarkWord);
    }

    //assert both halves of the word on the rendered row, since the absence alone passes on a row that lost the mark
    [Fact]
    public void THE_TUI_ROW_SAYS_CURRENT_AND_NOT_RECOMMENDED()
    {
        var rows = WalkRender.SettledFrame(Picker(), 100, "publisher").Rows;
        var text = string.Join("\n", rows);

        Assert.Contains("current", text, StringComparison.Ordinal);
        Assert.DoesNotContain("recommended", text, StringComparison.OrdinalIgnoreCase);
    }

    //the plain face must say the same word, or the two faces describe one option differently
    [Fact]
    public void THE_PLAIN_FACE_SAYS_CURRENT_TOO()
    {
        var options = WizardPaint.Options(Picker(), GlyphSet.Unicode);

        var marked = Assert.Single(options, o => o.Recommended);
        Assert.Equal("current", marked.MarkWord);
    }

    //the mark decides the drawn word and the cursor row, so a word that switched itself on would disagree about the row
    [Fact]
    public void A_WORD_WITHOUT_THE_MARK_DRAWS_NOTHING()
    {
        var screen = new WizardScreen.Choice("probe", "Which?",
            [new ChoiceOption("a", "Alpha", MarkWord: "current"),
             new ChoiceOption("b", "Beta")]);

        var text = string.Join("\n", WalkRender.SettledFrame(screen, 100, "probe").Rows);

        Assert.DoesNotContain("current", text, StringComparison.Ordinal);
    }

    //an option with the mark and no word still says recommended, so a caller that sets only the mark keeps that wording
    [Fact]
    public void AN_OPTION_WITH_NO_WORD_STILL_SAYS_RECOMMENDED()
    {
        var screen = new WizardScreen.Choice("probe", "Which?",
            [new ChoiceOption("a", "Alpha", Recommended: true),
             new ChoiceOption("b", "Beta")]);

        var text = string.Join("\n", WalkRender.SettledFrame(screen, 100, "probe").Rows);

        Assert.Contains("recommended", text, StringComparison.Ordinal);
    }
}
