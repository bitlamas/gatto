using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//back is the strip, the cursor moves over the sections and Enter on a completed one re-opens it
public class StripBackTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    //two sections answered, the shelf up on the third
    private static readonly IReadOnlyList<StripSection> Road =
    [
        new("machine", StripState.Done), new("engine", StripState.Done),
        new("model", StripState.Current), new("check", StripState.Pending),
        new("done", StripState.Pending),
    ];

    private static WizardScreen.Choice Shelf(bool allowBack = true) =>
        new("model.search", "Which model should gatto start with?",
            [new ChoiceOption("0", "unsloth/gemma-4-26B-A4B-it")],
            Shelf: new ShelfView(
                [ShelfRows.Of("unsloth/gemma-4-26B-A4B-it", "unsloth",
                    new HubQuant("gemma-Q4_K_M.gguf", 16_900_000_000, null),
                    FitRegime.FitsGpu, 131072, false, Badge: null, Downloads: 0, Gated: false)], MachineShape.UnifiedWithShare,
                Families: ["gemma", "all"], Family: "gemma"),
            Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode))
        { Strip = Road, AllowBack = allowBack };

    private static (TuiWizardSurface F, System.Func<IReadOnlyList<string>> Painted) Face(
        params ConsoleKeyInfo[] keys)
    {
        var s = new RecordingSurface { Width = 100 };
        var f = new TuiWizardSurface(s, new Keys(keys), new Theme(new TermCaps(true, true)),
            "0.5.0", "1a2b3c4", () => 0);
        return (f, () => f.LastPainted);
    }

    //two Tabs from the list out to the strip, the shelf's ring runs strip, families, list, search
    private static readonly ConsoleKeyInfo[] ToTheStrip =
        [K(ConsoleKey.Tab), K(ConsoleKey.Tab)];

    private static string StripRow(IReadOnlyList<string> painted) =>
        painted.First(r => r.Contains("machine", StringComparison.Ordinal));



    //an ordinary strip screen with two options and no shelf, so its ring is only the strip and the list
    private static WizardScreen.Choice Ordinary() =>
        //a placeholder key, the flow declares no screen by this name
        new("planted.ordinary", "Which engine build fits this machine?",
            [new ChoiceOption("0", "the pinned build"), new ChoiceOption("1", "something else")])
        { Strip = Road, AllowBack = true };

    //nothing is Done, the opening state, so there is nothing behind to go back to
    private static readonly IReadOnlyList<StripSection> Opening =
    [
        new("machine", StripState.Current), new("engine", StripState.Pending),
        new("model", StripState.Pending), new("check", StripState.Pending),
        new("done", StripState.Pending),
    ];

    //a keys-only screen on whichever road it is given, two options, Enter answers the first and Esc the second
    private static WizardScreen.Choice KeysOnly(IReadOnlyList<StripSection> road) =>
        new("machine", "This machine",
            [new ChoiceOption("0", "next"), new ChoiceOption("1", "leave")], KeysOnly: true)
        { Strip = road, AllowBack = true };

    //a watch-only screen, its single key is the stop. the road has completed sections on purpose, so the guard can't pass on an empty road
    private static WizardScreen.Choice WatchOnly() =>
        new("model.fetching", "Downloading…",
            [new ChoiceOption("stop", "stop, deletes the partial")], KeysOnly: true, Watching: true)
        { Strip = Road, AllowBack = true };

    //a watching screen needs a clock, without one the face waits on a console that isn't there and the run hangs
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static (TuiWizardSurface F, System.Func<IReadOnlyList<string>> Painted) WatchingFace(
        params ConsoleKeyInfo[] keys)
    {
        var s = new RecordingSurface { Width = 100 };
        var f = new TuiWizardSurface(s, new Keys(keys), new Theme(new TermCaps(true, true)),
            "0.5.0", "1a2b3c4", () => 0, clock: _ => new Ready());
        return (f, () => f.LastPainted);
    }

    //the keys row, the last painted row with anything on it
    private static string Footer(IReadOnlyList<string> painted) =>
        painted.Last(r => r.Trim().Length > 0);


    //the keys open on the question. reading the opening focus off regions[0] would move the user onto the road behind them
    [Fact]
    public void AN_ORDINARY_SCREEN_OPENS_WITH_THE_KEYS_ON_THE_LIST()
    {
        var (f, _) = Face([K(ConsoleKey.Enter)]);

        Assert.Equal("0", f.Choose(Ordinary()));
    }


    //back is the strip, so with no section complete there is nowhere to go. the exclusion comes from the road it is given
    [Fact]
    public void A_SCREEN_WITH_NOTHING_DONE_HAS_NO_STOP()
    {
        var (f, painted) = Face([K(ConsoleKey.Tab), K(ConsoleKey.LeftArrow), K(ConsoleKey.Enter)]);

        f.Choose(KeysOnly(Opening));

        Assert.Equal("  ❯ machine · engine · model · check · done", StripRow(painted()));
    }


    //a watch-only screen has no stop even with a road behind it. only Watching with KeysOnly excludes, the check screen watches and keeps its stop
    [Fact]
    public void A_WATCH_ONLY_SCREEN_HAS_NO_STOP_EVEN_WITH_A_ROAD_BEHIND_IT()
    {
        var (f, painted) = WatchingFace([K(ConsoleKey.Tab), K(ConsoleKey.LeftArrow),
                                         K(ConsoleKey.Escape), K(ConsoleKey.Escape)]);

        f.Choose(WatchOnly(), watch: () => false);

        Assert.Equal("  machine ✓ · engine ✓ · ❯ model · check · done", StripRow(painted()));
    }
}
