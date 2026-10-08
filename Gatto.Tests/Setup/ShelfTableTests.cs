using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//nothing is ever lost. the collapse ladder buys width so no cell is cut, and past its floor the renderer wraps
public class ShelfTableTests
{
    //the default parameter count is a measured number, so the formatter meets the shape it will see
    private static ModelRow Row(
        string repoId, long bytes = 4_000_000_000, string file = "model-Q4_K_M.gguf",
        FitRegime fit = FitRegime.FitsGpu, bool vision = false, Badge? badge = null,
        string? modified = "2026-08-01", long downloads = 10, long? prms = 30_532_122_624) =>
        ShelfRows.Of(repoId, repoId.Split('/')[0], new HubQuant(file, bytes, "sha"), fit,
            NativeCtx: 262144, Vision: vision, Badge: badge, Downloads: downloads, Gated: false,
            LastModified: modified is null ? null : DateTimeOffset.Parse(modified + "T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            Params: prms);

    //reads a cell by its column name rather than by position. an inserted column shifts an ordinal index silently, and one at the end still passes while wrong
    private static string Cell(TableSpec spec, string header)
    {
        var i = spec.Headers.ToList().IndexOf(header);
        Assert.True(i >= 0, $"no column named \"{header}\": {string.Join(" | ", spec.Headers)}");
        return spec.Rows[0][i];
    }

    //no colour, since the sweep measures display cells and a painted line hides a width bug behind escapes
    private static Theme Plain() => new(new TermCaps(Rich: false, TrueColor: false));

    private static IReadOnlyList<string> Lines(
        IReadOnlyList<ModelRow> rows, int width) =>
        ShelfTable.Render(rows, Plain(), width, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode).Select(r => r.Text).ToList();

    //the oracle must measure the real width, since an infinite terminal never reaches the last column. the sweep steps by one, so the exact-fit width is in it
    [Fact]
    public void NO_LINE_EVER_EXCEEDS_THE_TERMINAL_WIDTH_at_any_width()
    {
        var rows = new[]
        {
            Row("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", 18_000_000_000, "Qwen3-Coder-30B-A3B-Q4_K_M.gguf",
                badge: new Badge("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", new DateOnly(2026, 8, 1), "b", "n")),
            Row("unsloth/gemma-4-26B-it-GGUF", 9_000_000_000, "gemma-4-26B-it-Q6_K.gguf", vision: true),
            Row("unsloth/tiny-GGUF", 900_000_000, "tiny-IQ2_XXS.gguf", fit: FitRegime.FitsRamOnly),
        };

        for (var width = 20; width <= 140; width++)
        {
            foreach (var line in Lines(rows, width))
                Assert.True(UnicodeWidth.Of(line) <= width,
                    $"width {width}: a line of {UnicodeWidth.Of(line)} cells, \"{line}\"");
        }
    }

    [Fact]
    public void NO_CELL_CONTENT_IS_EVER_LOST_however_narrow_it_gets()
    {
        //a model name is one the user may type, so nothing may be cut. the oracle matches characters in order, since a wrapped name is split by the next column's text
        var rows = new[] { Row("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF") };
        const string name = "Qwen3-Coder-30B-A3B-Instruct-GGUF";

        for (var width = 20; width <= 140; width++)
        {
            var flat = string.Concat(Lines(rows, width));
            var i = 0;
            foreach (var ch in flat)
                if (i < name.Length && ch == name[i]) i++;

            Assert.True(i == name.Length,
                $"width {width}: lost the name after {i} of {name.Length} characters");
        }
    }

    //each stage is pinned at a hand-computed width rather than by asking Natural. the merged stage fits exactly one width, asserted so an unreached rung is visible
    [Fact]
    public void THE_LADDER_DROPS_THE_LEAST_DECISIVE_THING_LEFT_at_widths_computed_by_hand()
    {
        var rows = new[] { Row("unsloth/abc-GGUF", 4_000_000_000, "abc-Q4_K_M.gguf") };

        //each width comes from hand-counted cells plus gaps rather than from asking StageFor. the tier label left the grid, so the model column is the longest name
        Assert.Equal(ShelfStage.Full, ShelfTable.StageFor(rows, 43, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode));
        Assert.Equal(ShelfStage.MergedSizeQuant, ShelfTable.StageFor(rows, 42, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode));

        //the last stage is returned even when it does not fit, since nothing more can be dropped and the renderer wraps.
        Assert.Equal(ShelfStage.NoContext, ShelfTable.StageFor(rows, 41, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode));
        Assert.Equal(ShelfStage.NoContext, ShelfTable.StageFor(rows, 10, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void THE_MARKS_COLUMN_SURVIVES_EVERY_WIDTH_including_the_floor()
    {
        //the marks column never drops, since a mark that disappears is worse than no mark. the check sweeps widths, since the floor is a threshold that moves
        var rows = new[]
        {
            Row("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", vision: true,
                badge: new Badge("o/m", new DateOnly(2026, 8, 1), "b", "n")) with { Arch = "gatto-test-unknown-arch" },
        };

        var floors = 0;
        for (var width = 20; width <= 140; width++)
        {
            if (ShelfTable.StageFor(rows, width, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode)
                == ShelfStage.NoContext) floors++;

            var flat = string.Concat(Lines(rows, width));
            foreach (var glyph in new[] { Gatto.Terminal.GlyphSet.Unicode.Vision, Gatto.Terminal.GlyphSet.Unicode.Ok, Gatto.Terminal.GlyphSet.Unicode.OtherBuild })
                Assert.True(flat.Contains(glyph, StringComparison.Ordinal),
                    $"width {width}: the marks column lost \"{glyph}\"");
        }

        //if the sweep never reached the last stage, it proved nothing about that rung
        Assert.True(floors > 0, "the sweep never reached the floor rung, widen it or lengthen the name");
    }

    [Fact]
    public void THE_FLOOR_DROPS_CONTEXT_and_keeps_the_marks()
    {
        //the floor gives up the context column, since the model's page still shows the number. the two halves are asserted as a pair so neither drifts
        var spec = ShelfTable.Spec(
            [Row("unsloth/abc-GGUF", vision: true)], ShelfStage.NoContext,
            Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.DoesNotContain("context", spec.Headers);
        Assert.Equal(Gatto.Terminal.GlyphSet.Unicode.Vision, spec.Rows[0][^1]);
    }

    [Fact]
    public void CONTEXT_IS_A_COLUMN_and_it_is_the_listings_own_number()
    {
        var spec = ShelfTable.Spec(
            [Row("unsloth/abc-GGUF")], ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        //the digits come from SearchRow.Ctx, the one home for that rule, so no two surfaces spell the number differently
        Assert.Equal(SearchRow.Ctx(262144), Cell(spec, "context"));
    }

    [Fact]
    public void A_SHELF_WHOSE_LISTING_CARRIED_NO_CONTEXT_HAS_NO_CONTEXT_COLUMN()
    {
        //a listing without a context leaves every cell empty, and the empty column costs its header plus a gap. a column that separates nothing reads as information
        var none = ShelfTable.Spec(
            [Row("unsloth/abc-GGUF") with { NativeCtx = null }], ShelfStage.Full,
            Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.DoesNotContain("context", none.Headers);
        //the cells drop with the header, or every value sits one column left of its name
        Assert.Equal(none.Headers.Count, none.Rows[0].Count);

        //one row with a context is enough to bring the column back for the whole shelf.
        var some = ShelfTable.Spec(
            [Row("unsloth/abc-GGUF") with { NativeCtx = null }, Row("unsloth/def-GGUF")],
            ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.Contains("context", some.Headers);
        Assert.Equal("", Cell(some, "context"));
    }

    [Fact]
    public void PARAMS_IS_THE_GGUFS_OWN_COUNT_and_survives_every_rung_of_the_ladder()
    {
        //the count comes from gguf.total, which the search response already returns. it describes the model itself, so the ladder never drops it
        var rows = new[] { Row("unsloth/abc-GGUF") };

        foreach (var stage in new[]
                 { ShelfStage.Full, ShelfStage.MergedSizeQuant, ShelfStage.NoContext })
        {
            var spec = ShelfTable.Spec(rows, stage, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);
            Assert.Equal("params", spec.Headers[1]);
            Assert.Equal("30.5B", spec.Rows[0][1]);
        }
    }

    [Fact]
    public void A_COUNT_WE_WERE_NEVER_GIVEN_IS_AN_EMPTY_CELL_and_never_read_off_the_name()
    {
        //a repo name is a marketing string, so a count nobody gave stays an empty cell
        var spec = ShelfTable.Spec(
            [Row("unsloth/Qwen3-Coder-30B-A3B-GGUF", prms: null)], ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.Equal("", spec.Rows[0][1]);
    }

    [Fact]
    public void PARAMS_NEVER_CLAIMS_AN_ACTIVE_EXPERT_COUNT()
    {
        //the field gives only the total, and no source but the repo name gives an active expert count, so that number is omitted
        var spec = ShelfTable.Spec(
            [Row("unsloth/gemma-4-26B-A4B-GGUF", prms: 27_009_346_304)], ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.Equal("27.0B", spec.Rows[0][1]);
        Assert.DoesNotContain("A4B", spec.Rows[0][1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_SUB_BILLION_MODEL_IS_SAID_IN_MILLIONS_rather_than_as_zero_point_something()
    {
        //a sub-billion count in millions reads as a real figure, where 0.4B reads as a rounding artefact
        var spec = ShelfTable.Spec([Row("unsloth/small-GGUF", prms: 352_000_000)], ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.Equal("352M", spec.Rows[0][1]);
    }

    [Fact]
    public void THE_LADDER_IS_MONOTONE_never_recovering_a_column_as_the_terminal_narrows()
    {
        //a ladder that adds a column back while the terminal narrows reads as flicker. the check runs over the whole sweep rather than one width
        var rows = new[] { Row("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF"), Row("unsloth/b-GGUF") };

        var previous = ShelfStage.Full;
        for (var width = 140; width >= 20; width--)
        {
            var stage = ShelfTable.StageFor(rows, width, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);
            Assert.True(stage >= previous, $"width {width}: {previous} → {stage} recovered a column");
            previous = stage;
        }
    }

    [Fact]
    public void THE_MODEL_COLUMN_IS_THE_LONGEST_NAME_not_the_longest_TIER_LABEL()
    {
        //the tier label left the grid, so the model column is the longest name. the oracle is the params column's start, right only if the label stopped competing
        var rows = new[]
        {
            Row("unsloth/a-GGUF", fit: FitRegime.FitsGpu),      //the curated name here is a-GGUF, six cells of the model column
            Row("unsloth/b-GGUF", fit: FitRegime.FitsRamOnly),  //this row's tier has the longest label, the worst case for the model column's width.
        };

        var rendered = ShelfTable.Render(rows, Plain(), 120,
            Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);
        var header = rendered[0].Text;

        //the expected start is computed from the fixture, so a renamed fixture cannot silently change the assertion
        var longestName = rows.Max(r => r.RowFile!.RepoId["unsloth/".Length..].Length);
        var expected = Math.Max("model".Length, longestName) + 2;

        Assert.Equal(expected, header.IndexOf("params", StringComparison.Ordinal));

        //no tier label may appear anywhere in the grid
        var tier = ShelfTable.TierLabel(FitRegime.FitsRamOnly, Gatto.Core.Hardware.MachineShape.Discrete, Gatto.Terminal.GlyphSet.Unicode);
        Assert.All(rendered, r => Assert.DoesNotContain(tier, r.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void THE_FIT_TIER_IS_A_SEPARATOR_ROW_and_not_a_column()
    {
        var rows = new[]
        {
            Row("unsloth/a-GGUF", fit: FitRegime.FitsGpu),
            Row("unsloth/b-GGUF", fit: FitRegime.FitsGpu),
            Row("unsloth/c-GGUF", fit: FitRegime.FitsRamOnly),
        };
        var spec = ShelfTable.Spec(rows, ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        //the grid holds models only, with the tier lines interleaved in afterwards
        Assert.Equal(3, spec.Rows.Count);
        Assert.DoesNotContain("fit", string.Join(" ", spec.Headers), StringComparison.OrdinalIgnoreCase);

        //no tier label may appear in any grid row
        foreach (var fit in new[] { FitRegime.FitsGpu, FitRegime.FitsRamOnly })
        {
            var label = ShelfTable.TierLabel(fit, Gatto.Core.Hardware.MachineShape.Discrete, Gatto.Terminal.GlyphSet.Unicode);
            Assert.All(spec.Rows, r => Assert.DoesNotContain(label, string.Join(" ", r), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AN_EMPTY_SHELF_RENDERS_ITS_HEADER_AND_NO_ROWS()
    {
        var spec = ShelfTable.Spec([], ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);
        Assert.Empty(spec.Rows);
        Assert.NotEmpty(ShelfTable.Render([], Plain(), 80, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode));
    }

    [Fact]
    public void MARKS_ARE_GLYPHS_ONLY_and_the_column_has_no_header()
    {
        //the marks column holds glyphs only, and the legend holds the words that explain them
        var rows = new[]
        {
            Row("unsloth/v-GGUF", vision: true,
                badge: new Badge("unsloth/v-GGUF", new DateOnly(2026, 8, 1), "b", "n")),
        };
        var spec = ShelfTable.Spec(rows, ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.Equal("", spec.Headers[^1]);
        var marks = spec.Rows[0][^1];
        Assert.Contains(Gatto.Terminal.GlyphSet.Unicode.Vision, marks, StringComparison.Ordinal);
        Assert.Contains(Gatto.Terminal.GlyphSet.Unicode.Ok, marks, StringComparison.Ordinal);
        foreach (var banned in new[] { "recommended", "best", "verified", "good", "fast", "slow" })
            Assert.DoesNotContain(banned, marks, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AN_UNCONVENTIONAL_FILENAME_LEAVES_THE_QUANT_BLANK_rather_than_inventing_one()
    {
        //an odd file name leaves the quant cell blank, the rule omits rather than guesses
        var spec = ShelfTable.Spec(
            [Row("unsloth/a-GGUF", file: "weights.gguf")], ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode);

        Assert.Equal("", Cell(spec, "quant"));
        Assert.Equal("Q4_K_M", Cell(
            ShelfTable.Spec([Row("unsloth/a-GGUF")], ShelfStage.Full, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode),
            "quant"));
    }

    [Fact]
    public void ATTACKER_TEXT_IN_A_REPO_NAME_CANNOT_DRIVE_THE_TERMINAL()
    {
        //don't sanitize a repo name here, TableLayout.CellSpans already did it before measuring (a second pass measures one string and paints another)
        var rows = new[] { Row("unsloth/evil[31m-GGUF") };

        foreach (var line in Lines(rows, 80))
            Assert.DoesNotContain('', line);
    }

    [Fact]
    public void TEN_ROWS_LAY_OUT_WITHOUT_THE_TABLE_DEGRADING_to_records()
    {
        //ten rows must stay a table, a boxed renderer switches to key/value records past a row limit and makes a pick list's height unpredictable
        var rows = Enumerable.Range(0, 10).Select(i => Row($"unsloth/model-{i}-GGUF")).ToArray();

        var lines = Lines(rows, 80);

        //the grid holds a header and ten models only, the tier label is interleaved later by ShelfBinding
        Assert.Equal(11, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("model:", StringComparison.Ordinal));
    }
}
