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
        //8 GiB puts 14 quarter-GiB layers on the card, 4.5 GiB puts 24 layers of 0.14 GiB
        Assert.Equal(FitRegime.FitsSplit, LocalShelf.FitOf(File("Q8_0", 8 * GiB), Card(), 4096));
        Assert.Equal(14, LocalShelf.SplitOf(File("Q8_0", 8 * GiB), Card(), 4096));
        Assert.Equal(24, LocalShelf.SplitOf(File("Q4_K_M", 9 * GiB / 2), Card(), 4096));

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
}
