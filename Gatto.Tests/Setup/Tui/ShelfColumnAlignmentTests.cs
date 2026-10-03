using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//assert column offsets, since a whole-row literal pins the fixture's spelling and an unpadded size cell shifts every later column
public class ShelfColumnAlignmentTests
{
    private static ShelfRow R(string id, string file, long bytes, long paramCount, FitRegime fit) =>
        new(id, id.Split('/')[0], new HubQuant(file, bytes, null), fit, 32768, false, null, 10, false,
            Params: paramCount);

    //sizes of one, two and three digits, since a fixture whose sizes share one width can't fail an alignment check
    private static ShelfView Widths() => new(
        [
            R("bartowski/Qwen3-8B-GGUF", "qwen3-8b-Q4_K_M.gguf", 4_000_000_000, 8_000_000_000,
                FitRegime.FitsGpu),
            R("unsloth/Qwen3.6-70B-GGUF", "qwen3.6-70b-Q4_K_M.gguf", 40_000_000_000,
                70_000_000_000, FitRegime.FitsRamOnly),
            R("unsloth/Kimi-K2-GGUF", "kimi-k2-Q4_K_M.gguf", 600_000_000_000,
                1_000_000_000_000, FitRegime.DoesNotFit),
        ],
        null, MachineShape.Discrete, Total: 3);

    private static IReadOnlyList<string> DataRows() =>
        [.. Shelf.Table(Widths(), 0, focused: true, glyphs: GlyphSet.Unicode).Skip(1).Select(r => r.Text)];

    //the size is padded to the widest one shown, so the quant token starts in one column however wide a size grows
    [Fact]
    public void EVERY_ROW_STARTS_ITS_QUANT_IN_THE_SAME_COLUMN()
    {
        var offsets = DataRows().Select(t => t.IndexOf("Q4_K_M", StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(-1, offsets);
        Assert.True(offsets.Distinct().Count() == 1,
            "the quant token starts at " + string.Join(", ", offsets)
            + " on rows whose sizes are 3.7 GB, 37.3 GB and 558.8 GB");
    }

    //check the column after the size, since the quant offset can't see a size cell that outgrows its width
    [Fact]
    public void EVERY_ROW_STARTS_ITS_FIT_MARK_IN_THE_SAME_COLUMN()
    {
        var offsets = DataRows()
            .Select(t => t.IndexOfAny(['✓', '⚠', '✗']))
            .ToList();

        Assert.DoesNotContain(-1, offsets);
        Assert.True(offsets.Distinct().Count() == 1,
            "the fit mark starts at " + string.Join(", ", offsets));
    }

    //trillion-parameter models exist, so the cell rolls at 1T as another unit of the billions format
    [Theory]
    [InlineData(8_000_000_000L, "8B")]
    [InlineData(70_000_000_000L, "70B")]
    [InlineData(999_000_000_000L, "999B")]
    [InlineData(1_000_000_000_000L, "1T")]
    [InlineData(1_250_000_000_000L, "1.3T")]
    public void A_COUNT_AT_A_TRILLION_ROLLS_TO_T(long paramCount, string expected) =>
        Assert.Equal(expected, ShelfTable.ParamsCell(
            R("o/m", "m.gguf", 1_000_000_000, paramCount, FitRegime.FitsGpu)));
}
