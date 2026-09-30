using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//answering a family chip must clear the typed search. the fixture answers nothing to a search, so a surviving term empties the shelf.
public class FamilyChipClearsSearchTests
{
    private static ShelfRow Row(string id, string arch) => new(
        RepoId: id, Publisher: id.Split('/')[0],
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: 900, Gated: false, Params: 3_000_000_000, Arch: arch);

    private static string Family => Families.Load().Ladder[0];

    //record each request so a test can assert what the flow asked the engine rather than infer it from the answer.
    private static (SetupFlow Flow, List<HubSearchRequest> Asked) OnTheHubShelf()
    {
        IReadOnlyList<ShelfRow> browse = [Row("o/one", "gemma3"), Row("o/two", "qwen3")];
        var asked = new List<HubSearchRequest>();
        var probes = new WizardProbes
        {
            //the engine setting is what pushes a screen behind the shelf. without it the back-key tests would answer the shelf with a key it does not have.
            Llama = @"C:\llama\llama-server.exe",
            Rows = browse,
            //the local shelf needs rows, or m falls through to a Hub search. the last test below would count that fall-through as the local chip reaching across.
            Found = [new FoundModel(@"D:\w\a\a-Q4_K_M.gguf", 4_000_000_000, null)],
            Answer = req =>
            {
                asked.Add(req);
                //a typed search answers nothing and a browse answers rows, so a word that survives the chip press empties the shelf.
                return req.Search is { Length: > 0 }
                    ? new HubSearchOutcome([], null, null)
                    : new HubSearchOutcome(browse, null, null);
            },
        };
        //the run starts on the Hub shelf only when CanSwitchSource is true, else every chip below drives the local arm.
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        var opened = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.Equal(SetupFlow.SearchKey, opened.Key);
        return (flow, asked);
    }

    //the chip browses its family, and the flow must not send the typed word with it.
    [Fact]
    public void A_FAMILY_CHIP_CLEARS_THE_TYPED_SEARCH_AND_BROWSES()
    {
        var (flow, asked) = OnTheHubShelf();
        Assert.IsType<WizardScreen.Choice>(flow.Answer(ShelfControls.TypedAnswer("LFM")));

        var screen = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(SetupFlow.CtlFamily + Family));

        //assert at the request as well as the rows, since rows can come back for other reasons.
        Assert.Null(asked[^1].Search);
        Assert.Equal(Family, asked[^1].Family);
        Assert.NotEmpty(screen.Shelf!.Rows);
    }

    //the screen must show the chip lit and the word gone from the input box. assert the chip as painted ink, the view's field is set either way.
    [Fact]
    public void THE_CHIP_IS_LIT_AND_THE_DOOR_NO_LONGER_HOLDS_THE_WORD()
    {
        var (flow, _) = OnTheHubShelf();
        flow.Answer(ShelfControls.TypedAnswer("LFM"));

        var screen = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(SetupFlow.CtlFamily + Family));

        Assert.Equal(Family, screen.Shelf!.Family);
        Assert.Null(screen.Draft);
        Assert.NotNull(screen.Door);

        var chips = Shelf.Chips(screen.Shelf, chip: 0, focus: Region.List, width: 100,
            glyphs: GlyphSet.Unicode);
        Assert.Contains(chips.Runs, r => r.Text == Family && r.Ink == RunInk.Accent);
        Assert.DoesNotContain("(lifted", chips.Text, StringComparison.Ordinal);

        var frame = string.Concat(Tui.WalkRender.SettledFrame(screen, 100, "after-chip").Rows);
        Assert.DoesNotContain("LFM", frame, StringComparison.Ordinal);
        Assert.Contains("search models", frame, StringComparison.Ordinal);
    }

    //the all chip clears the search too, so a press after a search asks for the whole shelf.
    [Fact]
    public void THE_ALL_CHIP_CLEARS_THE_SEARCH_AS_WELL()
    {
        var (flow, asked) = OnTheHubShelf();
        flow.Answer(ShelfControls.TypedAnswer("LFM"));

        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlFamily + "all"));

        Assert.Null(asked[^1].Search);
        Assert.Null(asked[^1].Family);
        Assert.NotEmpty(screen.Shelf!.Rows);
    }

    //a chip pressed with no search in force must still filter by family. otherwise a fix that clears everything and browses would go unseen.
    [Fact]
    public void A_CHIP_WITH_NO_SEARCH_IN_FORCE_STILL_FILTERS_BY_FAMILY()
    {
        var (flow, asked) = OnTheHubShelf();

        flow.Answer(SetupFlow.CtlFamily + Family);

        Assert.Equal(Family, asked[^1].Family);
        Assert.Null(asked[^1].Search);
    }

    //the local shelf's chip is a different control and must not clear the Hub search, that shelf has no search of its own.
    [Fact]
    public void THE_LOCAL_CHIP_DOES_NOT_TOUCH_THE_HUB_SEARCH()
    {
        var (flow, asked) = OnTheHubShelf();
        flow.Answer(ShelfControls.TypedAnswer("LFM"));
        var before = asked.Count;

        flow.Answer(SetupFlow.CtlSource);
        flow.Answer(SetupFlow.CtlFamily + Family);

        //the local control redraws the local shelf and asks the hub nothing, so a count change means the two controls stopped being separate.
        Assert.Equal(before, asked.Count);
    }

    //the local chip must not clear the Hub's typed word either. the local arm adds no Hub request, so counting requests would miss it.
    [Fact]
    public void THE_LOCAL_CHIP_DOES_NOT_CLEAR_THE_HUB_SEARCH_EITHER()
    {
        var (flow, asked) = OnTheHubShelf();
        flow.Answer(ShelfControls.TypedAnswer("LFM"));

        flow.Answer(SetupFlow.CtlSource);
        flow.Answer(SetupFlow.CtlFamily + Family);
        flow.Answer(SetupFlow.CtlSource);

        Assert.Equal("LFM", asked[^1].Search);
    }

    //back must reach the shelf as the user left it. state travels live and the search re-runs, since a pushed screen's picture and its rows predate the chip
    [Fact]
    public void BACK_AFTER_A_CHIP_LANDS_ON_THE_SHELF_THEY_LEFT()
    {
        var (flow, asked) = OnTheHubShelf();
        flow.Answer(ShelfControls.TypedAnswer("LFM"));
        flow.Answer(SetupFlow.CtlFamily + Family);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal(SetupFlow.SearchKey, back.Key);
        Assert.Equal(Family, back.Shelf!.Family);
        Assert.False(back.Shelf.Searched);
        //assert the request too (the rows are re-fetched under the live filter, so the chips row and the rows could describe different shelves)
        Assert.Equal(Family, asked[^1].Family);
        Assert.Null(asked[^1].Search);
    }

    //a search must survive back, word and all (this fixture's search finds nothing, so the input box brings the word back as Draft)
    [Fact]
    public void BACK_AFTER_A_SEARCH_KEEPS_THE_SEARCH_AND_THE_WORD()
    {
        var (flow, asked) = OnTheHubShelf();
        flow.Answer(ShelfControls.TypedAnswer("LFM"));

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal(SetupFlow.SearchKey, back.Key);
        Assert.True(back.Shelf!.Searched);
        Assert.Equal("LFM", back.Draft);
        Assert.Equal("LFM", asked[^1].Search);
    }

    //a typed search must drop the family (the engine nulls it, so the shelf would claim a filter the rows did not use)
    [Fact]
    public void A_TYPED_SEARCH_DROPS_THE_FAMILY()
    {
        var (flow, asked) = OnTheHubShelf();
        flow.Answer(SetupFlow.CtlFamily + Family);

        var searched = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(ShelfControls.TypedAnswer("LFM")));

        Assert.Null(asked[^1].Family);
        Assert.Equal("LFM", asked[^1].Search);
        Assert.Equal("all", searched.Shelf!.Family);
        Assert.True(searched.Shelf.Searched);
    }

    //the flow must never send a family and a search together. the engine's guard stays as dead code and must not be what holds the rule
    [Fact]
    public void THE_FLOW_NEVER_SENDS_A_FAMILY_AND_A_SEARCH_AT_ONCE()
    {
        var (flow, asked) = OnTheHubShelf();

        flow.Answer(SetupFlow.CtlFamily + Family);
        flow.Answer(ShelfControls.TypedAnswer("LFM"));
        flow.Answer(SetupFlow.CtlFamily + "all");
        flow.Answer(ShelfControls.TypedAnswer("other"));
        flow.Answer(SetupFlow.CtlFamily + Family);

        Assert.NotEmpty(asked);
        Assert.DoesNotContain(asked, r => r.Family is { Length: > 0 } && r.Search is { Length: > 0 });
    }

    //a chip that empties the shelf for no countable reason must still keep its frame
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_CHIP_THAT_EMPTIES_THE_SHELF_FOR_NO_COUNTABLE_REASON_KEEPS_ITS_FRAME(bool allChip)
    {
        IReadOnlyList<ShelfRow> browse = [Row("o/one", "gemma3")];
        var calls = 0;
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = browse,
            Answer = _ => ++calls == 1
                ? new HubSearchOutcome(browse, null, null)
                : new HubSearchOutcome([], null, null, HiddenByKind: 4),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        var screen = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(SetupFlow.CtlFamily + (allChip ? "all" : Family)));

        Assert.Empty(screen.Shelf!.Rows);
        Assert.NotNull(screen.Door);

        var block = string.Join(" | ", screen.Shelf.Empty ?? []);
        Assert.Contains("nothing to show on this shelf right now", block, StringComparison.Ordinal);
        Assert.Contains("another family might", block, StringComparison.Ordinal);
        //neither key lifts anything in this state, so the text may not promise either one.
        Assert.DoesNotContain("a shows all", block, StringComparison.Ordinal);
        Assert.DoesNotContain("all shows them", block, StringComparison.Ordinal);
    }

    //an empty shelf opens where the emptiness came from, the search box after a failed search and the chip row after a failed browse
    [Fact]
    public void AN_EMPTY_SHELF_OPENS_WHERE_THE_EMPTYING_CAME_FROM()
    {
        var (searching, _) = OnTheHubShelf();
        var searchEmpty = Assert.IsType<WizardScreen.Choice>(
            searching.Answer(ShelfControls.TypedAnswer("LFM")));
        Assert.Empty(searchEmpty.Shelf!.Rows);
        Assert.Equal(Region.Search, Shelf.Opening(searchEmpty.Shelf));

        //the chip half needs its own probes (that fixture's chip clears the word, so the browse answers rows and the shelf is not empty)
        IReadOnlyList<ShelfRow> browse = [Row("o/one", "gemma3")];
        var calls = 0;
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = browse,
            Answer = _ => ++calls == 1
                ? new HubSearchOutcome(browse, null, null)
                : new HubSearchOutcome([], null, null, HiddenByKind: 4),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        var chipEmpty = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(SetupFlow.CtlFamily + Family));
        Assert.Empty(chipEmpty.Shelf!.Rows);
        Assert.Equal(Region.Families, Shelf.Opening(chipEmpty.Shelf));
    }

    //a Hub that goes unreachable mid-run must show its cause, or the empty-shelf block hides it. the fixture fails the second fetch so the kept frame is set first
    [Fact]
    public void A_HUB_THAT_GOES_UNREACHABLE_MID_WALK_DOES_NOT_GET_THE_SHELF_FRAME()
    {
        IReadOnlyList<ShelfRow> browse = [Row("o/one", "gemma3")];
        var calls = 0;
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Rows = browse,
            Answer = _ => ++calls == 1
                ? new HubSearchOutcome(browse, null, null)
                : new HubSearchOutcome([], HubSearchCause.HubFailed, null),
        };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        var screen = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(SetupFlow.CtlFamily + Family));

        Assert.Null(screen.Shelf);
        Assert.Contains(screen.BodyRows!,
            r => r.Text.Contains("couldn't reach Hugging Face", StringComparison.Ordinal));
        Assert.DoesNotContain(screen.BodyRows!,
            r => r.Text.Contains("nothing to show on this shelf", StringComparison.Ordinal));
    }
}
