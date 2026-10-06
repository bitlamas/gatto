using Gatto.Cli.Setup;
using Gatto.Core.Hardware;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the shelf drawn while its rows load: every control in place, and the purr and the step line where the rows will be
public class LoadingShelfFaceTests
{
    private const string Step = "reading the files of 13 models · 6 of 13";
    private const string Local = "looking for models on this machine…";

    private static WizardScreen.Choice Loading() => new(
        SetupFlow.ShelfLoadingKey, "Which model should gatto add?", [],
        Shelf: new ShelfView([], null, MachineShape.UnifiedWithShare,
            Families: ["gemma", "qwen", "all"], Family: "all", Loading: true),
        Watching: true, Door: "search models…");

    //a watch that answers on its third poll, so the frames before it are the loading frames
    private static Func<bool> ArrivesOn(int poll)
    {
        var polls = 0;
        return () => ++polls >= poll;
    }

    [Fact]
    public void THE_LOADING_FRAME_HOLDS_THE_CONTROLS_THE_HEADER_THE_PURR_AND_THE_STEP_LINE()
    {
        var rig = new WizardRig(width: 120) { PollTime = true };
        var face = rig.TuiFace();

        var answer = face.Choose(Loading(), ArrivesOn(3), null, null, () => new ShelfLoadTick(Step, Local));

        Assert.Equal(SetupFlow.Landed, answer);
        var frame = rig.PaintedFrames[^1];
        var title = Assert.Single(frame, r => r.Contains("Which model should gatto add?"));
        //the purr is in the list, so the title row carries the title alone
        Assert.Equal("Which model should gatto add?", title.Trim());
        var local = frame.ToList().FindIndex(r => r.Contains(Local));
        var chips = frame.ToList().FindIndex(r => r.Contains("gemma") && r.Contains("qwen"));
        var header = frame.ToList().FindIndex(r => r.Contains("model") && r.Contains("params") && r.Contains("kind"));
        var purr = frame.ToList().FindIndex(r => r.Contains(Step));
        Assert.True(local >= 0 && local < chips && chips < header && header < purr,
            string.Join(Environment.NewLine, frame));
        Assert.Contains(Gatto.Repl.Cats.Face(Gatto.Terminal.GlyphSet.Unicode), frame[purr]);
        Assert.Contains(frame, r => r.Contains("search models…"));
        Assert.Contains("Esc", frame[^1]);
    }

    //the count is read once per frame, so a tick that changes between polls changes the line
    [Fact]
    public void THE_STEP_LINE_FOLLOWS_THE_TICK()
    {
        var rig = new WizardRig(width: 120) { PollTime = true };
        var face = rig.TuiFace();
        var done = 0;

        face.Choose(Loading(), ArrivesOn(4), null, null,
            () => new ShelfLoadTick($"reading the files of 13 models · {++done} of 13", null));

        Assert.Contains(rig.PaintedFrames, f => f.Any(r => r.Contains("· 1 of 13")));
        Assert.Contains(rig.PaintedFrames, f => f.Any(r => r.Contains("· 2 of 13")));
        Assert.DoesNotContain(rig.PaintedFrames[^1], r => r.Contains("on this machine"));
    }

    //the loading shelf opens on the list, so the shelf's own chord leaves in two presses
    [Fact]
    public void ESC_ARMS_AND_A_SECOND_ESC_LEAVES()
    {
        var rig = new WizardRig(width: 120) { PollTime = true, WatchKeyBudget = 2 };
        var face = rig.TuiFace(WizardRig.Esc, WizardRig.Esc);

        var answer = face.Choose(Loading(), () => false, null, null, () => new ShelfLoadTick(Step, null));

        Assert.Null(answer);
    }

    private const string Wait = "please wait while gatto fetches the models from Hugging Face…";

    private static readonly string Face = Gatto.Repl.Cats.Face(Gatto.Terminal.GlyphSet.Unicode);

    //the start-up screen as the flow builds it: the cat as hero rows, the one sentence, and the watch
    private static WizardScreen.Choice Starting() => new WizardScreen.Choice(
        SetupFlow.ShelfLoadingKey, null, [], BodyRows: [new WizardRow(Wait)], Watching: true)
    {
        Hero = [.. Gatto.Repl.Cats.For("", Gatto.Terminal.GlyphSet.Unicode).Split('\n').Select(l => "  " + l.TrimEnd('\r'))],
        Starting = true,
    };

    private static int Purrs(IReadOnlyList<string> frame) =>
        string.Join("\n", frame).Split(Face).Length - 1;

    //the start-up screen names the program, leaves the strip blank, puts the purr level with the cat's face and the step line under the sentence
    [Fact]
    public void THE_START_UP_SCREEN_IS_THE_CAT_WITH_THE_PURR_THE_SENTENCE_AND_THE_STEP_LINE_BESIDE_IT()
    {
        var rig = new WizardRig(width: 100) { PollTime = true };
        var face = rig.TuiFace();

        Assert.Equal(SetupFlow.Landed, face.Choose(Starting(), ArrivesOn(3), null, null, () => new ShelfLoadTick(Step, Local)));

        var frame = rig.PaintedFrames[^1];
        Assert.StartsWith("  gatto ", frame[0]);
        Assert.Contains("v0.5.0", frame[0]);
        Assert.Equal("", frame[1].Trim());
        var at = frame.ToList().FindIndex(r => r.Contains("（＾､＾７"));
        Assert.True(at > 0, string.Join(Environment.NewLine, frame));
        Assert.Contains(Face + " purr", frame[at]);
        Assert.EndsWith(Wait, frame[at + 1]);
        Assert.Contains($"> {Step} · ", frame[at + 2]);
        Assert.Equal(1, Purrs(frame));
        //the line about this machine belongs to the shelf, which is not on this screen
        Assert.DoesNotContain(frame, r => r.Contains(Local));
        Assert.Equal("Esc leave", frame[^1].Trim());
    }

    //the footer names Esc alone, so every other key is inert and the chord still leaves
    [Fact]
    public void ONLY_ESC_ACTS_ON_THE_START_UP_SCREEN()
    {
        var right = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false);
        ConsoleKeyInfo[] keys = [WizardRig.Tab, right, WizardRig.Ch('m'), WizardRig.Ch('b'), WizardRig.Ch('/'),
            WizardRig.Ch('a'), WizardRig.Enter, WizardRig.Digit('1'), WizardRig.Esc, WizardRig.Esc];
        var rig = new WizardRig(width: 100) { PollTime = true, WatchKeyBudget = keys.Length };
        var face = rig.TuiFace(keys);

        Assert.Null(face.Choose(Starting(), () => false, null, null, () => new ShelfLoadTick(Step, null)));
        Assert.Equal(0, rig.KeysPending);
    }

    //the pulse between a loading view and the next screen moves the purr on that view, and the title row stays bare
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void THE_PULSE_AFTER_A_LOADING_VIEW_ADDS_NO_SECOND_PURR(bool starting)
    {
        var rig = new WizardRig(width: 120) { PollTime = true, Pulse = new FakePulse() };
        var face = rig.TuiFace();

        face.Choose(starting ? Starting() : Loading(), ArrivesOn(2), null, null, () => new ShelfLoadTick(Step, null));
        rig.Pulse.Fire();

        var frame = face.LastPainted;
        Assert.Equal(1, Purrs(frame));
        Assert.Contains(frame, r => r.Contains(Step));
        Assert.DoesNotContain(frame, r => r.Contains("Which model should gatto add?") && r.Contains(Face));
    }

    //a chip answers its family while the rows load, which is what lets the flow start the load again
    [Fact]
    public void A_CHIP_ANSWERS_ITS_FAMILY_DURING_THE_LOAD()
    {
        var rig = new WizardRig(width: 120) { PollTime = true, WatchKeyBudget = 4 };
        var right = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false);
        var face = rig.TuiFace(WizardRig.Tab, WizardRig.Tab, right, WizardRig.Enter);

        var answer = face.Choose(Loading(), () => false, null, null, () => new ShelfLoadTick(Step, null));

        Assert.Equal(ShelfControls.FamilyAnswer("qwen"), answer);
    }
}
