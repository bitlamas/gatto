using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//the rules split across the table and the pane, so a test names its surface and guards what a row must not say
public class SearchRowTests
{
    private static ShelfRow Row(
        FitRegime fit = FitRegime.FitsGpu, bool vision = false, Badge? badge = null,
        long? ctx = 32768, long bytes = 4_000_000_000) =>
        new("org/model", "org", new HubQuant("model-Q4_K_M.gguf", bytes, null),
            fit, ctx, vision, badge, Downloads: 500, Gated: false);

    private const Gatto.Core.Hardware.MachineShape Discrete = Gatto.Core.Hardware.MachineShape.Discrete;

    //the table answers which row to choose, so assert on what it renders (a column that stops being emitted must fail here)
    private static string Table(ShelfRow r, Gatto.Core.Hardware.MachineShape shape = Discrete) =>
        string.Join(" · ", ShelfTable.Render([r], new Theme(TermCaps.Plain), 200, null, shape, glyphs: GlyphSet.Unicode)
            .Select(x => x.Text));

    //the pane answers what this row is, drive the production path so the tests can't pass against an unreachable screen
    private static string Pane(ShelfRow r) =>
        string.Join(" · ", Gatto.Cli.Setup.Tui.Pane
            .Rows(r, null, Discrete, 100, glyphs: GlyphSet.Unicode).Select(x => x.Text));

    //the tier line answers how these rows fit, drive it through ShelfBinding so the claim stays about what reaches a screen
    private static string TierLine(FitRegime fit)
    {
        var row = Row(fit);
        var view = new ShelfView([row], null, Discrete);
        var headings = ShelfBinding.For(view, [new Gatto.Repl.SelectOption(row.RepoId)],
            new Theme(TermCaps.Plain), glyphs: GlyphSet.Unicode).HeadingsAt(100);
        return string.Join(" · ", headings.Select(h => h.Text));
    }

    //use Both only for a rule that forbids something anywhere. a presence claim met by either surface would not see a fact on the wrong one.
    private static string Both(ShelfRow r, Gatto.Core.Hardware.MachineShape shape = Discrete) =>
        Table(r, shape) + " · " + Pane(r);

    [Fact]
    public void A_BADGE_says_what_was_MEASURED_and_never_recommends()
    {
        //the words must state what was measured, they live on the pane since a bare tick in the table reads as a recommendation
        var pane = Pane(Row(badge: new Badge("org/model", new DateOnly(2026, 8, 10), "abc1234", "temp 0.7")));

        Assert.Contains("tool-calling verified", pane, StringComparison.Ordinal);
        Assert.Contains("2026-08", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("recommend", pane, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("best", pane, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_BADGE_SHOWS_ITS_MARGIN_only_when_there_is_one()
    {
        //both halves belong in one test, since a line that does not move with the fact is the defect
        var badge = new Badge("org/model", new DateOnly(2026, 8, 10), "abc1234", "temp 0.7",
            Passed: 5, Ran: 5);

        var perfect = Pane(Row(badge: badge));
        var marginal = Pane(Row(badge: badge with { Passed = 4 }));

        Assert.DoesNotContain("tasks", perfect, StringComparison.Ordinal);
        Assert.Contains("4 of 5 tasks", marginal, StringComparison.Ordinal);

        //a bare fraction beside a model name reads as a rating out of five, so the unit is always named
        Assert.DoesNotContain("4/5", marginal, StringComparison.Ordinal);
        Assert.DoesNotContain("recommend", marginal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NO_BADGE_means_NO_CLAIM_rather_than_a_hedged_one()
    {
        //an unbadged row states nothing, a hedge such as not yet verified is still a claim about tool-calling, so check both surfaces
        var both = Both(Row(badge: null));

        Assert.DoesNotContain("verified", both, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unverified", both, StringComparison.OrdinalIgnoreCase);
    }

    //the parameter is an int, since a public theory method can't expose the internal enum, the cast happens inside the test
    [Theory]
    //each regime renders as its own plain words, since the screen must not name an internal concept
    [InlineData((int)FitRegime.FitsGpu, "graphics card")]
    [InlineData((int)FitRegime.FitsRamOnly, "memory")]
    [InlineData((int)FitRegime.DoesNotFit, "too big")]
    [InlineData((int)FitRegime.Unknown, "not known")]
    public void FIT_IS_SAID_AS_A_COMPUTATION_including_when_it_could_not_be_computed(int regime, string expected)
    {
        var fit = (FitRegime)regime;
        //the wording must not promise more than the arithmetic knows, so Unknown reads as itself
        Assert.Contains(expected, TierLine(fit), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_VISION_MARKER_appears_only_when_the_repo_actually_carries_a_projector()
    {
        //the marker comes from a projector file in the repo, assert the table's glyph since the words sit on the pane's encoder line
        Assert.Contains(Gatto.Terminal.GlyphSet.Unicode.Vision, Table(Row(vision: true)), StringComparison.Ordinal);
        Assert.DoesNotContain(Gatto.Terminal.GlyphSet.Unicode.Vision, Table(Row(vision: false)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_ROW_carries_the_ONE_QUANT_with_its_size_and_never_a_menu_of_them()
    {
        //the arithmetic chooses one quant per model, so the row never asks the user to pick between them
        var table = Table(Row(bytes: 8_589_934_592));

        //the size column always shows one decimal, so the digits line up down the page
        Assert.Contains("8.0 GB", table, StringComparison.Ordinal);

        //the row shows no gguf file name, so scope the check to the table, the pane shows the download url which holds that name
        Assert.DoesNotContain(".gguf", table, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_CONTEXT_IS_GROUPED_WITH_COMMAS_and_never_with_the_ambient_culture_separator()
    {
        //the grouped number always uses a comma, since a narrow no-break space reads as a typo. ctx is spelled out as context, it is jargon.
        var pane = Pane(Row(ctx: 32768));

        Assert.Contains("32,768", pane, StringComparison.Ordinal);
        //the phrase comes from the pane itself, this test is about the number and its separator
        Assert.Contains("context up to", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("32 768", pane, StringComparison.Ordinal);
        Assert.DoesNotContain(" ctx", pane, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fr-FR")]   //this culture groups with a narrow no-break space, which must not be the separator
    [InlineData("de-DE")]   //this culture groups with a period, so 32.768 reads as a decimal
    [InlineData("en-US")]
    public void THE_CONTEXT_SEPARATOR_IS_THE_SAME_ON_EVERY_MACHINE(string culture)
    {
        //change the ambient culture, since a machine-dependent render hides from any one culture. assert the separator itself.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            var pane = Pane(Row(ctx: 262144));

            Assert.Contains("262,144", pane, StringComparison.Ordinal);
            //the phrase comes from the pane itself, this test is about the number and its separator
        Assert.Contains("context up to", pane, StringComparison.Ordinal);
            Assert.DoesNotContain("262.144", pane, StringComparison.Ordinal);   //a period here reads as a decimal in de-DE
            //build the three space forms from their code points, U+202F and U+00A0 look like a plain space in the source
            foreach (var sep in new[] { (char)0x0020, (char)0x202F, (char)0x00A0 })
                Assert.DoesNotContain("262" + sep + "144", pane, StringComparison.Ordinal);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void A_row_carries_NO_DESCRIPTION_AND_NO_CAPABILITY_PROSE()
    {
        //author-supplied claims such as reasoning and tool use have no standard, so no surface may state them. check both surfaces.
        var both = Both(Row(badge: new Badge("org/model", new DateOnly(2026, 8, 10), "abc", "t")));

        foreach (var folklore in new[] { "reasoning", "tool use", "instruct-tuned", "uncensored", "state of the art" })
            Assert.DoesNotContain(folklore, both, StringComparison.OrdinalIgnoreCase);
    }
}
