using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Shelves;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//a split shows on the shelf as its own mark, words, group and place in the pick, between the card and memory
public class SplitFitShelfTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void THE_SPLIT_MARK_IS_GPU_RAM_IN_THE_WARN_INK_IN_THE_RAM_MARKS_WIDTH()
    {
        var split = FitMarks.Of(FitRegime.FitsSplit, MachineShape.Discrete, GlyphSet.Unicode);
        var ram = FitMarks.Of(FitRegime.FitsRamOnly, MachineShape.Discrete, GlyphSet.Unicode);

        Assert.Equal(new FitMark(GlyphSet.Unicode.Warn, "GPU+RAM", RunInk.Warn), split);
        Assert.Equal(9, UnicodeWidth.Of(split.Text));
        Assert.Equal(ram.Glyph, split.Glyph);
    }

    //named only on a shelf that shows a split, so the search's legend never explains a mark it cannot give
    [Fact]
    public void THE_LEGEND_NAMES_THE_SPLIT_BETWEEN_THE_CARD_AND_MEMORY_WHEN_A_ROW_CARRIES_IT()
    {
        var g = GlyphSet.Unicode;

        Assert.Equal($"{g.Ok} GPU {g.Dot} {g.Warn} GPU+RAM {g.Dot} {g.Warn} RAM", FitMarks.LegendFor(MachineShape.Discrete, g, split: true).Text);
        Assert.Equal($"{g.Ok} GPU {g.Dot} {g.Warn} RAM", FitMarks.LegendFor(MachineShape.Discrete, g).Text);
    }

    [Fact]
    public void THE_ROW_AND_THE_GROUP_SAY_ON_THE_CARD_AND_IN_MEMORY()
    {
        Assert.Equal("on the card and in memory", SearchRow.FitWords(FitRegime.FitsSplit, MachineShape.Discrete, GlyphSet.Unicode));
        Assert.Equal("on the card and in memory", Shelf.GroupHeading(FitRegime.FitsSplit));
    }

    //every verdict the arithmetic gives has a mark and its words, the four of them, and only the unpriced file has neither
    [Fact]
    public void FOUR_VERDICTS_EACH_HAVE_A_MARK_AND_WORDS()
    {
        var verdicts = Enum.GetValues<FitRegime>().Where(f => f != FitRegime.Unknown).ToList();

        Assert.Equal(4, verdicts.Count);
        foreach (var fit in verdicts)
        {
            Assert.NotEqual("", FitMarks.Of(fit, MachineShape.Discrete, GlyphSet.Unicode).Word);
            Assert.NotEqual("size not known", SearchRow.FitWords(fit, MachineShape.Discrete, GlyphSet.Unicode));
        }
    }

    private static ModelRow Row(string name, FitRegime fit) =>
        ShelfRows.Of($"org/{name}", "org", new HubQuant($"{name}-Q4_K_M.gguf", 5 * GiB, null), fit, Model: name);

    [Fact]
    public void A_CARD_ROW_AND_A_SPLIT_ROW_ARE_TWO_GROUPS()
    {
        var v = new ShelfView([Row("a", FitRegime.FitsGpu), Row("b", FitRegime.FitsSplit)], MachineShape.Discrete);

        Assert.True(Shelf.Grouped(v));
        Assert.False(Shelf.Grouped(v with { Rows = [Row("a", FitRegime.FitsSplit), Row("b", FitRegime.FitsSplit)] }));
    }

    //32 blocks at 4096 tokens is a half GiB cache, so a 4 GiB card has 3.5 GiB for layers
    private static GgufHeader Header(long? blocks = 32) => new(
        GgufOutcome.Complete, null, "qwen3", "t", 32768, null,
        BlockCount: blocks, HeadCount: 32, HeadCountKv: 8,
        EmbeddingLength: 4096, KeyLength: 128, ValueLength: 128, ChatTemplate: null);

    private static HardwareClass Card() => new(
        MemoryTopology.Discrete, ShareKind.None, 4UL << 30, 24UL << 30,
        new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 4UL << 30), 0, BudgetBound.None);

    private static FoundModel File(string quant, long bytes, long? blocks = 32) =>
        new($@"C:\m\x-{quant}.gguf", bytes, Header(blocks));

    private static string? Pick(params FoundModel[] files) =>
        LocalShelf.RowFileOf(new LocalShelf.LocalGroup("x", files), Card(), served: _ => false, activePath: null, listed: _ => false)
            is { } f ? System.IO.Path.GetFileName(f.Path) : null;

    [Fact]
    public void THE_SPLIT_WITH_MORE_LAYERS_ON_THE_CARD_IS_PICKED()
    {
        //each at its own pick: 8 GiB holds 14 quarter-GiB layers at 4096, and 4.5 GiB holds 20 of 0.14 GiB at the largest rung holding half
        Assert.Equal(FitRegime.FitsSplit, LocalShelf.FitOf(File("Q8_0", 8 * GiB), Card(), 4096));
        Assert.Equal(14, LocalShelf.SplitOf(File("Q8_0", 8 * GiB), Card(), 4096));
        Assert.Equal(20, LocalShelf.SplitOf(File("Q4_K_M", 9 * GiB / 2), Card(), 4096));

        Assert.Equal("x-Q4_K_M.gguf", Pick(File("Q8_0", 8 * GiB), File("Q4_K_M", 9 * GiB / 2)));
    }

    [Fact]
    public void A_FILE_THAT_FITS_THE_CARD_BEATS_A_SPLIT()
    {
        Assert.Equal("x-Q4_K_S.gguf", Pick(File("Q8_0", 8 * GiB), File("Q4_K_S", 3 * GiB)));
    }

    [Fact]
    public void A_SPLIT_BEATS_A_FILE_THAT_FITS_MEMORY_ONLY()
    {
        //with no block count the file prices no split, so it is memory only
        Assert.Equal(FitRegime.FitsRamOnly, LocalShelf.FitOf(File("Q4_K_M", 9 * GiB / 2, blocks: null), Card(), 4096));

        Assert.Equal("x-Q8_0.gguf", Pick(File("Q8_0", 8 * GiB), File("Q4_K_M", 9 * GiB / 2, blocks: null)));
    }

    private static IReadOnlyList<string> ShelfCells(FoundModel f, HardwareClass? hw)
    {
        var row = LocalShelf.Row(new LocalShelf.LocalGroup("x", [f]), f, hw);
        return ShelfTable.Spec([row], ShelfStage.Full, MachineShape.Discrete, GlyphSet.Unicode).Rows[^1];
    }

    //the column states what setup writes, and the regime is the one the pick was judged at
    [Fact]
    public void A_ROW_IS_PRICED_AT_ITS_PICK_AND_ITS_COLUMN_SHOWS_IT()
    {
        var f = File("Q4_K_S", 2 * GiB);
        var pick = FitArithmetic.PickContext(f.Header!, f.FileBytes, f.StreamedBytes, Card());

        Assert.NotNull(pick);
        Assert.True(pick.Context < 32768, $"the pick is {pick.Context}");
        Assert.Equal(pick.Regime, LocalShelf.FitOf(f, Card(), 4096));
        Assert.Equal(pick.Context, LocalShelf.Row(new LocalShelf.LocalGroup("x", [f]), f, Card()).PickedCtx);
        Assert.Contains(SearchRow.Ctx(pick.Context), ShelfCells(f, Card()));
        Assert.DoesNotContain(SearchRow.Ctx(32768), ShelfCells(f, Card()));
    }

    //no hardware to pick against leaves the file's own window in the column and no regime
    [Fact]
    public void A_ROW_WITH_NO_HARDWARE_SHOWS_THE_CEILING_AND_STAYS_UNKNOWN()
    {
        var f = File("Q4_K_S", 2 * GiB);

        Assert.Contains(SearchRow.Ctx(32768), ShelfCells(f, null));
        Assert.Equal(FitRegime.Unknown, LocalShelf.FitOf(f, null, 4096));
    }

    //a file nothing holds has no pick, so the row keeps the 4096 price and the file's own window
    [Fact]
    public void A_ROW_NOTHING_HOLDS_SHOWS_THE_CEILING_AND_THE_4096_PRICE()
    {
        var f = File("Q8_0", 400 * GiB);

        Assert.Null(FitArithmetic.PickContext(f.Header!, f.FileBytes, f.StreamedBytes, Card()));
        Assert.Equal(FitRegime.DoesNotFit, LocalShelf.FitOf(f, Card(), 4096));
        Assert.Contains(SearchRow.Ctx(32768), ShelfCells(f, Card()));
    }

    //the estimate throws over the cap and the shelf has no catch, so a local file claiming an impossible window must still draw a row
    [Fact]
    public void A_FILE_CLAIMING_AN_IMPOSSIBLE_WINDOW_STILL_DRAWS_ITS_ROW()
    {
        var f = File("Q8_0", GiB) with { Header = Header() with { ContextLength = FitArithmetic.MaxContext + 1L } };

        Assert.Equal(FitArithmetic.Judge(FitArithmetic.Estimate(f.Header!, f.FileBytes, 4096, KvCacheKind.Q8_0), Card()), LocalShelf.FitOf(f, Card(), 4096));
        Assert.Null(LocalShelf.Row(new LocalShelf.LocalGroup("x", [f]), f, Card()).PickedCtx);
    }

    private static string PaneText(FoundModel f, HardwareClass? hw)
    {
        var group = new LocalShelf.LocalGroup("x", [f]);
        var row = LocalShelf.Row(group, f, hw);
        var facts = LocalShelf.FactsFor(group, f, ruled: true, hw);
        var open = Pane.Rows(row, facts, MachineShape.Discrete, 60, glyphs: GlyphSet.Unicode).Select(r => r.Text);
        var fold = Pane.Fold(row, facts, MachineShape.Discrete, 60, focused: false, glyphs: GlyphSet.Unicode).Select(r => r.Text);
        return string.Join("\n", open.Concat(fold));
    }

    //the pane states what setup writes beside the file's own window, on both its faces
    [Fact]
    public void THE_PANE_STATES_THE_PICK_OF_THE_CEILING_ON_A_LOCAL_ROW()
    {
        var f = File("Q4_K_S", 2 * GiB);
        var pick = FitArithmetic.PickContext(f.Header!, f.FileBytes, f.StreamedBytes, Card());

        var text = PaneText(f, Card());

        Assert.Equal(2, text.Split($"context {SearchRow.Ctx(pick!.Context)} of {SearchRow.Ctx(32768)}").Length - 1);
        Assert.DoesNotContain("context up to", text, StringComparison.Ordinal);
    }

    //a pick at the ceiling has nothing to add to the ceiling
    [Fact]
    public void THE_PANE_KEEPS_THE_CEILING_ALONE_ON_A_ROW_PICKED_AT_IT()
    {
        var f = File("Q4_K_S", GiB / 4);
        Assert.Equal(32768, FitArithmetic.PickContext(f.Header!, f.FileBytes, f.StreamedBytes, Roomy())!.Context);

        var text = PaneText(f, Roomy());

        Assert.Equal(2, text.Split($"context up to {SearchRow.Ctx(32768)}").Length - 1);
        Assert.DoesNotContain(" of ", text, StringComparison.Ordinal);
    }

    private static HardwareClass Roomy() => new(
        MemoryTopology.Discrete, ShareKind.None, 16UL << 30, 24UL << 30,
        new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 16UL << 30), 0, BudgetBound.None);

    [Fact]
    public void THE_PANE_KEEPS_THE_CEILING_ALONE_ON_A_ROW_WITH_NO_PICK()
    {
        var text = PaneText(File("Q4_K_S", 2 * GiB), null);

        Assert.Equal(2, text.Split($"context up to {SearchRow.Ctx(32768)}").Length - 1);
    }

    //the row on a card with no matrix cores prices the f16 cache the scaffold will write, so its pick is that cache's
    [Fact]
    public void A_ROW_ON_A_CARD_WITH_NO_MATRIX_CORES_IS_PRICED_AT_F16()
    {
        HardwareClass At(CoopmatKind coopmat) => new(
            MemoryTopology.Discrete, ShareKind.None, 4UL << 30, 24UL << 30,
            new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 4UL << 30, "1002", GraphicsCoopmat: coopmat), 0, BudgetBound.None);
        var f = File("Q4_K_S", 5 * GiB / 2);
        var group = new LocalShelf.LocalGroup("x", [f]);

        var q8 = LocalShelf.Row(group, f, At(CoopmatKind.Khr)).PickedCtx;
        var f16 = LocalShelf.Row(group, f, At(CoopmatKind.None)).PickedCtx;

        Assert.True(f16 < q8, $"f16 picked {f16}, q8_0 picked {q8}");
    }
}
