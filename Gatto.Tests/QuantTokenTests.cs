using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Client;
using Gatto.Core.Models;
using Gatto.Roles;
using Gatto.Terminal;
using System.Text.Json;

namespace Gatto.Tests;

//the tail on Q\d+ or IQ\d+ takes only allowlist elements, so one non-member element voids the segment. the shelf and serve status share that rule
public class QuantTokenTests
{

    [Theory]
    [InlineData("m-Q8_0.gguf", "Q8_0")]
    [InlineData("m-Q4_1.gguf", "Q4_1")]
    [InlineData("m-Q2_K.gguf", "Q2_K")]
    [InlineData("m-Q3_K_L.gguf", "Q3_K_L")]
    [InlineData("m-Q4_K_S.gguf", "Q4_K_S")]
    [InlineData("m-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("m-Q5_K_S.gguf", "Q5_K_S")]
    [InlineData("m-Q5_K_M.gguf", "Q5_K_M")]
    [InlineData("m-Q6_K.gguf", "Q6_K")]
    [InlineData("m-IQ1_S.gguf", "IQ1_S")]
    [InlineData("m-IQ1_M.gguf", "IQ1_M")]
    [InlineData("m-IQ2_XXS.gguf", "IQ2_XXS")]
    [InlineData("m-IQ2_XS.gguf", "IQ2_XS")]
    [InlineData("m-IQ2_S.gguf", "IQ2_S")]
    [InlineData("m-IQ2_M.gguf", "IQ2_M")]
    [InlineData("m-IQ3_XXS.gguf", "IQ3_XXS")]
    [InlineData("m-IQ3_S.gguf", "IQ3_S")]
    [InlineData("m-IQ3_M.gguf", "IQ3_M")]
    [InlineData("m-IQ4_NL.gguf", "IQ4_NL")]
    [InlineData("m-IQ4_XS.gguf", "IQ4_XS")]
    //keep XL in the allowlist, a vendor's dynamic quant really uses that label (a table of just k, s, m and l would drop it)
    [InlineData("m-UD-Q4_K_XL.gguf", "Q4_K_XL")]
    [InlineData("m-BF16.gguf", "BF16")]
    [InlineData("m-f16.gguf", "F16")]
    [InlineData("m-F32.gguf", "F32")]
    //the shard suffix comes off before the grammar runs
    [InlineData("m-Q5_K_M-00001-of-00003.gguf", "Q5_K_M")]
    //the 4-bit float family, the only quant some mixture-of-experts repositories ship
    [InlineData("gpt-oss-20b-MXFP4.gguf", "MXFP4")]
    [InlineData("Qwen3.5-122B-A10B-MXFP4_MOE.gguf", "MXFP4_MOE")]
    [InlineData("m-mxfp4_moe-00001-of-00003.gguf", "MXFP4_MOE")]
    public void every_real_quant_family_survives_the_allowlist(string fileName, string expected)
    {
        //every quant token found on real model disks has a row above, so nothing already stamped changes
        Assert.Equal(expected, QuantToken.Of(fileName));
    }

    [Theory]
    //a made-up token like Q4_K_M_V2 must never be stamped, one unknown element voids the whole segment (the field's contract is never guessed)
    [InlineData("model-Q4_K_M_v2.gguf")]
    //vendor words like pruned name a changed file, and where they sit must not decide, so these rows are refused too
    [InlineData("model-Q4_K_M_pruned.gguf")]
    [InlineData("model-IQ2_XXS_experimental.gguf")]
    //one member element beside a non-member still fails, the rule reads each element on its own
    [InlineData("model-Q4_K_MOE.gguf")]
    [InlineData("model-MXFP4_XL.gguf")]
    [InlineData("model-MXFP8.gguf")]
    public void one_non_member_element_makes_the_whole_segment_not_a_quant(string fileName)
    {
        //an unknown segment yields nothing rather than a guess
        Assert.Null(QuantToken.Of(fileName));
    }

    [Fact]
    public void THE_FENCE_new_families_are_ruled_in_by_adding_an_ELEMENT()
    {
        //accepting Q4_M_K_0 is deliberate, the contract is never guessed, and promoting the element table to a token table would shrink what the stamp names
        Assert.Equal("Q4_M_K_0", QuantToken.Of("model-Q4_M_K_0.gguf"));
    }

    [Fact]
    public void THE_SHELF_AND_SERVE_STATUS_AGREE_ACROSS_THE_NEW_NULLS()
    {
        //two surfaces describing the same file must agree on its quant. the good row is load-bearing, an absence check also passes on a table that renders nothing
        const string junk = "model-Q4_K_M_v2.gguf";
        const string good = "model-Q4_K_M.gguf";

        //an unknown token leaves the shelf cell empty, rather than an invented quant
        Assert.DoesNotContain("Q4_K_M_V2", string.Concat(Shelf(junk)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Q4_K_M", string.Concat(Shelf(good)), StringComparison.Ordinal);

        //serve status --json omits the quant key for a token it can't read, rather than writing null (that output's own contract)
        Assert.False(StatusQuant(junk, out _));
        Assert.True(StatusQuant(good, out var token));
        Assert.Equal("Q4_K_M", token);

        //the shelf and serve status both read QuantToken.Of, so the two can't drift apart
        Assert.Null(QuantToken.Of(junk));
        Assert.Equal("Q4_K_M", QuantToken.Of(good));
    }

    //a token can end a segment after an underscore, and each underscore is tried from the first, so 26B_q4_0 reads Q4_0 and not the last 0
    [Theory]
    [InlineData("gemma-4-26B_q4_0-it.gguf", "Q4_0")]
    [InlineData("model_Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("Qwen3.5-9B-UD-Q4_K_XL.gguf", "Q4_K_XL")]
    [InlineData("mmproj-F16.gguf", "F16")]
    [InlineData("weights_v2_1.gguf", null)]
    public void A_TOKEN_AFTER_ANY_UNDERSCORE_IS_READ(string name, string? token) =>
        Assert.Equal(token, QuantToken.Of(name));

    //the floor reads one number per token: the bits after Q or IQ, or the float width
    [Theory]
    [InlineData("Q4_K_M", 4)] [InlineData("IQ4_XS", 4)] [InlineData("Q4_0", 4)]
    [InlineData("IQ3_XXS", 3)] [InlineData("Q3_K_L", 3)] [InlineData("Q2_K", 2)]
    [InlineData("IQ1_S", 1)] [InlineData("MXFP4", 4)] [InlineData("BF16", 16)] [InlineData("F32", 32)]
    public void THE_CLASS_IS_THE_NUMBER_AFTER_Q(string token, int cls) =>
        Assert.Equal(cls, QuantToken.ClassOf(token));

    [Fact]
    public void NO_TOKEN_HAS_NO_CLASS() => Assert.Null(QuantToken.ClassOf(null));

    //the floor is class 4 up, 3 up at 20B or more, everything with a token when lifted, and never a file with no token
    [Theory]
    [InlineData("m-Q4_K_M.gguf", 9_000_000_000L, false, true)]
    [InlineData("m-Q3_K_L.gguf", 9_000_000_000L, false, false)]
    [InlineData("m-Q3_K_L.gguf", 27_000_000_000L, false, true)]
    [InlineData("m-IQ2_XXS.gguf", 27_000_000_000L, false, false)]
    [InlineData("m-IQ2_XXS.gguf", 27_000_000_000L, true, true)]
    [InlineData("m-BF16.gguf", 9_000_000_000L, false, true)]
    [InlineData("weights.gguf", 9_000_000_000L, true, false)]
    public void THE_FLOOR(string name, long total, bool lifted, bool admitted) =>
        Assert.Equal(admitted, QuantToken.AtFloor(name, total, lifted));

    //a model with no total takes the Q4 floor
    [Fact]
    public void NO_TOTAL_TAKES_THE_Q4_FLOOR() =>
        Assert.False(QuantToken.AtFloor("m-Q3_K_L.gguf", null, false));

    private static IReadOnlyList<string> Shelf(string fileName) =>
        ShelfTable.Render(
            [ShelfRows.Of("o/m", "o", new HubQuant(fileName, 4_000_000_000, "sha"),
                FitRegime.FitsGpu, NativeCtx: 262144, Vision: false, Badge: null,
                Downloads: 10, Gated: false)],
            new Theme(new TermCaps(Rich: false, TrueColor: false)), 140, Gatto.Core.Hardware.MachineShape.Discrete, glyphs: GlyphSet.Unicode)
        .Select(r => r.Text).ToList();

    private static bool StatusQuant(string fileName, out string? token)
    {
        using var doc = JsonDocument.Parse(
            ServeStatusJson.Build(new LoadedModel(Path.Combine(@"C:\models", fileName), 8192),
                "p", matchesModel: true));
        var present = doc.RootElement.TryGetProperty("quant", out var q);
        token = present ? q.GetString() : null;
        return present;
    }
}
