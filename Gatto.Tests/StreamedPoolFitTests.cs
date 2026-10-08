using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Tests.Setup;

namespace Gatto.Tests;

//a streamed table is memory too: the measured points on the 8060S, and what the rule does beside a discrete card and with no card
public class StreamedPoolFitTests
{
    private const long B = 1_000_000_000L;
    private static readonly HardwareClass Apu = ShelfMachines.Apu8060S;

    //the Hub path prices a row from its bare header, as the shelf does
    private static FitRegime Hub(long bytes, long streamed, HardwareClass hw) =>
        HubSearch.FitOf(bytes, 262144, hw, 4096, "qwen4exp", streamed);

    [Fact]
    public void THE_8060S_BUDGETS_ARE_THE_MEASURED_ONES()
    {
        Assert.Equal(81_552_232_940UL, Apu.GpuBudgetBytes);
        Assert.Equal(68_262_201_344UL, Apu.RamBudgetBytes);
        Assert.Equal(119_458_852_352UL, HardwareClassifier.StreamPoolBytes(Apu.Snapshot));
    }

    //bartowski's Q3_K_XL fits the card less its table, but the two together pass the pool
    [Fact]
    public void THE_119_GB_FILE_IS_TOO_BIG() =>
        Assert.Equal(FitRegime.DoesNotFit, Hub(119_347_361_600, 54_400_261_120, Apu));

    [Fact]
    public void THE_93_GB_IQ4_XS_FITS() =>
        Assert.Equal(FitRegime.FitsGpu, Hub(93_682_584_224, 28_800_138_240, Apu));

    //the file that runs, priced from its own header at the profile's context with q8_0 cache, as the local shelf prices it
    [Fact]
    public void THE_102_GB_FILE_THAT_RUNS_FITS()
    {
        var header = new GgufHeader(GgufOutcome.Complete, null, "qwen4exp", null, 262144, null,
            48, 24, 2, 2560, 256, 256, null, FullAttentionInterval: 4);
        var e = FitArithmetic.Estimate(header, 102_474_187_584, 262144, KvCacheKind.Q8_0, 28_800_138_240);

        Assert.Equal(3_422_552_064, e.KvCacheBytes);
        Assert.Equal(FitRegime.FitsGpu, FitArithmetic.Judge(e, Apu));
    }

    //beside a discrete card the table sits in system memory, so a table larger than the RAM budget costs the card
    [Fact]
    public void ON_THE_VEGA_A_TABLE_PAYS_THE_RAM_BUDGET()
    {
        var vega = ShelfMachines.Vega;
        Assert.Equal(FitRegime.FitsGpu, Hub(5 * B, 1_800_000_000, vega));
        Assert.Equal(FitRegime.DoesNotFit, Hub(20 * B, 18 * B, vega));
    }

    //with no card the whole set lives in system memory, the table with it
    [Fact]
    public void WITH_NO_CARD_THE_TABLE_PAYS_THE_RAM_BUDGET()
    {
        var none = ShelfMachines.NoCard;
        Assert.Equal(FitRegime.FitsRamOnly, Hub(5 * B, 1_800_000_000, none));
        Assert.Equal(FitRegime.DoesNotFit, Hub(20 * B, 18 * B, none));
    }

    //a file with no table reads as it always did, on every machine
    [Theory]
    [InlineData(10L * B)]
    [InlineData(60L * B)]
    [InlineData(200L * B)]
    public void NO_TABLE_NO_CHANGE(long bytes)
    {
        foreach (var hw in new[] { Apu, ShelfMachines.Vega, ShelfMachines.NoCard })
        {
            var needed = bytes * 125 / 100;
            var expected = needed <= (long)hw.GpuBudgetBytes ? FitRegime.FitsGpu
                : needed <= (long)hw.RamBudgetBytes ? FitRegime.FitsRamOnly : FitRegime.DoesNotFit;
            Assert.Equal(expected, HubSearch.FitOf(bytes, 262144, hw, 4096, "qwen3next", null));
        }
    }

    //beside a discrete card equal ends do not prove the middle, so a file the card cannot hold whole is read
    [Fact]
    public void BESIDE_A_DISCRETE_CARD_THE_CUT_READS_WHAT_THE_CARD_CANNOT_HOLD_WHOLE()
    {
        var listing = new HubListing("o/m-GGUF", "gemma3n", 32768, false, 0);
        var could = HubSearch.TableCouldMove(listing, ShelfMachines.Vega, 4096);
        Assert.True(could(new HubQuant("m-Q8_0.gguf", 40 * B, "aa")));
        Assert.False(could(new HubQuant("m-Q4_K_M.gguf", 3 * B, "bb")));
    }
}
