using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//drives the shelf's controls through the real face. presses commit nothing and the pick commits once, and flow state cannot tell the two apart
public class ShelfControlsTests
{
    //joins rendered rows into one plain string, with escapes removed and whitespace runs collapsed. a wrapping sentence is still found as one sentence
    private static string Flat(IEnumerable<string> rows) =>
        System.Text.RegularExpressions.Regex.Replace(
            string.Join(" ", rows.Select(Gatto.Terminal.TermText.StripAnsiForWidth)), @"\s+", " ");

    //each row gets a distinct download and parameter count, so a re-sort is observable. the Row fixture's identical stats would hide a shelf out of order
    private static ShelfRow Ranked(string id, long downloads, long parameters) => new(
        RepoId: id, Publisher: id.Split('/')[0],
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 18_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: downloads, Gated: false,
        LastModified: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        Params: parameters, AllQuants: null, Projectors: null, Arch: "qwen3");

    private static ShelfRow Row(string id, bool badge = false) => new(
        RepoId: id, Publisher: id.Split('/')[0],
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 18_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false,
        Badge: badge ? new Badge(id, new DateOnly(2026, 8, 1), "v", "greedy", Passed: 5, Ran: 5) : null,
        Downloads: 100, Gated: false,
        LastModified: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        Params: 30_500_000_000, AllQuants: null, Projectors: null, Arch: "qwen3");

    private static (SetupFlow Flow, WizardProbes Probes, WizardScreen.Choice Shelf) AtShelf(
        string? curated = "unsloth", int hidden = 0)
    {
        var probes = new WizardProbes
        {
            Rows = [Row("unsloth/a-GGUF", badge: true), Row("unsloth/b-GGUF")],
            Curated = curated,
            Hidden = hidden,
        };
        var flow = new SetupFlow(probes);
        //the fixture machine has an engine, so the shelf sits one answer past the screen this returns
        return (flow, probes, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()));
    }

    //a helper's name is a claim about the code, so it names the screen the flow reaches. this one stops at the engine step's first screen
    private static (SetupFlow Flow, WizardProbes Probes, WizardScreen.Choice Entry) AtEngineStep()
    {
        var probes = new WizardProbes
        {
            Rows = [Row("unsloth/a-GGUF", badge: true), Row("unsloth/b-GGUF")],
            Curated = "unsloth",
        };
        var flow = new SetupFlow(probes);
        return (flow, probes, Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening()));
    }

    //one face drives the whole sequence, or each press starts a fresh transcript. the first screen is answered on purpose, so a stale height has a block to reclaim
    private static (WizardRig Rig, WizardScreen.Choice Last) Walk(
        SetupFlow flow, WizardScreen.Choice fork, params char[] presses)
    {
        var rig = new WizardRig(100);
        var keys = new[] { WizardRig.Enter }
            .Concat(presses.Select(WizardRig.Ch)).Append(WizardRig.Enter).ToArray();
        var face = rig.Face(keys);

        //answers the screen above the shelf through the real transcript, so the walk starts from a committed block.
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(face.Choose(fork)!));

        for (var i = 0; i < presses.Length; i++)
        {
            var answer = face.Choose(screen);
            Assert.NotNull(answer);
            screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(answer!));
        }
        face.Choose(screen);
        return (rig, screen);
    }

    [Fact]
    public void N_CONTROL_PRESSES_COMMIT_NOTHING_and_the_pick_commits_once()
    {
        //the oracle is the final scrollback, since a tail written then erased leaves the same frames in between. three presses must leave one block in the transcript
        var (flow, _, fork) = AtEngineStep();
        var (rig, _) = Walk(flow, fork, 's', 's', 'a');

        //the model's name appears once, as the committed answer. a tail per control press would show that block four times
        Assert.Equal(1, rig.Occurrences("unsloth/a-GGUF"));

        //a missing Skip() is not caught here, since its reclaim needs an earlier commit. the title is checked, since a committed block keeps question and answer
        Assert.Equal(1, rig.Occurrences(SetupFlow.EngineTitle));
    }

    [Fact]
    public void A_PICK_ALONE_COMMITS_THE_SAME_ONE_BLOCK()
    {
        //without this case, one block after three presses would also be true of a face that committed nothing
        var (flow, _, fork) = AtEngineStep();
        var (rig, _) = Walk(flow, fork);

        Assert.Equal(1, rig.Occurrences("unsloth/a-GGUF"));
        //the claim is that the block survived. the engine step's title works here, since a committed block keeps the question and answer and drops the options
        Assert.Equal(1, rig.Occurrences(SetupFlow.EngineTitle));
    }

    [Fact]
    public void EACH_CONTROL_ASKS_THE_ENGINE_A_DIFFERENT_QUESTION()
    {
        //the flow's job is to ask for the right thing, so this asserts the request. the engine's behaviour has its own tests
        var (flow, probes, shelf) = AtShelf();
        Assert.Equal(HubSearchView.Curated, probes.LastRequest!.View);
        Assert.Null(probes.LastRequest.Axis);
        Assert.False(probes.LastRequest.IncludeUnfittable);

        //the publisher control only opens a screen, so the pick that follows sends the request
        flow.Answer(SetupFlow.CtlPublisher);
        flow.Answer(SetupFlow.PickEveryPublisher);
        Assert.Equal(HubSearchView.Broadened, probes.LastRequest!.View);

        flow.Answer(SetupFlow.CtlLift);
        Assert.True(probes.LastRequest!.IncludeUnfittable);

        flow.Answer(SetupFlow.CtlSort);
        Assert.NotNull(probes.LastRequest!.Axis);
    }

    [Fact]
    public void THE_PUBLISHER_CONTROL_IS_A_TOGGLE_and_goes_back()
    {
        //a key must be reversible, so the publisher control offers both views. the picker names each publisher, so the round trip is two picks
        var (flow, probes, _) = AtShelf();

        flow.Answer(SetupFlow.CtlPublisher);
        flow.Answer(SetupFlow.PickEveryPublisher);
        Assert.Equal(HubSearchView.Broadened, probes.LastRequest!.View);

        flow.Answer(SetupFlow.CtlPublisher);
        flow.Answer(SetupFlow.PickPublisher + "unsloth");
        Assert.Equal(HubSearchView.Curated, probes.LastRequest!.View);
        Assert.Equal("unsloth", probes.LastRequest!.Publisher);
    }

    [Fact]
    public void SORTING_NEVER_EMPTIES_THE_SHELF_and_re_sorts_what_is_in_hand()
    {
        //an axis is part of the query, so a sort can empty the shelf on a tight machine. the oracle is a screen with no model rows, since other code prints sentences
        IReadOnlyList<ShelfRow> rows =
            [Ranked("o/popular", 900, 3_000_000_000), Ranked("o/huge", 3, 70_000_000_000)];
        var probes = new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            //the first search, with no axis, returns the rows. every axis after it finds nothing new.
            Answer = req => req.Axis is null
                ? new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0)
                : new HubSearchOutcome([], HubSearchCause.NothingFits, "unsloth", HiddenByFit: 0),
        };

        var flow = new SetupFlow(probes);
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.Equal(2, shelf.Options.Count(o => o.Key is "0" or "1"));

        var sorted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSort));

        //the two model rows must survive a sort whose search found nothing.
        Assert.Equal(2, sorted.Options.Count(o => o.Key is "0" or "1"));

        //the rows must follow the axis the cycle reached. a header that says largest first over a downloads-ordered shelf is a false claim
        var axis = probes.LastRequest!.Axis!.Value;
        var expected = HubSearch.Arrange(rows, axis, HubSearch.OrderFor(HubSearchView.Curated),
            _ => null).Select(r => r.RepoId).ToList();
        Assert.Equal(expected, sorted.Shelf!.Rows.Select(r => r.RepoId).ToList());

        //the header must name the axis the rows are in, so the two cannot disagree
        Assert.Contains(ShelfBinding.AxisWords(axis), ShelfBinding.StateSentence(sorted.Shelf!, Gatto.Terminal.GlyphSet.Unicode),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_CARRIED_OVER_SHELF_SAYS_SO_and_a_successful_sort_stays_quiet()
    {
        //a sort that found nothing keeps the old rows, so the header's order says nothing about the search. the body must say these are the rows already shown
        IReadOnlyList<ShelfRow> rows =
            [Ranked("o/popular", 900, 3_000_000_000), Ranked("o/huge", 3, 70_000_000_000)];
        var probes = new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            Answer = req => req.Axis is null
                ? new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 0)
                : new HubSearchOutcome([], HubSearchCause.NothingFits, "unsloth", HiddenByFit: 0),
        };

        var flow = new SetupFlow(probes);
        var before = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        //a sort whose search succeeded adds no sentence, since a line printed on every sort is noise.
        Assert.DoesNotContain("already shown", Flat(ShelfBinding
            .For(before.Shelf!, [], new Gatto.Terminal.Theme(Gatto.Terminal.TermCaps.Plain), glyphs: GlyphSet.Unicode)
            .HeadingsAt(100).Select(h => h.Text)), StringComparison.Ordinal);

        var after = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlSort));

        //the guard reads the rendered headings, where the prose appears. escapes are stripped and whitespace collapsed first, since painting resets mid-sentence
        var body = Flat(ShelfBinding
            .For(after.Shelf!, [], new Gatto.Terminal.Theme(Gatto.Terminal.TermCaps.Plain), glyphs: GlyphSet.Unicode)
            .HeadingsAt(100).Select(h => h.Text));

        Assert.Contains("found none that fit this machine", body, StringComparison.Ordinal);
        Assert.Contains("these are the models already shown", body, StringComparison.Ordinal);

        //the shelf priced a bounded window and stopped, so the line may never claim nothing bigger fits
        Assert.DoesNotContain("nothing bigger", body, StringComparison.OrdinalIgnoreCase);

        //the sentence explains the shelf and does not replace it, so the rows must still be there.
        Assert.Equal(2, after.Options.Count(o => o.Key is "0" or "1"));
    }

    [Fact]
    public void AN_EMPTY_BROADENED_SHELF_DOES_NOT_OFFER_TO_BROADEN()
    {
        //the widen row exists only when a curated publisher is set, and this asserts the screen rather than a report of it
        var probes = new WizardProbes { Rows = [], Curated = null };
        var flow = new SetupFlow(probes);
        var empty = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());

        //with no publisher to widen from, the widen row must not appear.
        Assert.DoesNotContain(empty.Options, o => o.Key == SetupFlow.Broaden);
        //an empty shelf must keep one correct way out, or the user has no move left.
        Assert.Contains(empty.Options, o => o.Key == SetupFlow.TypeAnId);
    }

    [Fact]
    public void THE_SORT_CYCLE_REACHES_EVERY_AXIS_and_never_size()
    {
        //the cycle omits size on purpose, since the axis decides which repos get a tree call and a size is known only after one
        var (flow, probes, _) = AtShelf();
        var seen = new List<SearchOrder>();

        for (var i = 0; i < 6; i++)
        {
            flow.Answer(SetupFlow.CtlSort);
            seen.Add(probes.LastRequest!.Axis!.Value);
        }

        Assert.Equal(
            //the curated default is MostDownloaded, so the first press gives RecentlyUpdated. the whole six-press sequence is asserted, so a moved start is visible
            [SearchOrder.RecentlyUpdated, SearchOrder.MostParams, SearchOrder.VerifiedFirst,
             SearchOrder.MostDownloaded, SearchOrder.RecentlyUpdated, SearchOrder.MostParams],
            seen);
    }

    [Fact]
    public void THE_SEARCH_CONTROL_OPENS_THE_TYPED_ID_DOOR()
    {
        //the same entry a row opens, reached here by the search control
        var (flow, _, _) = AtShelf();
        var ask = Assert.IsType<WizardScreen.Ask>(flow.Answer(SetupFlow.CtlSearch));

        Assert.Equal(SetupFlow.TypedIdKey, ask.Key);
    }

    [Fact]
    public void A_CONTROL_PRESS_PUTS_THE_CURSOR_BACK_where_the_user_left_it()
    {
        //without this, a control press loses the row the user was reading. the oracle is the cursor glyph on that row, since state can hold the number without sending it
        var (flow, _, shelf) = AtShelf();
        var rig = new WizardRig(100);
        var face = rig.Face(WizardRig.Down, WizardRig.Ch('s'), WizardRig.Enter);

        var answer = face.Choose(shelf);
        var next = Assert.IsType<WizardScreen.Choice>(flow.Answer(answer!));
        face.Choose(next);

        var frame = rig.Frames[^1].Split('\n').Single(r => r.Contains("❯", StringComparison.Ordinal));
        Assert.Contains("b-GGUF", frame, StringComparison.Ordinal);
    }
}
