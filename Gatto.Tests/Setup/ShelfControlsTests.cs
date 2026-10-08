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
    private static ModelRow Ranked(string id, long downloads, long parameters) => ShelfRows.Of(
        RepoId: id, Publisher: id.Split('/')[0],
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 18_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: downloads, Gated: false,
        LastModified: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        Params: parameters, AllQuants: null, Projectors: null, Arch: "qwen3");

    private static ModelRow Row(string id, bool badge = false) => ShelfRows.Of(
        RepoId: id, Publisher: id.Split('/')[0],
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 18_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false,
        Badge: badge ? new Badge(id, new DateOnly(2026, 8, 1), "v", "greedy", Passed: 5, Ran: 5) : null,
        Downloads: 100, Gated: false,
        LastModified: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        Params: 30_500_000_000, AllQuants: null, Projectors: null, Arch: "qwen3");

    private static (SetupFlow Flow, WizardProbes Probes, WizardScreen.Choice Shelf) AtShelf()
    {
        var probes = new WizardProbes
        {
            Rows = [Row("unsloth/a-GGUF", badge: true), Row("unsloth/b-GGUF")],
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
        var (rig, _) = Walk(flow, fork, 'a', 'a', 'a');

        //the model's name appears once, as the committed answer. a tail per control press would show that block four times
        Assert.Equal(1, rig.Occurrences("a-GGUF"));

        //a missing Skip() is not caught here, since its reclaim needs an earlier commit. the title is checked, since a committed block keeps question and answer
        Assert.Equal(1, rig.Occurrences(SetupFlow.EngineTitle));
    }

    [Fact]
    public void A_PICK_ALONE_COMMITS_THE_SAME_ONE_BLOCK()
    {
        //without this case, one block after three presses would also be true of a face that committed nothing
        var (flow, _, fork) = AtEngineStep();
        var (rig, _) = Walk(flow, fork);

        Assert.Equal(1, rig.Occurrences("a-GGUF"));
        //the claim is that the block survived. the engine step's title works here, since a committed block keeps the question and answer and drops the options
        Assert.Equal(1, rig.Occurrences(SetupFlow.EngineTitle));
    }

    [Fact]
    public void EACH_CONTROL_ASKS_THE_ENGINE_A_DIFFERENT_QUESTION()
    {
        //the flow's job is to ask for the right thing, so this asserts the request. the engine's behaviour has its own tests
        var (flow, probes, shelf) = AtShelf();
        Assert.Equal(Families.Load().Landing, probes.LastRequest!.Lit.Order());
        Assert.False(probes.LastRequest.Lifted);

        flow.Answer(SetupFlow.CtlLift);
        Assert.True(probes.LastRequest!.Lifted);

        flow.Answer(ShelfControls.FamilyAnswer("glm"));
        Assert.Equal(["gemma", "glm", "qwen"], probes.LastRequest!.Lit.Order());
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
        var face = rig.Face(WizardRig.Down, WizardRig.Ch('a'), WizardRig.Enter);

        var answer = face.Choose(shelf);
        var next = Assert.IsType<WizardScreen.Choice>(flow.Answer(answer!));
        face.Choose(next);

        var frame = rig.Frames[^1].Split('\n').Single(r => r.Contains("❯", StringComparison.Ordinal));
        Assert.Contains("b-GGUF", frame, StringComparison.Ordinal);
    }
}
