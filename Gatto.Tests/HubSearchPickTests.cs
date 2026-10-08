using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests;

//the pick inside one regime: the floor first, then the band, its own range, above it smallest first, below it largest first
public class HubSearchPickTests
{
    private const long GiB = 1L << 30;

    private static readonly HardwareClass Big = HardwareClassifier.Classify(
        new HardwareSnapshot(64UL * (ulong)GiB, 64UL * (ulong)GiB, GpuKind.Discrete, 24UL * (ulong)GiB, "0x10de"));

    //the 8 GiB card measured for the shelf wave
    private static readonly HardwareClass Small8 = HardwareClassifier.Classify(
        new HardwareSnapshot(32UL * (ulong)GiB, 32UL * (ulong)GiB, GpuKind.Discrete, 8UL * (ulong)GiB, "0x1002"));

    private static HubQuant Q(string name, long gb) => new(name, gb * GiB, null);

    //both on the card, so the order decides and not the regime
    private static void SameRegime(HardwareClass hw, params HubQuant[] q) =>
        Assert.Single(q.Select(x => HubSearch.FitOf(x.Bytes, null, hw, 4096)).Distinct());

    [Fact]
    public void PAST_THE_BAND_THE_SMALLEST_WINS()
    {
        var q = new[] { Q("m-Q8_0.gguf", 9), Q("m-BF16.gguf", 17) };
        SameRegime(Big, q);
        Assert.Equal("m-Q8_0.gguf", HubSearch.Pick(q, Big, null, 4096, 9_000_000_000, false)!.Value.Quant.FileName);
    }

    [Fact]
    public void BELOW_THE_BAND_THE_LARGEST_WINS()
    {
        var q = new[] { Q("m-IQ3_XXS.gguf", 2), Q("m-Q3_K_L.gguf", 3) };
        var picked = HubSearch.Pick(q, Small8, null, 4096, 27_000_000_000, false)!.Value;
        Assert.Equal(FitRegime.FitsGpu, picked.Fit);
        Assert.Equal("m-Q3_K_L.gguf", picked.Quant.FileName);
    }

    [Fact]
    public void ABOVE_THE_BAND_BEATS_BELOW_IT()
    {
        var q = new[] { Q("m-Q3_K_L.gguf", 12), Q("m-Q8_0.gguf", 15) };
        SameRegime(Big, q);
        var picked = HubSearch.Pick(q, Big, null, 4096, 27_000_000_000, false)!.Value;
        Assert.Equal(FitRegime.FitsGpu, picked.Fit);
        Assert.Equal("m-Q8_0.gguf", picked.Quant.FileName);
    }

    [Fact]
    public void THE_BANDS_OWN_RANGE_BEATS_ABOVE_IT()
    {
        var q = new[] { Q("m-IQ4_XS.gguf", 5), Q("m-Q8_0.gguf", 9) };
        Assert.Equal("m-IQ4_XS.gguf", HubSearch.Pick(q, Big, null, 4096, 9_000_000_000, false)!.Value.Quant.FileName);
    }

    [Fact]
    public void THE_PICK_DOES_NOT_DEPEND_ON_ORDER()
    {
        var q = new[] { Q("m-Q2_K.gguf", 2), Q("m-Q8_0.gguf", 3), Q("m-IQ3_XXS.gguf", 2) };
        var first = HubSearch.Pick(q, Big, null, 4096, 9_000_000_000, true)!.Value.Quant.FileName;
        var reversed = HubSearch.Pick(q.Reverse().ToArray(), Big, null, 4096, 9_000_000_000, true)!.Value.Quant.FileName;
        Assert.Equal("m-Q8_0.gguf", first);
        Assert.Equal(first, reversed);
    }

    //two files of one size fall to the name, so the pick is the same whichever arrives first
    [Fact]
    public void EQUAL_BYTES_FALL_TO_THE_NAME()
    {
        var q = new[] { Q("b-Q4_K_M.gguf", 5), Q("a-Q4_K_M.gguf", 5) };
        Assert.Equal("a-Q4_K_M.gguf", HubSearch.Pick(q, Big, null, 4096, 9_000_000_000, false)!.Value.Quant.FileName);
        Assert.Equal("a-Q4_K_M.gguf", HubSearch.Pick(q.Reverse().ToArray(), Big, null, 4096, 9_000_000_000, false)!.Value.Quant.FileName);
    }

    [Fact]
    public void UNDER_THE_FLOOR_NOTHING_IS_PICKED()
    {
        var q = new[] { Q("m-IQ1_S.gguf", 2), Q("m-IQ2_S.gguf", 3) };
        Assert.Null(HubSearch.Pick(q, Small8, null, 4096, 27_000_000_000, false));
        //a keeps the floor too, and only a typed repo id lifts it
        Assert.Null(HubSearch.Pick(q, Small8, null, 4096, 27_000_000_000, true));
        Assert.NotNull(HubSearch.Pick(q, Small8, null, 4096, 27_000_000_000, true, floorLifted: true));
    }

    [Fact]
    public void A_FILE_WITH_NO_TOKEN_IS_NEVER_PICKED() =>
        Assert.Null(HubSearch.Pick([Q("weights.gguf", 4)], Big, null, 4096, 9_000_000_000, true));

    [Fact]
    public void THE_BAND_STILL_LEADS()
    {
        var q = new[] { Q("m-Q4_K_M.gguf", 5), Q("m-Q8_0.gguf", 9) };
        Assert.Equal("m-Q4_K_M.gguf", HubSearch.Pick(q, Big, null, 4096, 9_000_000_000, false)!.Value.Quant.FileName);
    }

    //the generic form hands back the caller's own item, so a repo tag survives the pick
    [Fact]
    public void THE_TAG_COMES_BACK_WITH_THE_PICK()
    {
        var items = new[] { ("repo-a", Q("m-Q8_0.gguf", 9)), ("repo-b", Q("m-Q4_K_M.gguf", 5)) };
        var picked = HubSearch.Pick(items, i => i.Item2, Big, null, 4096, 9_000_000_000, false)!.Value;
        Assert.Equal("repo-b", picked.Item.Item1);
    }
}
