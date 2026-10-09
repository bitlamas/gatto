using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//a press and the wheel on the shelf reach what the keys reach, read against the frame that was painted
public class ShelfMouseTests
{
    //a pane's file choice names its file after this prefix, where the row alone answers its key
    private const string PickPrefix = SetupFlow.CtlPick;


    //a shelf with nothing behind it, so Esc arms the leave chord rather than stepping back
    private static WizardScreen.Choice Front(string machine = "unified", bool lifted = false) =>
        ShelfFixtures.Shelf(machine, lifted) with { AllowBack = false };

    [Fact]
    public void A_PRESS_SELECTS_AND_A_SECOND_PRESS_ANSWERS()
    {
        var shelf = Front();
        var row = ShelfFixtures.Target(shelf, HitKind.Row, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(row), RigMouse.On(row));
        Assert.Equal(shelf.Options[1].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_DOUBLE_CLICK_ON_AN_UNSELECTED_ROW_SELECTS_AND_ANSWERS()
    {
        var shelf = Front();
        var row = ShelfFixtures.Target(shelf, HitKind.Row, index: 2);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(row), RigMouse.On(row, @double: true));
        Assert.Equal(shelf.Options[2].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_PRESS_AFTER_A_RESIZE_IS_DROPPED()
    {
        var rig = new WizardRig(120);
        var shelf = Front();
        var selected = ShelfFixtures.Target(shelf, HitKind.Row, index: 0);
        //the width changes after the first paint and before the press is read
        var face = rig.TuiFaceEvents((Action)(() => rig.Surface.Width = 100), RigMouse.On(selected),
            new KeyEvent(WizardRig.Esc), new KeyEvent(WizardRig.Esc));
        Assert.Null(face.Choose(shelf));   //a press read against the stale frame would answer the selected row
    }

    [Fact]
    public void A_DOUBLE_CLICK_ON_ESC_ARMS_ONCE()
    {
        var rig = new WizardRig(120);
        var shelf = Front();
        var esc = ShelfFixtures.Target(shelf, HitKind.FooterKey, key: "Esc");
        var face = rig.TuiFaceEvents(RigMouse.On(esc), RigMouse.On(esc, @double: true),
            new KeyEvent(WizardRig.Down), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[1].Key, face.Choose(shelf));   //no leave: the second press of the double click did not complete the chord
        Assert.Contains(rig.Frames, f => f.Contains("Esc again to leave"));
    }

    [Fact]
    public void A_QUEUED_PRESS_IS_DROPPED_AT_A_SCREEN_CHANGE_AND_A_QUEUED_KEY_SURVIVES()
    {
        var shelf = Front();
        var row1 = ShelfFixtures.Target(shelf, HitKind.Row, index: 1);
        //the pause stops the first screen's drop, so the press is still queued when the second screen paints
        var reader = new BatchConsoleReader([BatchConsoleReader.Key('\r', ConsoleKey.Enter)], [],
            [BatchConsoleReader.Press(row1.FirstCol, row1.Row), BatchConsoleReader.Key('\r', ConsoleKey.Enter)]);
        var face = new WizardRig(120).TuiFaceOver(new ConsoleInputSource(reader));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
        //a missing drop reads the press, which selects row 1, and the Enter answers it
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }

    [Theory]
    [InlineData(-60, -60, 1)]
    [InlineData(120, -240, 2)]   //a notch up at row 0 stays at row 0, then two notches down reach row 2
    public void THE_WHEEL_MOVES_ONE_ROW_PER_120(int first, int second, int expectedRow)
    {
        var shelf = Front();
        var list = ShelfFixtures.Target(shelf, HitKind.Row, index: 0);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.Wheel(list, first), RigMouse.Wheel(list, second), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[expectedRow].Key, face.Choose(shelf));
    }

    [Fact]
    public void THE_WHEEL_STOPS_AT_THE_LAST_ROW()
    {
        var shelf = Front();
        var targets = ShelfFixtures.Targets(shelf);
        var last = targets.Where(t => t.Tag.Kind == HitKind.Row).Max(t => t.Tag.Index);
        var row = targets.Single(t => t.Tag.Kind == HitKind.Row && t.Tag.Index == 0);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.Wheel(row, -120 * (last + 3)), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[last].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_PRESS_ON_A_PANE_LINE_OPENS_IT_AND_ON_A_FILE_CHOOSES_IT()
    {
        var shelf = Front("discrete");
        var line = ShelfFixtures.Target(shelf, HitKind.PaneLine, index: 1);
        //the press opened publisher 1 with the keys on its pick, so the Enter that follows chooses a file
        var picked = new WizardRig(120).TuiFaceEvents(RigMouse.On(line), new KeyEvent(WizardRig.Enter)).Choose(shelf);
        Assert.StartsWith(PickPrefix, picked);

        var opened = ShelfFixtures.Targets(shelf, 120, 0, [.. ShelfFixtures.OpenKeys(shelf)]);
        var file = opened.First(t => t.Tag.Kind == HitKind.PaneFile);
        var answer = new WizardRig(120).TuiFaceEvents([.. ShelfFixtures.OpenKeys(shelf).Select(k => (object)new KeyEvent(k)), .. RigMouse.On(file)])
            .Choose(shelf);
        Assert.StartsWith(PickPrefix, answer);   //a file choice carries its file
    }

    [Fact]
    public void A_PRESS_ON_THE_FOLDED_FILE_ANSWERS_IT()
    {
        var shelf = Front();
        var file = ShelfFixtures.Target(shelf, HitKind.FoldedFile, width: 80);
        var face = new WizardRig(80).TuiFaceEvents(RigMouse.On(file));
        Assert.StartsWith(PickPrefix, face.Choose(shelf));
    }

    [Fact]
    public void A_PRESS_ON_A_CHIP_ANSWERS_THAT_FAMILY()
    {
        var shelf = Front();
        var chip = ShelfFixtures.Target(shelf, HitKind.Chip, index: 2);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(chip));
        Assert.Equal(ShelfControls.FamilyAnswer(shelf.Shelf!.Families![2]), face.Choose(shelf));
    }

    [Fact]
    public void A_PRESS_ON_THE_DOOR_TAKES_THE_KEYS_THERE()
    {
        var shelf = Front();
        var door = ShelfFixtures.Target(shelf, HitKind.Door);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(door), new KeyEvent(WizardRig.Ch('q')), new KeyEvent(WizardRig.Enter));
        Assert.Equal(ShelfControls.TypedAnswer("q"), face.Choose(shelf));
    }

    [Fact]
    public void A_PRESS_ON_PARAMS_SORTS()
    {
        var shelf = Front();
        var header = ShelfFixtures.Target(shelf, HitKind.ParamsHeader);
        Assert.Equal(SetupFlow.CtlParams, new WizardRig(120).TuiFaceEvents(RigMouse.On(header)).Choose(shelf));
    }

    [Fact]
    public void THE_A_CLAUSE_AND_THE_M_FOOTER_WORD_ACT_AS_THEIR_KEYS()
    {
        var shelf = Front("discrete");
        var a = ShelfFixtures.Target(shelf, HitKind.Clause, key: "a");
        Assert.Equal(ShelfControls.LiftAnswer(), new WizardRig(120).TuiFaceEvents(RigMouse.On(a)).Choose(shelf));
        var m = ShelfFixtures.Target(shelf, HitKind.FooterKey, key: "m");
        Assert.Equal(ShelfControls.SourceAnswer(), new WizardRig(120).TuiFaceEvents(RigMouse.On(m)).Choose(shelf));
    }

    [Fact]
    public void A_REGIME_COUNT_JUMPS_TO_ITS_FIRST_SHOWN_ROW()
    {
        var shelf = Front("discrete", lifted: true);
        var v = shelf.Shelf!;
        var targets = ShelfFixtures.Targets(shelf);
        var ram = targets.Single(t => t.Tag.Kind == HitKind.RegimeJump && t.Tag.Regimes!.SetEquals([FitRegime.FitsRamOnly]));
        var shown = targets.Where(t => t.Tag.Kind == HitKind.Row).Select(t => t.Tag.Index).Order().ToList();
        var first = shown.First(i => v.Rows[i].Fit == FitRegime.FitsRamOnly);
        Assert.NotEqual(0, first);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(ram), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[first].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_REGIME_COUNT_WITH_NO_SHOWN_ROW_DOES_NOTHING()
    {
        var shelf = Front("discrete", lifted: true);
        var v = shelf.Shelf!;
        //every shown row made a card row, so the memory count names rows the screen does not show
        var cardOnly = shelf with { Shelf = v with { Rows = [.. v.Rows.Select(r => r with { Fit = FitRegime.FitsGpu })] } };
        var ram = ShelfFixtures.Targets(cardOnly).Single(t => t.Tag.Kind == HitKind.RegimeJump && t.Tag.Regimes!.SetEquals([FitRegime.FitsRamOnly]));
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(ram), new KeyEvent(WizardRig.Enter));
        Assert.Equal(cardOnly.Options[0].Key, face.Choose(cardOnly));
    }

    [Fact]
    public void A_PRESS_SELECTS_AN_ESCAPE_ROW()
    {
        var shelf = Front();
        var withEscape = shelf with { Options = [.. shelf.Options, new ChoiceOption("escape-key", "Skip this step")] };
        var row = ShelfFixtures.Target(withEscape, HitKind.EscapeRow);
        Assert.Equal("escape-key", new WizardRig(120).TuiFaceEvents(RigMouse.On(row), new KeyEvent(WizardRig.Enter)).Choose(withEscape));
    }

    //the frame moves once the escape row is selected, since the pane goes, so the press is read off that frame
    [Fact]
    public void A_PRESS_ON_THE_SELECTED_ESCAPE_ROW_ANSWERS_IT()
    {
        var shelf = Front();
        var withEscape = shelf with { Options = [.. shelf.Options, new ChoiceOption("escape-key", "Skip this step")] };
        var downs = Enumerable.Repeat(WizardRig.Down, withEscape.Options.Count - 1).ToArray();
        var row = ShelfFixtures.Target(withEscape, HitKind.EscapeRow, keys: downs);
        var face = new WizardRig(120).TuiFaceEvents([.. downs.Select(k => (object)new KeyEvent(k)), .. RigMouse.On(row)]);
        Assert.Equal("escape-key", face.Choose(withEscape));
    }

    //a footer word moves no focus, so its Enter acts in the pane the keys are in
    [Fact]
    public void A_FOOTER_WORD_FROM_THE_PANE_ACTS_IN_THE_PANE()
    {
        var shelf = Front("discrete");
        var tabs = ShelfFixtures.OpenKeys(shelf)[..^1];
        var enter = ShelfFixtures.Target(shelf, HitKind.FooterKey, key: "Enter", keys: tabs);
        var face = new WizardRig(120).TuiFaceEvents([.. tabs.Select(k => (object)new KeyEvent(k)), .. RigMouse.On(enter), new KeyEvent(WizardRig.Enter)]);
        var answer = face.Choose(shelf);
        //the footer Enter opened the publisher and the key Enter chose its pick, a press that moved the keys to the list would have answered the row
        Assert.StartsWith(PickPrefix, answer);
    }

    //a clause or a footer word acts as its key with the keys in the door, it is not typed there
    [Fact]
    public void A_CLAUSE_PRESS_WITH_THE_KEYS_IN_THE_DOOR_ACTS_AS_A()
    {
        var shelf = Front("discrete");
        var question = new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, false, false);
        var a = ShelfFixtures.Target(shelf, HitKind.Clause, key: "a", keys: [question]);
        var face = new WizardRig(120).TuiFaceEvents(new KeyEvent(question), RigMouse.On(a), new KeyEvent(WizardRig.Enter));
        Assert.Equal(ShelfControls.LiftAnswer(), face.Choose(shelf));
    }

    [Theory]
    [InlineData("m")]
    [InlineData("a")]
    public void A_FOOTER_WORD_WITH_THE_KEYS_IN_THE_DOOR_ACTS_AS_ITS_KEY(string word)
    {
        var shelf = Front("discrete");
        var question = new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, false, false);
        var t = ShelfFixtures.Target(shelf, HitKind.FooterKey, key: word, keys: [question]);
        var face = new WizardRig(120).TuiFaceEvents(new KeyEvent(question), RigMouse.On(t), new KeyEvent(WizardRig.Enter));
        Assert.Equal(word == "m" ? ShelfControls.SourceAnswer() : ShelfControls.LiftAnswer(), face.Choose(shelf));
    }

    //a pane press where the keys cannot enter the pane does nothing, rather than answer the row with a download decision
    [Fact]
    public void A_PANE_LINE_WITH_NO_FILES_TAKES_NO_PRESS()
    {
        var shelf = Front("discrete");
        var v = shelf.Shelf!;
        var bare = shelf with { Shelf = v with { Facts = [.. v.Facts!.Select(f => f with { Files = [], Publishers = [.. f.Publishers!.Select(p => p with { Files = [] })] })] } };
        var line = ShelfFixtures.Targets(bare).First(t => t.Tag.Kind == HitKind.PaneLine);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(line), new KeyEvent(WizardRig.Esc), new KeyEvent(WizardRig.Esc));
        Assert.Null(face.Choose(bare));
    }

    [Fact]
    public void THE_WHEEL_OVER_THE_PANE_STEPS_THE_PANE()
    {
        var shelf = Front("discrete");
        var line = ShelfFixtures.Target(shelf, HitKind.PaneLine, index: 0);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.Wheel(line, -120), new KeyEvent(WizardRig.Enter), new KeyEvent(WizardRig.Enter));
        Assert.StartsWith(PickPrefix, face.Choose(shelf));
    }

    [Fact]
    public void THE_WHEEL_OVER_THE_FOLDED_FILE_DOES_NOTHING()
    {
        var shelf = Front();
        var file = ShelfFixtures.Target(shelf, HitKind.FoldedFile, width: 80);
        var face = new WizardRig(80).TuiFaceEvents(RigMouse.Wheel(file, -120), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_PRESS_ON_THE_SECOND_FILE_CHOOSES_THAT_FILE()
    {
        var shelf = Front("discrete");
        var open = ShelfFixtures.OpenKeys(shelf);
        var files = ShelfFixtures.Targets(shelf, 120, 0, [.. open]).Where(t => t.Tag.Kind == HitKind.PaneFile).OrderBy(t => t.Row).ToList();
        var first = new WizardRig(120).TuiFaceEvents([.. open.Select(k => (object)new KeyEvent(k)), .. RigMouse.On(files[0])]).Choose(shelf);
        var byKeys = new WizardRig(120).TuiFaceEvents([.. open.Select(k => (object)new KeyEvent(k)), new KeyEvent(WizardRig.Down), new KeyEvent(WizardRig.Enter)]).Choose(shelf);
        var byMouse = new WizardRig(120).TuiFaceEvents([.. open.Select(k => (object)new KeyEvent(k)), .. RigMouse.On(files[1])]).Choose(shelf);
        Assert.NotEqual(first, byKeys);
        Assert.Equal(byKeys, byMouse);
    }

    [Fact]
    public void PRESSES_ON_TWO_ROWS_INSIDE_THE_WINDOW_ARE_TWO_PRESSES()
    {
        var shelf = Front();
        var r1 = ShelfFixtures.Target(shelf, HitKind.Row, index: 1);
        var r2 = ShelfFixtures.Target(shelf, HitKind.Row, index: 2);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(r1, @double: true), RigMouse.On(r2, @double: true), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[2].Key, face.Choose(shelf));
    }

    [Fact]
    public void TWO_PRESSES_ON_A_PANE_LINE_AFTER_THE_WINDOW_ARE_TWO_PRESSES()
    {
        var shelf = Front("discrete");
        var line = ShelfFixtures.Target(shelf, HitKind.PaneLine, index: 1);
        var rig = new WizardRig(120);
        var face = rig.TuiFaceEvents(RigMouse.On(line), RigMouse.On(line), new KeyEvent(WizardRig.Enter), new KeyEvent(WizardRig.Enter));
        Assert.StartsWith(PickPrefix, face.Choose(shelf));
        Assert.Equal(0, rig.KeysPending);
    }

    //the shelf a chip answers comes back as the same screen, so the second press of a double click on the chip is still the second press and toggles nothing
    [Fact]
    public void A_DOUBLE_CLICK_ON_A_CHIP_TOGGLES_IT_ONCE()
    {
        var shelf = Front();
        var chip = ShelfFixtures.Target(shelf, HitKind.Chip, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(chip), RigMouse.On(chip, @double: true), new KeyEvent(WizardRig.Enter));
        Assert.Equal(ShelfControls.FamilyAnswer(shelf.Shelf!.Families![1]), face.Choose(shelf));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }

    //another screen forgets the last press, so a press there on the same cell is a first press
    [Fact]
    public void ANOTHER_SCREEN_FORGETS_THE_LAST_PRESS()
    {
        var shelf = Front();
        var chip = ShelfFixtures.Target(shelf, HitKind.Chip, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(chip), RigMouse.On(chip, @double: true));
        Assert.Equal(ShelfControls.FamilyAnswer(shelf.Shelf!.Families![1]), face.Choose(shelf));
        var other = shelf with { Key = "another-shelf" };
        Assert.Equal(ShelfControls.FamilyAnswer(shelf.Shelf!.Families![1]), face.Choose(other));
    }

    [Fact]
    public void THE_M_SHOWS_THEM_CLAUSE_ACTS_AS_M()
    {
        var shelf = Front("discrete");
        var row = new WizardRow("gatto also found 2 models already on this machine, m shows them", Tone: RowTone.Aside, Keys: ["m"]);
        var withRow = shelf with { BodyRows = [row] };
        var clause = ShelfFixtures.Target(withRow, HitKind.Clause, key: "m");
        Assert.Equal(ShelfControls.SourceAnswer(), new WizardRig(120).TuiFaceEvents(RigMouse.On(clause)).Choose(withRow));
    }

    [Theory]
    [InlineData(MouseKind.Press, MouseButton.Right)]
    [InlineData(MouseKind.Release, MouseButton.Left)]
    [InlineData(MouseKind.Move, MouseButton.None)]
    public void ONLY_A_LEFT_PRESS_ACTS(MouseKind kind, MouseButton button)
    {
        var shelf = Front();
        var row = ShelfFixtures.Target(shelf, HitKind.Row, index: 2);
        var ev = new MouseEvent(row.FirstCol, row.Row, kind, button, 0, 0);
        var face = new WizardRig(120).TuiFaceEvents(new RigMouse.PastTheWindow(), ev, new RigMouse.PastTheWindow(), ev, new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_PRESS_ON_NO_TARGET_DOES_NOTHING()
    {
        var shelf = Front();
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.Press(119, 0), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }

    [Fact]
    public void A_WHEEL_OVER_A_CHIP_DOES_NOTHING()
    {
        var shelf = Front();
        var chip = ShelfFixtures.Target(shelf, HitKind.Chip, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.Wheel(chip, -120), new KeyEvent(WizardRig.Enter));
        Assert.Equal(shelf.Options[0].Key, face.Choose(shelf));
    }
}
