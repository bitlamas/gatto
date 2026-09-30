using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the Hub shelf opens by default and the local one sits behind m, so every flow here is built with CanSwitchSource true
public class LandingTests
{
    //named rather than inlined, so a test here that forgets it reads as a missing word instead of a passing default
    private static SetupFlow Flow(WizardProbes probes) => new(probes) { CanSwitchSource = true };

    private static FoundModel OnDisk(string name, string arch = "gemma3") =>
        new(Path.Combine(@"C:\weights", name, name + "-Q4_K_M.gguf"), 4_000_000_000,
            new GgufHeader(GgufOutcome.Complete, null, arch, name, 262144, null,
                BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
                KeyLength: 128, ValueLength: 128, ChatTemplate: null));

    private static ShelfRow HubRow() => new(
        "unsloth/gemma-4-26B-A4B-it", "unsloth",
        new HubQuant("gemma-4-26B-A4B-it-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
        Params: 25_200_000_000);

    private static string Text(WizardScreen.Choice c) =>
        string.Join("\n", (c.BodyRows ?? []).Select(r => r.Text));

    //the default landing

    //models on disk and a healthy Hub open the Hub shelf, and the find is announced rather than chosen for the user
    [Fact]
    public void A_HEALTHY_HUB_OPENS_THE_HUB_SHELF_AND_NARRATES_THE_FIND()
    {
        var flow = Flow(new WizardProbes
        {
            Rows = [HubRow()],
            Found = [OnDisk("one"), OnDisk("two")],
        });

        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.SearchKey, opened.Key);
        Assert.Equal(ShelfSource.Hub, opened.Shelf!.Source);
        Assert.Contains("gatto also found 2 models already on this machine, m shows them",
            Text(opened), StringComparison.Ordinal);
    }

    //in a session the find names the models missing from the user's list, where setup names them as already on the machine
    [Fact]
    public void IN_SESSION_THE_FIND_IS_WHAT_IS_NOT_ON_THE_LIST_YET()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Found = [OnDisk("one"), OnDisk("two")] });

        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());

        Assert.Contains("gatto found 2 models on this machine not yet on its list, m shows them",
            Text(opened), StringComparison.Ordinal);
        Assert.DoesNotContain("already on this machine", Text(opened), StringComparison.Ordinal);
    }

    //nothing found means no line at all, or the guard above would pass on a face that always narrates
    [Fact]
    public void IN_SESSION_WITH_NOTHING_FOUND_THERE_IS_NO_LINE()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Found = [] });

        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());

        Assert.DoesNotContain("shows them", Text(opened), StringComparison.Ordinal);
    }

    //the count word is computed here too, so a single model reads 1 model
    [Fact]
    public void IN_SESSION_ONE_MODEL_IS_SINGULAR()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Found = [OnDisk("one")] });

        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());

        Assert.Contains("gatto found 1 model on this machine not yet on its list",
            Text(opened), StringComparison.Ordinal);
    }

    //the landing shows one screen and never composes the other, driven on the outage where the two screens differ
    [Fact]
    public void THE_LANDING_EMITS_ONE_SCREEN_NOT_TWO()
    {
        var flow = Flow(Outage(OnDisk("one")));
        var opened = flow.StartPastEngine();

        Assert.Equal(SetupFlow.DiscoveredKey, Assert.IsType<WizardScreen.Choice>(opened).Key);
        Assert.DoesNotContain(flow.Emitted, s => s is WizardScreen.Choice { Key: SetupFlow.SearchKey });
    }

    //m is a key inside the sentence, which no golden can see, so the key span needs its own assertion
    [Fact]
    public void THE_DISCOVERY_LINES_M_IS_MARKED_AS_A_KEY()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Found = [OnDisk("one")] });
        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        var line = Assert.Single(opened.BodyRows ?? [], r => r.Text.Contains("also found", StringComparison.Ordinal));
        Assert.Contains("m", line.Keys ?? []);
    }

    //use the singular for one model, since a line reading "1 models" makes a reader stop
    [Fact]
    public void ONE_MODEL_IS_ONE_MODEL()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Found = [OnDisk("one")] });
        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Contains("found 1 model already", Text(opened), StringComparison.Ordinal);
        Assert.DoesNotContain("1 models", Text(opened), StringComparison.Ordinal);
    }

    //an empty sweep is not narrated, since a sentence about an absence adds nothing to the screen
    [Fact]
    public void AN_EMPTY_SWEEP_IS_NOT_NARRATED()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()] });
        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.SearchKey, opened.Key);
        Assert.DoesNotContain("also found", Text(opened), StringComparison.Ordinal);
    }

    //the auto-landing

    //the fake scripts its outcome, so the HubFailed state is forced by construction
    private static WizardProbes Outage(params FoundModel[] found) => new()
    {
        Rows = [],
        Answer = _ => new HubSearchOutcome([], HubSearchCause.HubFailed),
        Found = found,
    };

    //the Hub is out of reach and this machine has models, so the shelf opens on the local source
    [Fact]
    public void AN_OUTAGE_WITH_MODELS_ON_DISK_OPENS_THE_LOCAL_SHELF()
    {
        var flow = Flow(Outage(OnDisk("one"), OnDisk("two")));

        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.DiscoveredKey, opened.Key);
        Assert.Equal(ShelfSource.Local, opened.Shelf!.Source);
        Assert.Contains("Hugging Face is out of reach, showing the models on this machine.",
            Text(opened), StringComparison.Ordinal);
    }

    //an outage with nothing on disk still gets the Hub's outage screen, since there is nowhere better to go
    [Fact]
    public void AN_OUTAGE_WITH_NOTHING_ON_DISK_STAYS_ON_THE_HUB_SCREEN()
    {
        var flow = Flow(Outage());

        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.Equal(SetupFlow.SearchKey, opened.Key);
    }

    //the landing searches once, so the oracle is the probe's call count (a second search retries Hugging Face on the path already failing)
    [Fact]
    public void THE_LANDING_SEARCHES_ONCE()
    {
        foreach (var found in new[] { Array.Empty<FoundModel>(), [OnDisk("one")] })
        {
            var searches = 0;
            var probes = new WizardProbes
            {
                Rows = [],
                Answer = _ => { searches++; return new HubSearchOutcome([], HubSearchCause.HubFailed); },
                Found = found,
            };

            Flow(probes).StartPastEngine();

            Assert.Equal(1, searches);
        }
    }

    //a healthy search with models on disk stays on the Hub, so the local shelf means the Hub was out of reach
    [Fact]
    public void A_HEALTHY_SEARCH_WITH_MODELS_ON_DISK_STAYS_ON_THE_HUB() =>
        Assert.Equal(SetupFlow.SearchKey,
            Assert.IsType<WizardScreen.Choice>(
                Flow(new WizardProbes { Rows = [HubRow()], Found = [OnDisk("one")] })
                    .StartPastEngine()).Key);

    //m keeps its meaning during an outage, since the rider fires on the arrival only
    [Fact]
    public void M_STILL_REACHES_THE_HUB_DURING_AN_OUTAGE()
    {
        var flow = Flow(Outage(OnDisk("one")));
        flow.StartPastEngine();

        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Equal(SetupFlow.SearchKey, hub.Key);
    }

    //the outage screen names the models behind the m key, and only when there is something behind it
    [Fact]
    public void THE_OUTAGE_SCREEN_NAMES_THE_MODELS_ONE_M_AWAY()
    {
        var flow = Flow(Outage(OnDisk("one"), OnDisk("two")));
        flow.StartPastEngine();
        var hub = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Contains("The 2 models on this machine are one m away", Text(hub), StringComparison.Ordinal);
    }

    [Fact]
    public void AND_NOT_WHEN_THERE_IS_NOTHING_BEHIND_IT()
    {
        var flow = Flow(Outage());
        var hub = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        Assert.DoesNotContain("one m away", Text(hub), StringComparison.Ordinal);
    }

    //honest-empty

    //the empty shelf is the way in for a user whose models sit outside the sweep, so it is the whole feature
    [Fact]
    public void THE_EMPTY_LOCAL_SHELF_IS_THE_INVITATION()
    {
        var probes = new WizardProbes { Rows = [HubRow()], Roots = [@"C:\home\weights", @"C:\dl"] };
        var flow = Flow(probes);
        flow.StartPastEngine();

        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));
        var block = string.Join("\n", empty.Shelf!.Empty ?? []);

        Assert.Equal(SetupFlow.DiscoveredKey, empty.Key);
        Assert.Empty(empty.Shelf!.Rows);
        Assert.Contains("no models on this machine yet, gatto looked in:", block, StringComparison.Ordinal);
        Assert.Contains(@"C:\home\weights", block, StringComparison.Ordinal);
        Assert.Contains(@"C:\dl", block, StringComparison.Ordinal);
        Assert.Contains("type a folder path below, gatto looks there and in its subfolders",
            block, StringComparison.Ordinal);
        Assert.Equal("type a folder path…", empty.Door);
    }

    //the invitation names no number, so tuning the engine's sweep depths can never falsify a sentence on screen
    [Fact]
    public void THE_INVITATION_NAMES_NO_DEPTH()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Roots = [@"C:\home\weights"] });
        flow.StartPastEngine();
        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        var invitation = Assert.Single(empty.Shelf!.Empty!,
            r => r.Contains("subfolders", StringComparison.Ordinal));

        Assert.DoesNotContain(invitation, char.IsDigit);
        Assert.DoesNotContain("two", invitation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("four", invitation, StringComparison.OrdinalIgnoreCase);
    }

    //the roots are the sweep's own, since a re-derived list would name a search nobody ran
    [Fact]
    public void THE_ROOTS_LISTED_ARE_THE_ONES_THE_SWEEP_REPORTED()
    {
        var probes = new WizardProbes { Rows = [HubRow()], Roots = [@"Z:\somewhere odd"] };
        var flow = Flow(probes);
        flow.StartPastEngine();

        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Contains(@"Z:\somewhere odd", empty.Shelf!.Empty!);
    }

    //the cat is drawn in the block, compared against Cats.EmptyOf() so the glyphs are never retyped
    [Fact]
    public void THE_EMPTY_SCREEN_DRAWS_THE_CAT()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Roots = [@"C:\home\weights"] });
        flow.StartPastEngine();
        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        Assert.Contains(Gatto.Repl.Cats.EmptyOf(glyphs: GlyphSet.Unicode), empty.Shelf!.Empty!);
    }

    //the folder door says Enter look, since the hint is data on each door and the corpus has three that differ
    [Fact]
    public void THE_FOLDER_DOOR_SAYS_LOOK_NOT_SEARCH()
    {
        var flow = Flow(new WizardProbes { Rows = [HubRow()], Roots = [@"C:\home\weights"] });
        flow.StartPastEngine();
        var empty = Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.SourceAnswer()));

        //assert on the rendered frame, and scope it to the placeholder's ellipsis so the invitation's own words don't match too
        var door = Assert.Single(
            WalkRender.SettledFrame(empty, 100).Rows,
            r => r.Contains("type a folder path…", StringComparison.Ordinal));

        Assert.Contains("Enter look · Esc back", door, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter search", door, StringComparison.Ordinal);
    }

    //the keys start in the door on a shelf with no rows, since an empty list leaves the arrows with nothing to move
    [Fact]
    public void THE_KEYS_OPEN_IN_THE_DOOR_WHEN_THERE_ARE_NO_ROWS()
    {
        var rows = new ShelfView([], null, Gatto.Core.Hardware.MachineShape.UnifiedWithShare,
            Source: ShelfSource.Local, Empty: ["nothing here"]);
        var some = new ShelfView([HubRow()], "unsloth", Gatto.Core.Hardware.MachineShape.UnifiedWithShare);

        Assert.Equal(Region.Search, Shelf.Opening(rows));
        Assert.Equal(Region.List, Shelf.Opening(some));
    }
}
