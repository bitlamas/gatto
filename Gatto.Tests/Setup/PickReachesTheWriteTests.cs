using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Tests.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//a quant picked in the pane must reach the download and the profile. every fixture makes the default and the pick disagree so the two are tellable
public class PickReachesTheWriteTests
{
    private const string Knee = "Q4_K_M";
    private const string Picked = "Q6_K";

    //the reference a pick names: the row's publisher, its repo and the file holding this token
    private static FileRef Ref(string token) =>
        new("unsloth", "unsloth/gemma-4-26B-A4B-it", $"gemma-4-26B-A4B-it-{token}.gguf");

    private static HubQuant Quant(string token, double gb) =>
        new($"gemma-4-26B-A4B-it-{token}.gguf", (long)(gb * 1024 * 1024 * 1024), null);

    //the repo lists its quants heaviest-first and the pane lists them lightest-first, so a pick matched by index takes the wrong file here
    private static ModelRow Row() =>
        ShelfRows.Of("unsloth/gemma-4-26B-A4B-it", "unsloth", Quant(Knee, 16.9),
            FitRegime.FitsGpu, 262144, false, Badge: null, Downloads: 5, Gated: false,
            Params: 25_200_000_000,
            AllQuants: [Quant(Picked, 21.1), Quant(Knee, 16.9)]);

    private static SetupFlow FlowAtTheShelf(out WizardScreen.Choice shelf)
    {
        var probes = new WizardProbes { Rows = [Row()] };
        var flow = new SetupFlow(probes) { CanSwitchSource = true };
        shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        return flow;
    }

    //a pick replaces the row's quant for later steps, and the assertion reads the screen rather than the flow's private state
    [Fact]
    public void THE_PICKED_QUANT_IS_WHAT_THE_WALK_CARRIES_FORWARD()
    {
        var flow = FlowAtTheShelf(out var shelf);
        var rowKey = shelf.Options[0].Key;

        var next = flow.Answer(ShelfControls.PickAnswer(rowKey, Ref(Picked)));

        var text = Text(next);
        Assert.Contains(Picked, text, StringComparison.Ordinal);
        //the engine's own pick must be gone from the screen, a screen naming both reads as a choice the user still has to make
        Assert.DoesNotContain(Knee, text, StringComparison.Ordinal);
    }

    //without a pick the engine's choice must stand, reading sizes in the pane changes nothing
    [Fact]
    public void AND_AN_UNPICKED_WALK_STILL_CARRIES_THE_ENGINES_CHOICE()
    {
        var flow = FlowAtTheShelf(out var shelf);

        var next = flow.Answer(shelf.Options[0].Key);

        var text = Text(next);
        Assert.Contains(Knee, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Picked, text, StringComparison.Ordinal);
    }

    //a pick must be matched by label, the fixture reverses the two orderings so a position match takes the wrong quant here
    [Fact]
    public void THE_PICK_IS_MATCHED_BY_LABEL_SO_THE_TWO_ORDERINGS_CANNOT_CROSS()
    {
        var flow = FlowAtTheShelf(out var shelf);

        var next = flow.Answer(ShelfControls.PickAnswer(shelf.Options[0].Key, Ref(Knee)));

        //picking the knee by name must yield the knee even though it sits last in the repo's list.
        Assert.Contains(Knee, Text(next), StringComparison.Ordinal);
    }

    //a label no quant has cannot be honoured, the engine's choice must stand unchanged
    [Fact]
    public void AN_UNRESOLVABLE_PICK_CHANGES_NOTHING()
    {
        var flow = FlowAtTheShelf(out var shelf);

        var next = flow.Answer(ShelfControls.PickAnswer(shelf.Options[0].Key, Ref("IQ1_S")));

        Assert.Contains(Knee, Text(next), StringComparison.Ordinal);
    }

    //the face half of the seam, the keyboard must send the same wrapped pick answer the flow reads
    [Fact]
    public void THE_FACE_SENDS_THE_PICK_OUT_AT_THE_LISTS_ENTER()
    {
        ModelRow[] rows = [Row()];
        var shelf = new ShelfView(rows, Gatto.Core.Hardware.MachineShape.UnifiedWithShare,
            Total: 1,
            Facts: [new ModelFacts(Files: [
                new PaneFile(Knee, (long)(16.9 * 1024 * 1024 * 1024), FitRegime.FitsGpu, Ref(Knee)),
                new PaneFile(Picked, (long)(21.1 * 1024 * 1024 * 1024), FitRegime.FitsGpu, Ref(Picked))])]);
        var c = new WizardScreen.Choice(SetupFlow.SearchKey, "Which model should gatto start with?",
            [new ChoiceOption("0", rows[0].RowFile!.RepoId)], Shelf: shelf, Door: SetupFlow.ShelfDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode));

        var ring = Shelf.Regions(shelf, 100, 0, hasDoor: true).ToList();
        var toFiles = (ring.IndexOf(Region.Files) - ring.IndexOf(Region.List) + ring.Count) % ring.Count;
        var back = ring.Count - toFiles;

        //the keystrokes, into the file pane, down to the heavier quant, Enter to pick, back to the list, Enter to proceed
        var answer = WalkRender.AnswerAfter(c, 100, [
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), toFiles),
            Key(ConsoleKey.DownArrow),
            Key(ConsoleKey.Enter),
            .. Enumerable.Repeat(Key(ConsoleKey.Tab), back),
            Key(ConsoleKey.Enter),
        ]);

        Assert.Equal(ShelfControls.PickAnswer("0", Ref(Picked)), answer);
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);

    //joins everything the screen shows (question, body rows, option labels), a guard on private state would pass while the shown quant is wrong
    private static string Text(WizardScreen s) => s switch
    {
        WizardScreen.Choice c => string.Join("\n",
            new[] { c.Question }
                .Concat((c.BodyRows ?? []).Select(r => r.Text))
                .Concat(c.Options.Select(o => o.Label + " " + o.Description))),
        WizardScreen.Ask a => string.Join("\n", (a.BodyRows ?? []).Select(r => r.Text)),
        _ => s.ToString() ?? "",
    };
}
