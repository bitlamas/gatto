using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Repl.Term;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the keys row is composed from the options, the last one Esc's and the others naming their own key. each expected row is read from the drawn frame in the corpus
public class InSessionDownloadTests
{
    //the footer is the last row the face paints on both roads, so the golden is sliced by the helper that knows its shape
    private static string PaintedKeys(WizardScreen.Choice c, int width) =>
        WalkRender.Watching(c, width, ModelFetchTests.FetchingTick).Rows[^1];

    //the 80 frame is its own file in the corpus (the fold is what it is drawn to show)
    private static string Screen(string name, int width) => width == 100 ? name : name + "-80";

    private static string DrawnSetupKeys(int width) =>
        Golden.Load("s6", Screen("fetching", width), width)[^1];

    private static string DrawnSessionKeys(int width) =>
        Golden.Panel("s10", Screen("download", width), width)[^1];

    //write the control byte as an escape, a raw one in source is invisible in every diff
    private static ConsoleKeyInfo CtrlC => new('\u0003', ConsoleKey.C, false, false, control: true);

    private static ConsoleKeyInfo Esc => new('\0', ConsoleKey.Escape, false, false, false);

    //the two roads' keys rows

    //the setup road draws pause and Esc (pausing keeps the bytes, and stopping deletes the partial)
    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    public void THE_SETUP_ROAD_DOWNLOAD_RENDERS_PAUSE_AND_ESC(int width) =>
        Assert.Equal(DrawnSetupKeys(width), PaintedKeys(ModelFetchTests.FetchingScreen(), width));

    //an option ahead of Esc's must name a key the loop knows, so the face refuses one it cannot press
    [Fact]
    public void A_WATCHING_SCREEN_REFUSES_AN_OPTION_WITH_A_KEY_IT_CANNOT_PRESS()
    {
        var unpressable = new WizardScreen.Choice("model.fetching", "Downloading…",
            [new ChoiceOption("pause", "pause", Press: "Ctrl+Alt+P"),
             new ChoiceOption("stop", "stop, deletes the partial")],
            KeysOnly: true, Watching: true);

        var ex = Assert.Throws<InvalidOperationException>(() => PaintedKeys(unpressable, 100));
        Assert.Contains("model.fetching", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Alt+P", ex.Message, StringComparison.Ordinal);

        //the same options with a key the loop knows are accepted, which is what says the refusal is about the key rather than the count
        var pressable = unpressable with
        {
            Options = [new ChoiceOption("pause", "pause", Press: "Space"),
                       new ChoiceOption("stop", "stop, deletes the partial")],
        };
        Assert.Contains("Space", PaintedKeys(pressable, 100), StringComparison.Ordinal);
    }

    //an option ahead of Esc's without a key is refused, the footer would otherwise advertise a verb the user cannot press
    [Fact]
    public void AN_OPTION_BEFORE_ESCS_WITHOUT_A_KEY_IS_REFUSED()
    {
        var nameless = new WizardScreen.Choice("model.fetching", "Downloading…",
            [new ChoiceOption("pause", "pause"),
             new ChoiceOption("stop", "stop, deletes the partial")],
            KeysOnly: true, Watching: true);

        var ex = Assert.Throws<InvalidOperationException>(() => PaintedKeys(nameless, 100));
        Assert.Contains("'pause' has no Press", ex.Message, StringComparison.Ordinal);
    }

    //a watching screen with no options is refused, an empty keys row would strand the user on a watch
    [Fact]
    public void A_WATCHING_SCREEN_WITH_NO_OPTIONS_IS_REFUSED()
    {
        var none = new WizardScreen.Choice("model.fetching", "Downloading…", [],
            KeysOnly: true, Watching: true);

        Assert.Throws<InvalidOperationException>(() => PaintedKeys(none, 100));
    }

    //the add road draws the setup road's two keys, and the oracle is s6's frame (s10's row still shows the retired p key)
    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    public void THE_ADD_ROAD_DRAWS_THE_SETUP_ROADS_TWO_KEYS(int width) =>
        Assert.Equal(DrawnSetupKeys(width),
            PaintedKeys(ModelFetchTests.FetchingScreen(inSession: true), width));

    //the fits sweep is retired, no option sets Short any more so both WatchKeys calls compose the same row

    //the title belongs to the road, each expected spelling read off that road's drawn frame (swapping the arms reddens both rows)
    [Theory]
    [InlineData(false, "s6-fetching-100.txt")]
    [InlineData(true, "s10-download-100.txt")]
    public void THE_MODEL_TITLE_IS_THE_ROADS(bool inSession, string frame)
    {
        var drawn = Gatto.Tests.Census.SourceTree
            .Read(Path.Combine(Golden.Dir, frame))
            .Split('\n')
            .Select(l => l.Trim())
            .Single(l => l.StartsWith("Which model should gatto", StringComparison.Ordinal));

        //the title is the sentence up to its question mark, the purr sits to the right of it
        var title = drawn[..(drawn.IndexOf('?') + 1)];

        Assert.Equal(SetupFlow.ModelTitleFor(inSession), title);
    }

    //diff the whole panel, sliced structurally by Golden.Panel (the transcript above and the status line below)
    [Theory]
    [InlineData("download", 100)]
    [InlineData("download-80", 80)]
    public void THE_IN_SESSION_DOWNLOAD_MATCHES_ITS_DRAWN_FRAME(string screen, int width)
    {
        var diff = Golden.Diff(Golden.Body(Golden.Panel("s10", screen, width)),
            Golden.Body(WalkRender.Watching(ModelFetchTests.FetchingScreen(inSession: true), width,
                ModelFetchTests.FetchingTick).Rows));

        Assert.True(diff is null, $"s10-{screen} at {width}:\n{diff}");
    }

    //two presses, the first arms and repaints, the second answers without one, so the last frame painted is the armed one
    [Fact]
    public void THE_IN_SESSION_ARMED_FRAME_MATCHES_ITS_DRAWN_ONE()
    {
        var diff = Golden.Diff(Golden.Body(Golden.Panel("s10", "download-armed", 100)),
            Golden.Body(WalkRender.Watching(ModelFetchTests.FetchingScreen(inSession: true), 100,
                ModelFetchTests.FetchingTick, script: [Esc, Esc], nowMs: () => 0).Rows));

        Assert.True(diff is null, $"s10-download-armed at 100:\n{diff}");
    }

    //the two reads that meant Esc's option

    //pressing Esc answers the stop rather than the first option, Options[0] would arm a chord about deleting the partial and then pause
    [Fact]
    public void ESC_ANSWERS_THE_STOP_RATHER_THAN_THE_FIRST_OPTION() =>
        Assert.Equal(SetupFlow.ModelFetchStop,
            WalkRender.WatchAfterKeys(ModelFetchTests.FetchingScreen(inSession: true), 100,
                ModelFetchTests.FetchingTick, [Esc, Esc]));

    //the Ctrl+C arm sits in the same loop beside Esc's, so it needs its own driver. a rule with two call sites is only as tested as the one driven
    [Fact]
    public void CTRL_C_ANSWERS_THE_STOP_RATHER_THAN_THE_FIRST_OPTION() =>
        Assert.Equal(SetupFlow.ModelFetchStop,
            WalkRender.WatchAfterKeys(ModelFetchTests.FetchingScreen(inSession: true), 100,
                ModelFetchTests.FetchingTick, [CtrlC, CtrlC]));

    //no digit is drawn so no digit acts on a keys-only screen, and the oracle is the watch landing. the gate is shared, so each screen needs its own check
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_DIGIT_ANSWERS_NOTHING_ON_A_KEYS_ONLY_SCREEN(bool inSession) =>
        Assert.Equal(SetupFlow.Landed,
            WalkRender.WatchAfterKeys(ModelFetchTests.FetchingScreen(inSession), 100,
                ModelFetchTests.FetchingTick, [WizardRig.Digit('1')]));
}
