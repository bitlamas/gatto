using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the band follows the pointer on option rows and footer words only: an option row takes the cursor, a footer word takes the band while the pointer rests on it
public class HoverBandTests
{
    private static MouseEvent Over(HitTarget t) => new(t.FirstCol, t.Row, MouseKind.Move, MouseButton.None, 0, 0);

    private static MouseEvent OverCell(int x, int y) => new(x, y, MouseKind.Move, MouseButton.None, 0, 0);

    private static WizardScreen.Choice Consent() =>
        new("consent", "Download llama.cpp?", [new("yes", "Download it"), new("no", "Not now", EscVerb: "not now")]);

    [Fact]
    public void A_POINTER_ON_AN_OPTION_ROW_MOVES_THE_CURSOR_so_Enter_answers_that_row()
    {
        var c = Consent();
        var row = ShelfFixtures.Target(c, HitKind.OptionRow, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(Over(row), new KeyEvent(WizardRig.Enter));
        Assert.Equal(c.Options[1].Key, face.Choose(c));
    }

    [Fact]
    public void A_POINTER_ON_A_FOOTER_WORD_PUTS_IT_ON_THE_BAND_until_it_leaves()
    {
        var c = Consent();
        var word = ShelfFixtures.Target(c, HitKind.FooterKey, key: "Enter");
        string? before = null, on = null, after = null;
        TuiWizardSurface? face = null;
        string Footer() => face!.LastInked[^1];
        face = new WizardRig(120).TuiFaceEvents(
            (Action)(() => before = Footer()),
            Over(word),
            (Action)(() => on = Footer()),
            OverCell(word.FirstCol, word.Row - 1),
            (Action)(() => after = Footer()),
            new KeyEvent(WizardRig.Enter));

        Assert.Equal(c.Options[0].Key, face.Choose(c));
        Assert.DoesNotContain("\u001b[48", before);
        Assert.Contains("\u001b[48", on);
        Assert.Equal(before, after);
    }

    [Fact]
    public void A_POINTER_ON_A_SHELF_ROW_MOVES_NOTHING()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var row = ShelfFixtures.Target(shelf, HitKind.Row, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(Over(row), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }
}
