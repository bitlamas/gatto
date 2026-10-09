using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the row under the keys sits on a band, so the selection reads at a glance and not only by its mark
public class CursorBandTests
{
    private static readonly Theme Dark = new(new TermCaps(true, true));

    private static string BandOn => Ansi.Bg(Theme.UserInputBg, true);

    private static ConsoleKeyInfo Tab => new('\t', ConsoleKey.Tab, false, false, false);

    private static string[] Banded(IReadOnlyList<string> inked) => [.. inked.Where(l => l.Contains(BandOn, StringComparison.Ordinal))];

    private static string Plain(string inked) => System.Text.RegularExpressions.Regex.Replace(inked, "\x1b\\[[0-9;]*m", "");

    [Fact]
    public void THE_SELECTED_OPTION_IS_THE_ONE_BANDED_ROW()
    {
        var c = new WizardScreen.Choice("pick", "Pick one", [new ChoiceOption("a", "Alpha"), new ChoiceOption("b", "Beta")]);

        var band = Assert.Single(Banded(WalkRender.Inked(c, 100, 30, Dark)));
        Assert.Contains("1. Alpha", Plain(band), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_SHELF_BANDS_THE_ROW_UNDER_THE_KEYS_AND_ONLY_ITS_TABLE_CELLS()
    {
        var shelf = ShelfFixtures.Shelf("unified", false) with { AllowBack = false };

        var band = Assert.Single(Banded(WalkRender.Inked(shelf, 120, 40, Dark)));
        Assert.Contains("❯", Plain(band), StringComparison.Ordinal);
        //the pane beside the row is not on the band, so the band ends before the divider
        var divider = band.IndexOf('│');
        Assert.True(divider > 0);
        Assert.DoesNotContain(BandOn, band[divider..], StringComparison.Ordinal);
    }

    [Fact]
    public void WITH_THE_KEYS_IN_THE_PANE_ITS_LINE_IS_BANDED_AND_THE_LIST_ROW_IS_NOT()
    {
        var shelf = ShelfFixtures.Shelf("unified", false) with { AllowBack = false };

        var inked = WalkRender.Inked(shelf, 120, 40, Dark, script: [Tab]);
        var band = Assert.Single(Banded(inked));
        var divider = band.IndexOf('│');
        Assert.True(divider > 0);
        Assert.DoesNotContain(BandOn, band[..divider], StringComparison.Ordinal);
    }
}
