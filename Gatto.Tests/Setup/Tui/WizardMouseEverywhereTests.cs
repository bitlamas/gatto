using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//off the shelf a press selects and answers option rows and acts as a footer word's key, and while work runs only the footer words answer
public class WizardMouseEverywhereTests
{
    //the shapes of the consent, the two check screens and a connect screen: a question, option rows, and on the connect screen a door
    public static TheoryData<string> Screens() => new() { "consent", "check-ask", "audition", "connect" };

    private static WizardScreen.Choice ScreenOf(string kind) => kind switch
    {
        "consent" => new("consent", "Download llama.cpp?", [new("yes", "Download it"), new("no", "Not now", EscVerb: "not now")],
            BodyRows: ["gatto needs llama.cpp to run a model on this machine."]),
        "check-ask" => new("check-ask", "Check it now?", [new("check", "Check it now"), new("unchecked", "Add it unchecked", EscVerb: "add unchecked")]),
        "audition" => new("audition", "Try the model now?", [new("yes", "Yes"), new("skip", "Skip")]),
        _ => new("connect", "Which server?", [new("local", "http://127.0.0.1:8080"), new("other", "Another address")], Door: "type an address…"),
    };

    private static HitTarget OptionAt(WizardScreen.Choice c, int i, int width = 120, params ConsoleKeyInfo[] keys) =>
        ShelfFixtures.Target(c, HitKind.OptionRow, index: i, width: width, keys: keys);

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_PRESS_SELECTS_AN_OPTION_AND_A_SECOND_ANSWERS_IT(string kind)
    {
        var c = ScreenOf(kind);
        var row = OptionAt(c, 1);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(row), RigMouse.On(row));
        Assert.Equal(c.Options[1].Key, face.Choose(c));
    }

    [Fact]
    public void A_PRESS_ON_A_NO_DEFAULT_SCREEN_SELECTS_FIRST()
    {
        var c = ScreenOf("audition") with { NoDefault = true };
        var row = OptionAt(c, 0);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(row), new KeyEvent(WizardRig.Enter));
        Assert.Equal(c.Options[0].Key, face.Choose(c));
    }

    [Fact]
    public void THE_DOOR_OF_A_CONNECT_SCREEN_TAKES_THE_KEYS()
    {
        var c = ScreenOf("connect");
        var door = ShelfFixtures.Target(c, HitKind.Door);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(door), new KeyEvent(WizardRig.Ch('x')), new KeyEvent(WizardRig.Enter));
        Assert.Equal(ShelfControls.TypedAnswer("x"), face.Choose(c));
    }

    [Fact]
    public void A_PRESS_ON_THE_SELECTED_OPTION_WITH_A_DRAFT_IN_THE_DOOR_ANSWERS_THE_OPTION()
    {
        var c = ScreenOf("connect");
        var door = ShelfFixtures.Target(c, HitKind.Door);
        var row = ShelfFixtures.Target(c, HitKind.OptionRow, index: 0);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(door), new KeyEvent(WizardRig.Ch('x')), RigMouse.On(row));
        Assert.Equal(c.Options[0].Key, face.Choose(c));
    }

    [Fact]
    public void TWO_PRESSES_ON_AN_OPTION_WITH_THE_KEYS_IN_THE_DOOR_SELECT_AND_ANSWER()
    {
        var c = ScreenOf("connect");
        var door = ShelfFixtures.Target(c, HitKind.Door);
        var row = ShelfFixtures.Target(c, HitKind.OptionRow, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(door), RigMouse.On(row), RigMouse.On(row));
        Assert.Equal(c.Options[1].Key, face.Choose(c));
    }

    [Fact]
    public void THE_WHEEL_DOES_NOTHING_ON_THE_CONSENT()
    {
        var c = ScreenOf("consent");
        var row = OptionAt(c, 0);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.Wheel(row, -240), new KeyEvent(WizardRig.Enter));
        Assert.Equal(c.Options[0].Key, face.Choose(c));
    }

    //the welcome screen answers keys only, so its footer's Enter word is the one press that answers
    [Fact]
    public void A_KEYS_ONLY_SCREENS_ENTER_WORD_ANSWERS_AS_ENTER()
    {
        var c = new WizardScreen.Choice("welcome", "Set gatto up?", [new("go", "start"), new("quit", "leave")], KeysOnly: true);
        Assert.DoesNotContain(ShelfFixtures.Targets(c), t => t.Tag.Kind == HitKind.OptionRow);
        var enter = ShelfFixtures.Target(c, HitKind.FooterKey, key: "Enter");
        Assert.Equal("go", new WizardRig(120).TuiFaceEvents(RigMouse.On(enter)).Choose(c));
    }

    private static WizardScreen.Choice DownloadWatch() =>
        new("download", "Downloading", [new("pause", "pause", Press: "p"), new("stop", "stop")],
            Watching: true, KeysOnly: true, ArmedCost: "Esc again to stop the download");

    [Fact]
    public void A_PRESS_ON_ESC_ON_A_DOWNLOAD_WATCH_ARMS_ITS_PRICE()
    {
        var rig = new WizardRig(120);
        var c = DownloadWatch();
        var esc = ShelfFixtures.Target(c, HitKind.FooterKey, key: "Esc");
        var face = rig.TuiFaceEvents(RigMouse.On(esc), new KeyEvent(WizardRig.Esc));
        Assert.Equal("stop", face.Choose(c));
        Assert.Contains(rig.Frames, f => f.Contains("Esc again to stop the download"));
    }

    [Fact]
    public void A_DOUBLE_CLICK_ON_ESC_ON_A_DOWNLOAD_WATCH_ARMS_ONCE()
    {
        var c = DownloadWatch();
        var esc = ShelfFixtures.Target(c, HitKind.FooterKey, key: "Esc");
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(esc), RigMouse.On(esc, @double: true), new KeyEvent(WizardRig.Ch('p')));
        Assert.Equal("pause", face.Choose(c));   //a second press that completed the chord would have stopped the download
    }

    [Fact]
    public void A_WATCHS_KEY_WORD_ANSWERS_AS_ITS_KEY()
    {
        var c = DownloadWatch();
        var pause = ShelfFixtures.Target(c, HitKind.FooterKey, key: "p");
        Assert.Equal("pause", new WizardRig(120).TuiFaceEvents(RigMouse.On(pause)).Choose(c));
    }

    [Fact]
    public void THE_START_UP_SCREENS_ESC_WORD_ARMS_THE_LEAVE_CHORD()
    {
        var rig = new WizardRig(120);
        var c = new WizardScreen.Choice("starting", null, [new("leave", "leave")], Watching: true, KeysOnly: true) { Starting = true };
        var esc = ShelfFixtures.Target(c, HitKind.FooterKey, key: "Esc");
        var face = rig.TuiFaceEvents(RigMouse.On(esc), new KeyEvent(WizardRig.Esc));
        Assert.Equal("leave", face.Choose(c));
        Assert.Contains(rig.Frames, f => f.Contains("Esc again to leave"));
    }

    [Fact]
    public void A_CHIP_PRESS_ON_THE_LOADING_SHELF_ANSWERS_THAT_FAMILY()
    {
        var shelf = ShelfFixtures.Shelf("unified");
        var loading = shelf with { Watching = true, Shelf = shelf.Shelf! with { Loading = true, Rows = [], Facts = [] } };
        var chip = ShelfFixtures.Target(loading, HitKind.Chip, index: 1);
        var face = new WizardRig(120).TuiFaceEvents(RigMouse.On(chip));
        Assert.Equal(ShelfControls.FamilyAnswer(loading.Shelf!.Families![1]), face.Choose(loading));
    }

    [Fact]
    public void AN_ASKS_OFFER_ROW_SELECTS_AND_ANSWERS_AND_ITS_DOOR_TAKES_THE_KEYS()
    {
        var a = new WizardScreen.Ask("folder", "Where are your models?", _ => null, "a folder…", new AskOffer("the usual place", @"C:\models"));
        var targets = AskTargets(a);
        var offer = targets.Single(t => t.Tag.Kind == HitKind.OptionRow);
        Assert.Equal(@"C:\models", new WizardRig(120).TuiFaceEvents(RigMouse.On(offer)).Ask(a));

        var door = targets.Single(t => t.Tag.Kind == HitKind.Door);
        var typed = new WizardRig(120).TuiFaceEvents(RigMouse.On(door), new KeyEvent(WizardRig.Ch('D')), new KeyEvent(WizardRig.Enter)).Ask(a);
        Assert.Equal("D", typed);
    }

    [Fact]
    public void AN_ASKS_ESC_WORD_ACTS_AS_ESC()
    {
        var a = new WizardScreen.Ask("folder", "Where?", _ => null, "a folder…") { AllowBack = true };
        var esc = AskTargets(a).Single(t => t.Tag.Kind == HitKind.FooterKey && t.Tag.Key == "Esc");
        Assert.Equal(SetupFlow.BackKey, new WizardRig(120).TuiFaceEvents(RigMouse.On(esc)).Ask(a));
    }

    private static IReadOnlyList<HitTarget> AskTargets(WizardScreen.Ask a)
    {
        var rig = new WizardRig(120);
        var face = rig.TuiFace();
        Assert.Throws<InvalidOperationException>(() => face.Ask(a));
        return face.LastHits.Targets;
    }
}
