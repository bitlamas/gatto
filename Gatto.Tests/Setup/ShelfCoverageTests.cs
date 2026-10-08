using System.Text.RegularExpressions;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//every fact the old detail line showed must stay reachable. the oracle is the frames on screen, since a prompt erases its own block
public class ShelfCoverageTests
{
    //an architecture the pinned build cannot load. the constant must stay one, or the arch census passes vacuously when the supported set grows
    private const string UnloadableArch = "gatto-test-unknown-arch";

    private static readonly Badge TheBadge = new(
        "bartowski/Qwen3-Coder-GGUF", new DateOnly(2026, 8, 1), "v0.4.0", "greedy",
        Passed: 4, Ran: 5);

    //the fixture's numbers must not match anything else on screen, or a census can pass on the wrong text
    private static ModelRow TheRow() => ShelfRows.Of(
        RepoId: "bartowski/Qwen3-Coder-GGUF",
        Publisher: "bartowski",
        PickedQuant: new HubQuant("Qwen3-Coder-Q4_K_M.gguf", 4_000_000_000, null),
        Fit: FitRegime.FitsGpu,
        NativeCtx: 32768,
        Vision: true,
        Badge: TheBadge,
        Downloads: 1234,
        Gated: false,
        LastModified: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        Params: 30_500_000_000,
        AllQuants: null,
        Projectors: [new HubQuant("mmproj-Qwen3-8B-f16.gguf", 700_000_000, null)],
        Arch: UnloadableArch);

    //classifies the fake's machine the way the flow does, so the expected fit words match the rendered ones
    private static MachineShape Shape() =>
        HardwareClassifier.Classify(new WizardProbes().Hardware()!).Shape;

    //drives the real flow, then renders through the real face. a census that built its own screen would miss a flow that stopped producing one
    private static List<string> ShelfFrames(int width = 100)
    {
        var flow = new SetupFlow(new WizardProbes { Rows = [TheRow()] });
        var shelf = Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        Assert.Equal(SetupFlow.SearchKey, shelf.Key);

        var rig = new WizardRig(width);
        var picked = rig.Face(WizardRig.Enter).Choose(shelf);
        Assert.NotNull(picked);
        Assert.NotEmpty(rig.Frames);

        //the pick returns the model's page, rendered through the same rig so its frames are captured too
        var page = Assert.IsType<WizardScreen.Choice>(flow.Answer(picked!));
        rig.Face(WizardRig.Enter).Choose(page);

        return rig.Frames;
    }

    //whitespace is collapsed on both sides, since a wrapped fact would read as missing. where a fact sits is a layout claim
    private static bool Reachable(IEnumerable<string> frames, string fact)
    {
        var needle = Collapse(fact);
        return frames.Any(f => Collapse(f).Contains(needle, StringComparison.Ordinal));
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static void AssertReachable(List<string> frames, string label, string fact)
    {
        //the fixture must show the fact itself, or the census claim about it is vacuous.
        Assert.False(string.IsNullOrWhiteSpace(fact), $"{label}: the fixture produced no such fact");
        Assert.True(Reachable(frames, fact),
            $"{label} is not reachable on the shelf: expected \"{fact}\"\n\n"
            + string.Join("\n----- frame -----\n", frames));
    }

    [Fact]
    public void The_model_IDENTITY_is_reachable()
    {
        //this fixture is broadened, so the publisher stays on the row and the full repo id must be on screen. a user has to recognise it and may have to type it
        AssertReachable(ShelfFrames(), "identity", TheRow().Model);
    }

    [Fact]
    public void The_download_SIZE_is_reachable()
        => AssertReachable(ShelfFrames(), "size", SearchRow.Gb(TheRow().RowQuant!.Bytes));

    [Fact]
    public void The_FIT_words_are_reachable()
        => AssertReachable(ShelfFrames(), "fit", SearchRow.FitWords(TheRow().Fit, Shape(), Gatto.Terminal.GlyphSet.Unicode));

    [Fact]
    public void The_CONTEXT_SIZE_is_reachable()
    {
        //this test asks whether the fact survived somewhere on the run. that it sits beside the model is its own test, since that makes it usable while choosing
        AssertReachable(ShelfFrames(), "context size", SearchRow.Ctx(TheRow().NativeCtx!.Value));
    }

    [Fact]
    public void The_VISION_capability_is_reachable()
    {
        //the fixture ships a projector, so the glyph and the encoder file name both exist to be found.
        var frames = ShelfFrames();
        AssertReachable(frames, "vision glyph", Gatto.Terminal.GlyphSet.Unicode.Vision);
        AssertReachable(frames, "vision encoder", ProjectorPick.Best(TheRow().RowProjectors)!.FileName);
    }

    //the needle is the marker the table draws
    [Fact]
    public void The_ARCHITECTURE_marker_is_reachable()
    {
        //the precondition runs first, since a pinned set that learns this architecture would make every claim below vacuous
        var marker = ArchNote.Marker(TheRow());
        Assert.False(string.IsNullOrEmpty(marker),
            $"fixture no longer exhibits an unloadable architecture, \"{UnloadableArch}\" is now supported");

        AssertReachable(ShelfFrames(), "arch marker", marker!);
    }

    //the badge words render only on the TUI pane, and this census draws the plain face. the skip ends when the plain face shows those words
    [Fact(Skip = "the words render on the TUI pane (Pane.Rows' only caller is Shelf.cs:514) and ShelfFrames renders the PLAIN face, which has no pane, so on that face a badge is a bare tick with no words, and a bare tick beside a model name reads as an endorsement. Reported; the fix is a design question. Un-skips when the plain face carries the words or is retired.")]
    public void THE_BADGES_WORDS_SURVIVE_THE_SWAP_label_date_AND_margin()
    {
        //a bare tick beside a model name reads as an endorsement, so the badge needs its words and date. the words come from SearchRow.BadgeWordsBare
        AssertReachable(ShelfFrames(), "badge words", SearchRow.BadgeWordsBare(TheBadge));
    }

    //these assert a column on the model's row. the download URL holds the file name, so a presence check would pass with an empty table

    //rendered at 120 columns so the model's row cannot wrap. a continuation line would split the fact from the identity
    private static void AssertOnTheModelRow(string label, string fact)
    {
        var id = TheRow().Model;
        var rows = ShelfFrames(120).SelectMany(f => f.Split('\n'))
            .Where(r => r.Contains(id, StringComparison.Ordinal)).ToList();

        Assert.True(rows.Count > 0, $"{label}: no rendered row carries the model's identity at all");
        Assert.True(rows.Any(r => r.Contains(fact, StringComparison.Ordinal)),
            $"{label} is not a COLUMN on the model's row: expected \"{fact}\" beside \"{id}\"\n  "
            + string.Join("\n  ", rows));
    }

    [Fact]
    public void The_PARAMETER_COUNT_is_a_column_on_the_model_row()
    {
        //the parameter count is fetched but drawn nowhere, so this test fails until the table shows it
        AssertOnTheModelRow("params", "30.5B");
    }

    [Fact]
    public void The_QUANT_token_is_a_column_on_the_model_row()
    {
        var quant = QuantToken.Of(TheRow().RowQuant!.FileName);
        Assert.False(string.IsNullOrEmpty(quant), "fixture's file name no longer yields a quant token");
        AssertOnTheModelRow("quant", quant!);
    }

    [Fact(Skip = "homeless until the model's page is built as the one home for the facts: the model's page carried this and the pane never gained it. Re-point, do not delete, the property is ruled and only its home is missing. Un-skips in the commit that gives the pane the facts.")]
    public void THE_MODELS_PAGE_ANNOUNCES_VISION_EXACTLY_ONCE()
    {
        //the oracle counts the word vision rather than the glyph. the fixture ships one projector, so no second mention is legitimate
        Assert.Single(TheRow().RowProjectors!);

        var page = ShelfFrames()[^1].Split('\n')
            .Where(r => r.Contains("vision", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(page.Count == 1,
            $"the model's page states the vision capability {page.Count} times, expected once:\n  "
            + string.Join("\n  ", page));
    }

    [Fact]
    public void The_CONTEXT_SIZE_is_a_column_on_the_model_row()
    {
        //the context window changes the pick, so it must sit beside the model. the page line with the same size has no repo id, so only the table row can pass
        AssertOnTheModelRow("context", SearchRow.Ctx(TheRow().NativeCtx!.Value));
    }
}
