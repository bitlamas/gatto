using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the shelf's search field takes typed text, and that answer is read before AnswerSearch's numbered tail, which throws on anything else
public class ShelfDoorTests
{
    private static readonly ConsoleKeyInfo Slash = new('/', ConsoleKey.Oem2, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static IEnumerable<ConsoleKeyInfo> Type(string text) =>
        text.Select(ch => new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false));

    private static IEnumerable<ConsoleKeyInfo> IntoDoor(string text, ConsoleKey end) =>
        [Slash, .. Type(text), Key(end)];

    //the answer wraps the text, so a bare numeral stays a row index and the wrapped form says what was typed
    [Theory]
    [InlineData("gemma")]
    [InlineData("unsloth/Qwen3.5-4B-GGUF")]
    [InlineData(@"D:\models")]
    public void THE_DOOR_ANSWERS_WITH_WHAT_WAS_TYPED(string text)
    {
        var (answer, _) = WalkRender.Answered(
            ShelfTests.Screen(ShelfTests.Unified96()), 100, IntoDoor(text, ConsoleKey.Enter));

        Assert.Equal(ShelfControls.TypedAnswer(text), answer);
    }

    //the draft is drawn while it is typed, since an answer-only test misses a field that takes text and draws nothing
    [Fact]
    public void THE_DRAFT_IS_DRAWN_WHILE_IT_IS_TYPED()
    {
        var rows = WalkRender.SettledFrame(
            ShelfTests.Screen(ShelfTests.Unified96()), 100, script: [Slash, .. Type("gemma")]).Rows;

        Assert.Contains(rows, r => r.Contains("gemma", StringComparison.Ordinal));
    }

    //the Esc key in the field answers nothing, and falling through would pick a model the user did not choose
    [Fact]
    public void ESC_IN_THE_DOOR_ANSWERS_NOTHING()
    {
        Assert.Throws<WalkRender.WalkEnded>(() => WalkRender.Answered(
            ShelfTests.Screen(ShelfTests.Unified96()), 100, IntoDoor("gem", ConsoleKey.Escape)));
    }

    //the Enter key on an empty field answers nothing, an empty answer must not pick the model under the cursor
    [Fact]
    public void ENTER_ON_AN_EMPTY_DOOR_ANSWERS_NOTHING()
    {
        Assert.Throws<WalkRender.WalkEnded>(() => WalkRender.Answered(
            ShelfTests.Screen(ShelfTests.Unified96()), 100, [Slash, Key(ConsoleKey.Enter)]));
    }

    //the field must not swallow Enter on the list, which still picks the model under the cursor
    [Fact]
    public void AND_ENTER_IN_THE_LIST_STILL_PICKS_THE_MODEL()
    {
        var (answer, _) = WalkRender.Answered(
            ShelfTests.Screen(ShelfTests.Unified96()), 100, [Key(ConsoleKey.Enter)]);

        Assert.Equal("0", answer);
    }

    //the arrows still move the table while the keys are in the field, since the list is still underneath
    [Fact]
    public void THE_ARROWS_STILL_WALK_THE_TABLE_FROM_THE_DOOR()
    {
        var (answer, _) = WalkRender.Answered(ShelfTests.Screen(ShelfTests.Unified96()), 100,
            [Slash, Key(ConsoleKey.DownArrow), Key(ConsoleKey.Escape), Key(ConsoleKey.Enter)]);

        Assert.Equal("1", answer);
    }

    private static (SetupFlow Flow, WizardProbes Probes) OnTheShelf()
    {
        var probes = new WizardProbes();
        var flow = new SetupFlow(probes);
        Assert.Equal(SetupFlow.SearchKey, ScreenKey.Of(flow.StartPastEngine()));
        return (flow, probes);
    }

    //a typed word starts a new search, the route this field exists for
    [Fact]
    public void A_TYPED_WORD_RE_SEARCHES()
    {
        var next = OnTheShelf().Flow.Answer(ShelfControls.TypedAnswer("gemma"));

        Assert.Equal(SetupFlow.SearchKey, ScreenKey.Of(next));
    }

    //a repo id takes the id route, so the shelf and the typed-id question cannot disagree about what a repo id is
    [Fact]
    public void A_TYPED_REPO_ID_TAKES_THE_ID_ROAD()
    {
        var next = OnTheShelf().Flow.Answer(ShelfControls.TypedAnswer("unsloth/Qwen3.5-4B-GGUF"));

        Assert.NotEqual(SetupFlow.SearchKey, ScreenKey.Of(next));
    }

    //assert the scan received the folder, since an empty scan falls through to search and the screen key can't tell the two apart
    [Fact]
    public void A_TYPED_PATH_TAKES_THE_SCAN_ROAD()
    {
        var (flow, probes) = OnTheShelf();
        var before = probes.ScanRoots.Count;

        flow.Answer(ShelfControls.TypedAnswer(@"D:\models"));

        Assert.Contains(@"D:\models", probes.ScanRoots[before..]);
    }

    //a folder typed into the typed-id question must scan rather than search for its own path string
    [Fact]
    public void A_PATH_PASTED_INTO_THE_TYPED_ID_ASK_SCANS_INSTEAD_OF_SEARCHING_FOR_ITSELF()
    {
        var (flow, probes) = OnTheShelf();
        Assert.Equal(SetupFlow.TypedIdKey, ScreenKey.Of(flow.Answer(SetupFlow.TypeAnId)));
        var before = probes.ScanRoots.Count;

        flow.Answer(@"D:\models");

        Assert.Contains(@"D:\models", probes.ScanRoots[before..]);
    }
}
