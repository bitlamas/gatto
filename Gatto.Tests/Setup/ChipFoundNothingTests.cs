using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//a chip press that leaves nothing fitting shows the cat and one chip-scoped sentence, keeps the chips row, and drops the catalogue-wide one.
public class ChipFoundNothingTests
{
    private static ModelRow Ranked(string id, string arch) => ShelfRows.Of(
        RepoId: id, Publisher: id.Split('/')[0],
        PickedQuant: new HubQuant("m-Q4_K_M.gguf", 18_000_000_000, null),
        Fit: FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
        Downloads: 900, Gated: false, Params: 3_000_000_000, Arch: arch);

    private static (SetupFlow Flow, WizardScreen.Choice Screen) AfterChip(
        string chip, int tooBig = 27, int otherFamilies = 0, HubSearchCause? cause = HubSearchCause.NothingFits,
        GlyphSet? set = null)
    {
        IReadOnlyList<ModelRow> rows = [Ranked("o/qwen-small", "qwen3"), Ranked("o/qwen-tiny", "qwen3")];
        var probes = new WizardProbes
        {
            Rows = rows,
            //the fake gives the too-big rows back to a lift (empty answers for every family would make the lift guard unfalsifiable).
            Answer = req => req.Family() is null || req.Lifted
                ? WizardProbes.Outcome(rows, null)
                : WizardProbes.Outcome([], cause, moreBehindA: tooBig > 0),
        };
        var flow = new SetupFlow(probes);
        //the flow owns the glyph set, it composes the cat and the sentence. a test that sets it on the painter alone would still draw a Unicode cat and call it ASCII.
        if (set is not null) flow.Glyphs = set;
        Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
        return (flow, Assert.IsType<WizardScreen.Choice>(ChipWalk.Narrow(flow, chip)));
    }

    private static string PaintedIn(GlyphSet g, WizardScreen.Choice screen, int width = 100) =>
        string.Join("\n", Shelf.Body(screen.Shelf!, row: 0, chip: 0, file: -1,
            Region.List, width, glyphs: g).Select(r => r.Text));

    private static string Painted(WizardScreen.Choice screen, int width = 100) =>
        PaintedIn(GlyphSet.Unicode, screen, width);

    //assert on the screen the chip press reaches (a screen built by hand would prove nothing).
    [Fact]
    public void A_CHIP_THAT_LEAVES_NOTHING_THAT_FITS_DRAWS_THE_CAT_AND_ONE_SENTENCE()
    {
        var (_, screen) = AfterChip("gemma");

        Assert.NotNull(screen.Shelf);
        var painted = Painted(screen);

        Assert.Contains(Gatto.Repl.Cats.EmptyOf(GlyphSet.Unicode), painted, StringComparison.Ordinal);
        Assert.Contains(
            $"No gemma model fits this machine's memory {GlyphSet.Unicode.Dot} a shows all",
            painted, StringComparison.Ordinal);
    }

    //the chips row stays on an empty result, otherwise the user can't see which chip emptied the shelf.
    [Fact]
    public void THE_CHIPS_ROW_SURVIVES_SO_THE_LIT_CHIP_IS_STILL_ON_SCREEN()
    {
        var (_, screen) = AfterChip("gemma");

        var painted = Painted(screen);
        foreach (var family in Families.Load().Ladder)
            Assert.Contains(family, painted, StringComparison.Ordinal);
    }

    //the catalogue-wide sentence must not render under a chip, the catalogue claim is false for one family.
    [Fact]
    public void THE_CATALOGUE_WIDE_SENTENCE_DOES_NOT_ALSO_RENDER()
    {
        var (_, screen) = AfterChip("gemma");

        Assert.DoesNotContain(screen.BodyRows ?? [],
            r => r.Text.Contains("needs more than that", StringComparison.Ordinal));
    }

    //an empty search with no chip keeps the catalogue-wide sentence, the new chip sentence must not reach it.
    [Fact]
    public void AN_EMPTY_SEARCH_WITH_NO_CHIP_KEEPS_THE_CATALOGUE_WIDE_SENTENCE()
    {
        var probes = new WizardProbes
        {
            Rows = [],
            Answer = _ => WizardProbes.Outcome([], HubSearchCause.NothingFits),
        };
        var screen = Assert.IsType<WizardScreen.Choice>(new SetupFlow(probes).StartPastEngine());

        Assert.Contains(screen.BodyRows ?? [],
            r => r.Text.Contains("needs more than that", StringComparison.Ordinal));
    }

    //an unreachable hub fetched no ladder, so the chip state is keyed on an active chip and not on empty rows.
    [Fact]
    public void A_HUB_THAT_COULD_NOT_BE_REACHED_STILL_COLLAPSES_TO_TWO_REGIONS()
    {
        var view = new ShelfView([], Gatto.Core.Hardware.MachineShape.Discrete);

        Assert.True(view.NothingFetched);
        Assert.Equal([Region.Search], Shelf.Regions(view, 100, -1, hasDoor: false));
    }

    //the sentence names a, so the key must be bound and must lead somewhere. press it, an offered key is not enough.
    [Fact]
    public void THE_SENTENCE_NAMES_a_AND_THE_SCREEN_ANSWERS_IT()
    {
        var (flow, screen) = AfterChip("gemma");

        Assert.True(ShelfControls.OffersLift(screen));

        var lifted = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlLift));
        Assert.NotEmpty(lifted.Shelf!.Rows);
    }

    //the heading must not make the catalogue-wide claim the body dropped, else the defect just moves up a row.
    [Fact]
    public void THE_HEADING_DOES_NOT_MAKE_THE_CLAIM_THE_BODY_LOST()
    {
        var (_, screen) = AfterChip("gemma");

        Assert.Equal(SetupFlow.ModelTitleFor(inSession: false), screen.Question);
    }

    //a chip that hides nothing is not the empty state, neither key would lead anywhere. the search's own copy keeps the cause this screen can't show.
    [Fact]
    public void A_CHIP_THAT_HIDES_NOTHING_EITHER_WAY_KEEPS_THE_SEARCHS_OWN_COPY()
    {
        var (_, screen) = AfterChip("mistral", tooBig: 0, otherFamilies: 0,
            cause: HubSearchCause.HubFailed);

        Assert.Null(screen.Shelf);
        Assert.Contains(screen.BodyRows ?? [],
            r => r.Text.Contains("couldn't reach Hugging Face", StringComparison.Ordinal));
    }

    //both sentences must read under the ASCII set, a unicode separator would draw a box. the dot check catches a screen composed under unicode.
    [Fact]
    public void BOTH_SENTENCES_READ_ON_A_HOST_THAT_CANNOT_DRAW_THE_SET()
    {
        var a = GlyphSet.Ascii;
        var (_, fit) = AfterChip("gemma", set: a);

        foreach (var (screen, sentence) in new[]
        {
            (fit, $"No gemma model fits this machine's memory {a.Dot} a shows all"),
        })
        {
            var painted = PaintedIn(a, screen);
            Assert.Contains(Gatto.Repl.Cats.EmptyOf(a), painted, StringComparison.Ordinal);
            Assert.Contains(sentence, painted, StringComparison.Ordinal);
            Assert.DoesNotContain(GlyphSet.Unicode.Dot, painted, StringComparison.Ordinal);
        }
    }

    //a screen that draws nothing also satisfies the no-dot assertion, so this case shows the same painting does produce the unicode dot.
    [Fact]
    public void AND_THE_SAME_WALK_UNDER_THE_UNICODE_SET_CARRIES_WHAT_THE_ASCII_ONE_MUST_NOT()
    {
        var (_, screen) = AfterChip("gemma", set: GlyphSet.Unicode);

        Assert.Contains(GlyphSet.Unicode.Dot, Painted(screen), StringComparison.Ordinal);
    }

    //assert the whole frame as a terminal shows it, so the ascii sweep, the width check and the dash census can see this screen.
    [Fact]
    public void THE_WHOLE_FRAME_MATCHES_ITS_GOLDEN()
    {
        var (_, screen) = AfterChip("gemma");

        Tui.Golden.AssertEquals("s5", "discrete-8-chip-empty", 100,
            Tui.WalkRender.SettledFrame(screen, 100, SetupFlow.SearchKey).Rows);
    }
}
