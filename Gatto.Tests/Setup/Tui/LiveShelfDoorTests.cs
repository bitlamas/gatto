using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//every guard here drives the live shelf out of the flow, since a hand-built fixture can hold a door the product never sets
public class LiveShelfDoorTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0 ? _q.Dequeue()
            : throw new InvalidOperationException("the face asked for a key the script does not have");
    }

    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.A, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    private static ModelRow Row(string id) => ShelfRows.Of(
        id, id.Split('/')[0], new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, null, 900, false, Params: 3_000_000_000, Arch: "qwen3");

    //the shelf the flow arrives at, with the probe that records the request
    private static (WizardProbes Probes, SetupFlow Flow, WizardScreen.Choice Screen) LiveShelf()
    {
        IReadOnlyList<ModelRow> rows = [Row("unsloth/a"), Row("unsloth/b")];
        var probes = new WizardProbes
        {
            Rows = rows,
            Answer = _ => WizardProbes.Outcome(rows, null),
        };
        var flow = new SetupFlow(probes);
        return (probes, flow, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()));
    }

    private static (string? Answer, IReadOnlyList<string> Frame) Press(
        WizardScreen.Choice screen, int width, params ConsoleKeyInfo[] keys)
    {
        var face = new TuiWizardSurface(
            new RecordingSurface { Width = width, Height = 60 },
            new Keys(keys), new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready());
        string? answer = null;
        try { answer = face.Choose(screen); }
        catch (InvalidOperationException) { } //the script ran out before an answer
        return (answer, face.LastPainted);
    }

    private static string DoorRow(IReadOnlyList<string> frame) =>
        frame.SingleOrDefault(r => r.Contains("search models", StringComparison.Ordinal)) ?? "";

    //the door is there

    //the claim is checked on the screen the flow arrives at, rendered by the shipping face out of the flow's own shelf
    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    public void THE_LIVE_HUB_SHELF_DRAWS_ITS_DOOR_AT_EVERY_SERVED_WIDTH(int width)
    {
        var (_, _, screen) = LiveShelf();

        Assert.NotNull(screen.Door);
        var (_, frame) = Press(screen, width, Key(ConsoleKey.Escape));

        Assert.NotEqual("", DoorRow(frame));
        Assert.Contains(DoorHints.Search, DoorRow(frame), StringComparison.Ordinal);
    }

    //the placeholder is pinned as a literal here, since every fixture derives it from the producer and nothing else holds the copy
    [Fact]
    public void THE_HUB_SHELFS_DOOR_SAYS_search_models()
    {
        Assert.Equal("search models…", SetupFlow.ShelfDoorPlaceholderOf(GlyphSet.Unicode));
        Assert.Equal("search models...", SetupFlow.ShelfDoorPlaceholderOf(GlyphSet.Ascii));
    }

    //a shelf with no rows has no door, since a door over nothing would be handed a stop with nothing in it
    [Fact]
    public void AN_EMPTY_SHELF_HAS_NO_DOOR()
    {
        var probes = new WizardProbes
        {
            Rows = [],
            Answer = _ => WizardProbes.Outcome([], HubSearchCause.NothingFits),
        };

        Assert.Null(Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine()).Door);
    }

    //and it works

    //the typed words must reach the request the flow made, which no screen read can show
    [Theory]
    [InlineData('?')]
    public void TYPING_INTO_THE_DOOR_AND_PRESSING_ENTER_SEARCHES_FOR_IT(char opener)
    {
        var (probes, flow, screen) = LiveShelf();

        var (answer, _) = Press(screen, 100,
            Ch(opener), Ch('q'), Ch('w'), Ch('e'), Ch('n'), Key(ConsoleKey.Enter));

        Assert.NotNull(answer);
        flow.Answer(answer!);

        Assert.Equal("qwen", probes.LastRequest?.Search);
    }

    //leaving the door with Esc searches for nothing, or a door that answers anything would pass the case above
    [Fact]
    public void ESCAPING_THE_DOOR_SEARCHES_FOR_NOTHING()
    {
        var (probes, _, screen) = LiveShelf();
        var before = probes.LastRequest?.Search;

        Press(screen, 100, Ch('?'), Ch('q'), Ch('w'), Key(ConsoleKey.Escape), Key(ConsoleKey.Escape));

        Assert.Equal(before, probes.LastRequest?.Search);
    }

    //the Search region exists only where there is a door, asserted both ways so a dropped region everywhere can't pass
    [Fact]
    public void THE_SEARCH_REGION_IS_A_STOP_ONLY_WHERE_THERE_IS_A_DOOR()
    {
        var (_, _, populated) = LiveShelf();

        Assert.Contains(Region.Search,
            Shelf.Regions(populated.Shelf!, 100, 0, hasDoor: populated.Door is not null));
        Assert.DoesNotContain(Region.Search,
            Shelf.Regions(populated.Shelf!, 100, 0, hasDoor: false));
    }

    //the local shelf's door has its own words, since it searches what is here and also takes a path
    [Fact]
    public void THE_LIVE_LOCAL_SHELF_DRAWS_ITS_OWN_DOOR()
    {
        var probes = new WizardProbes
        {
            Found = [new FoundModel(@"D:\m\qwen.gguf", 4_000_000_000, null)],
        };
        //the local shelf is the first screen when the sweep found something, so the key is asserted before the door is read
        var local = Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine());

        Assert.Equal(SetupFlow.DiscoveredKey, local.Key);
        Assert.Equal(SetupFlow.LocalShelfDoorPlaceholderOf(GlyphSet.Unicode), local.Door);
        Assert.NotEqual(SetupFlow.ShelfDoorPlaceholderOf(GlyphSet.Unicode), local.Door);
    }
}
