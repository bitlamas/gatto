using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//a wrapped row arrives as extra Continuation rows, and the binding must cut them back without dropping a line
public class ShelfBindingTests
{
    private static readonly Theme T = new(TermCaps.Plain);

    private static ModelRow Row(string id, long bytes, FitRegime fit, long? prm) =>
        ShelfRows.Of(id, id.Split('/')[0], new HubQuant(id.Split('/')[1] + "-Q4_K_M.gguf", bytes, null),
            fit, 32768, false, null, 1000, false, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            prm);

    //two tiers, so the sweep reaches both branches that cut the flat list, the header row and a mid-list tier line
    private static ShelfView View() => new(
        [
            Row("bartowski/Qwen3-Coder-GGUF", 4_000_000_000, FitRegime.FitsGpu, 30_500_000_000),
            Row("unsloth/Llama-4-Scout-GGUF", 9_000_000_000, FitRegime.FitsGpu, 17_000_000_000),
            Row("ggml-org/Gemma-3-27B-GGUF", 20_000_000_000, FitRegime.FitsRamOnly, 27_000_000_000),
        ],
        Shape: MachineShape.Discrete);

    private static IReadOnlyList<SelectOption> Options(ShelfView v) =>
    [
        .. v.Rows.Select(r => new SelectOption(r.RowFile!.RepoId)),
        new SelectOption("I already have a model, let me point at the folder"),
        new SelectOption("Type a model's name from Hugging Face"),
    ];

    [Fact]
    public void A_WIDTH_SWEEP_LOSES_NO_RENDERED_LINE_header_row_included()
    {
        //the oracle is ShelfTable.Render's own output, so the grouping is not re-derived here. the sweep starts at 20 columns, where the header row wraps
        var view = View();
        var options = Options(view);
        var (labelsAt, headingsAt, _) = ShelfBinding.For(view, options, T, glyphs: GlyphSet.Unicode);
        //the shelf has no row numbers, so this must pass numbered: false exactly as ShelfBinding does. a different value measures a column the binding never laid out
        var chrome = SelectPrompt.LabelColumn(anyMarked: false, multiSelect: false, numbered: false);

        var wrapped = 0;

        for (var w = 20; w <= 110; w++)
        {
            var content = Math.Max(1, w - chrome);
            var expected = ShelfTable.Render(view.Rows, T, content, view.Shape, glyphs: GlyphSet.Unicode);
            wrapped += expected.Count(r => r.Continuation);

            var produced = headingsAt(w).Select(h => h.Text)
                .Concat(labelsAt(w).SelectMany(l => l.Split('\n')))
                .ToList();

            foreach (var line in expected)
                Assert.True(produced.Contains(line.Text, StringComparer.Ordinal),
                    $"width {w}: the binding dropped a rendered line.\n"
                    + $"  missing: \"{line.Text}\" (continuation: {line.Continuation})\n"
                    + $"  produced:\n    {string.Join("\n    ", produced)}");
        }

        //if no width produces a wrapped row, the sweep proves nothing about the branch that drops a line.
        Assert.True(wrapped > 0,
            "the sweep never produced a single continuation row, it cannot have exercised the "
            + "wrapped-row branches, so widen the range or lengthen the fixture");
    }

    [Fact]
    public void EVERY_LABEL_FITS_THE_COLUMN_so_the_widget_never_has_to_TRUNCATE_one()
    {
        //the binding returns the whole string, so the oracle is the column budget and the loss happens later. a wrapped escape row stays one selectable row
        var view = View();
        var options = Options(view);
        var chrome = SelectPrompt.LabelColumn(anyMarked: false, multiSelect: false, numbered: false);
        var offenders = 0;

        for (var w = 30; w <= 110; w++)
        {
            var budget = Math.Max(1, w - chrome);
            foreach (var label in ShelfBinding.For(view, options, T, glyphs: GlyphSet.Unicode).LabelsAt(w))
                foreach (var line in label.Split('\n'))
                {
                    if (UnicodeWidth.Of(line) > budget) offenders++;
                    Assert.True(UnicodeWidth.Of(line) <= budget,
                        $"width {w}: a label line of {UnicodeWidth.Of(line)} cells in a {budget}-cell "
                        + $"column, the widget will truncate it: \"{line}\"");
                }
        }

        Assert.Equal(0, offenders);
    }


    private static ShelfView Populated(bool lift = false, int hidden = 0, int skipped = 0) =>
        View() with { Lift = lift, HiddenByFit = hidden, HiddenByKind = skipped };

    private static IReadOnlyList<string> Texts(ShelfView v, int w = 100) =>
        [.. ShelfBinding.For(v, Options(v), T, glyphs: GlyphSet.Unicode).HeadingsAt(w).Select(h => h.Text)];

    [Fact]
    public void THE_COUNT_LINE_SAYS_WHAT_THE_FILTER_HID_and_never_what_the_machine_cannot_do()
    {
        //the user cares about what they can run, so the line never leads with rows that do not fit. the hidden count is gatto's estimate, and pressing a overrules it
        var line = ShelfBinding.CountLine(Populated(hidden: 15), Gatto.Terminal.GlyphSet.Unicode);

        Assert.Contains("15 more hidden", line, StringComparison.Ordinal);
        Assert.Contains("press a", line, StringComparison.Ordinal);
        //the line makes no claim about the machine and prints no "of N" total
        Assert.DoesNotContain("machine", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" of ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_COUNT_LINE_SAYS_NOTHING_ABOUT_HIDING_when_nothing_is_hidden()
    {
        //the hidden count must be a fact about this search, or the line claims a filter that hid nothing
        var line = ShelfBinding.CountLine(Populated(hidden: 0), Gatto.Terminal.GlyphSet.Unicode);

        Assert.DoesNotContain("hidden", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("press a", line, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_COUNT_LINE_SAYS_WHAT_WAS_SKIPPED_FOR_ITS_KIND_and_offers_no_key_to_get_it_back()
    {
        //gatto drops repos by what they are, such as diffusion or embedding models, so the shelf has to say so
        var line = ShelfBinding.CountLine(Populated(skipped: 12), Gatto.Terminal.GlyphSet.Unicode);

        Assert.Contains("12 skipped", line, StringComparison.Ordinal);
        //the fit filter offers a, since its arithmetic is an estimate the user may overrule. nothing in the skipped count can be overruled, so the line offers no key
        Assert.DoesNotContain("press", line, StringComparison.OrdinalIgnoreCase);
        //the skipped segment makes no claim about the machine and prints no "of N" total
        Assert.DoesNotContain("machine", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" of ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_SKIPPED_COUNT_IS_A_FACT_ABOUT_THIS_SEARCH_and_reads_as_one_when_there_is_one()
    {
        //the skipped count must be a fact about this search, or the line claims something was kept back when nothing was
        Assert.DoesNotContain("skipped", ShelfBinding.CountLine(Populated(skipped: 0), Gatto.Terminal.GlyphSet.Unicode),
            StringComparison.OrdinalIgnoreCase);
        //the fragment is composed into a rendered row, so both number forms are asserted. one skipped repo reads a model, two read models
        Assert.Contains("1 skipped, not a model you can talk to",
            ShelfBinding.CountLine(Populated(skipped: 1), Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.Contains("2 skipped, not models you can talk to",
            ShelfBinding.CountLine(Populated(skipped: 2), Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_TWO_HIDDEN_COUNTS_ARE_SAID_SEPARATELY_because_they_are_different_facts()
    {
        //the fit count reports the machine's memory and the kind count reports what a repo is, so one number would blame the user's RAM
        var line = ShelfBinding.CountLine(Populated(hidden: 15, skipped: 12), Gatto.Terminal.GlyphSet.Unicode);

        Assert.Contains("15 more hidden, press a to include them", line, StringComparison.Ordinal);
        Assert.Contains("12 skipped", line, StringComparison.Ordinal);
        //the segment order decides which part a narrow width keeps, so the count with a key comes first
        Assert.True(line.IndexOf("hidden", StringComparison.Ordinal)
                  < line.IndexOf("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void THE_LIFTS_STATE_IS_READABLE_without_remembering_the_keypress()
    {
        //a control's state must be readable, or the key is a promise nobody can check. a lifted shelf hides nothing, so the line must say something else
        var line = ShelfBinding.CountLine(Populated(lift: true, hidden: 0), Gatto.Terminal.GlyphSet.Unicode);

        Assert.Contains("including", line, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_SINGLE_TIER_PUTS_ITS_FACT_ON_THE_COUNT_LINE()
    {
        //one tier separates nothing, so the fit label moves off the heading and onto the count line
        var one = View() with { Rows = [View().Rows[0]] };
        var line = ShelfBinding.CountLine(one, Gatto.Terminal.GlyphSet.Unicode);

        Assert.Contains(SearchRow.FitWords(one.Rows[0].Fit, one.Shape, Gatto.Terminal.GlyphSet.Unicode), line, StringComparison.Ordinal);
        //with two tiers the headings state the fit, so the count line must not repeat it three rows away
        Assert.DoesNotContain(SearchRow.FitWords(FitRegime.FitsGpu, View().Shape, Gatto.Terminal.GlyphSet.Unicode),
            ShelfBinding.CountLine(View(), Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_SHELF_SAYS_PLAINLY_WHEN_NOTHING_HERE_HAS_BEEN_MEASURED()
    {
        //an empty state must be said, or the user cannot tell an unverified shelf from one gatto never checked
        Assert.Contains(Texts(Populated()), s => s.Contains(ShelfBinding.UnmeasuredShelf, StringComparison.Ordinal));
    }

    [Fact]
    public void THE_HEADER_STRIP_ADVERTISES_EXACTLY_THE_KEYS_THAT_WORK()
    {
        //the strip and the key set come from one function, so a key that is not offered cannot be advertised. two lists agreeing by hand would let one slip in
        var normal = Populated();
        foreach (var k in ShelfControls.Keys(normal))
            Assert.Contains(k.ToString(), ShelfControls.Strip(normal, Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(80)]
    [InlineData(60)]
    [InlineData(40)]
    public void THE_CHROME_NEVER_LOSES_A_WORD_AT_ANY_WIDTH(int width)
    {
        //narrow widths wrap the chrome and never drop or truncate a word, so several widths are swept. a key that is not drawn is not a control the user can see
        var shelf = Populated(hidden: 15);
        var joined = string.Concat(Texts(shelf, width).Select(s => s.Replace(" ", "")));

        //the words come from ShelfControls.For, the same list that builds the strip. a hand-written copy agrees with the code only until the code moves
        var words = ShelfControls.For(shelf).Select(c => c.Word.Replace(" ", ""));
        foreach (var needle in words.Append("15morehidden"))
            Assert.True(joined.Contains(needle, StringComparison.Ordinal),
                $"width {width}: the chrome lost \"{needle}\"");
    }

    private static IReadOnlyList<SelectHeading> HeadingsFor(ShelfView v, int w = 100)
        => ShelfBinding.For(v, Options(v), T, glyphs: GlyphSet.Unicode).HeadingsAt(w);

    [Fact]
    public void THE_TIER_LINE_OPENS_WITH_THE_RULE_and_says_FitWords()
    {
        //one arithmetic has one vocabulary: the tier words come from SearchRow.FitWords through ShelfTable.TierLabel. the rule prefix reuses the Theme.Rule glyph
        var texts = HeadingsFor(View()).Select(h => h.Text).ToList();
        var gpu = ShelfTable.TierLabel(FitRegime.FitsGpu, MachineShape.Discrete, Gatto.Terminal.GlyphSet.Unicode);

        var line = Assert.Single(texts, t => t.Contains(gpu, StringComparison.Ordinal));
        Assert.Contains(ShelfTable.TierRuleOf(Gatto.Terminal.GlyphSet.Unicode), line, StringComparison.Ordinal);
        Assert.True(line.IndexOf(ShelfTable.TierRuleOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal)
                    < line.IndexOf(gpu, StringComparison.Ordinal),
            "the rule opens the line; it is a prefix, not a suffix");
    }

    [Fact]
    public void A_SINGLE_TIER_STILL_GETS_ITS_LABEL()
    {
        //the label says what the fit of these rows is, so a reader needs it with or without a second group below
        var single = View() with { Rows = [View().Rows[0]] };
        var gpu = ShelfTable.TierLabel(FitRegime.FitsGpu, MachineShape.Discrete, Gatto.Terminal.GlyphSet.Unicode);

        Assert.Contains(HeadingsFor(single), h => h.Text.Contains(gpu, StringComparison.Ordinal));
    }

    [Fact]
    public void AIR_SITS_ABOVE_A_TIER_LINE_and_never_below_it()
    {
        //blank space above a tier line binds it to the group below, so none sits under one. the check groups headings by option, since the flat list is not screen order
        var byOption = HeadingsFor(View())
            .GroupBy(h => h.BeforeOption)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(h => h.Text).ToList())
            .ToList();

        var tierGroups = byOption.Where(g => g.Any(t => t.Contains(ShelfTable.TierRuleOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal)))
            .ToList();
        Assert.Equal(2, tierGroups.Count);                              //the fixture must really hold two tiers, or the assertions over the groups prove nothing.

        //the tier line is the last row before its group's first model row, so no blank can sit under it
        foreach (var g in tierGroups)
            Assert.Contains(ShelfTable.TierRuleOf(Gatto.Terminal.GlyphSet.Unicode), g[^1], StringComparison.Ordinal);

        //every tier line takes a blank above it, the first one included.
        foreach (var g in tierGroups) Assert.Equal("", g[^2]);

        //a tier line that opens the block gets no blank, since every stage emits a header row above it
    }

    [Fact]
    public void EVERY_MODEL_KEEPS_ITS_OWN_LABEL_and_the_escape_rows_keep_theirs()
    {
        //one label per option, models first and the escape rows after, keeping the flow's words. an off by one puts a table row on an escape option
        var view = View();
        var options = Options(view);
        var (labelsAt, _, _) = ShelfBinding.For(view, options, T, glyphs: GlyphSet.Unicode);

        var labels = labelsAt(100);

        Assert.Equal(options.Count, labels.Count);
        for (var i = 0; i < view.Rows.Count; i++)
            Assert.Contains(view.Rows[i].Model, labels[i], StringComparison.Ordinal);
        for (var i = view.Rows.Count; i < options.Count; i++)
            Assert.Equal(options[i].Label, labels[i]);
    }
}
