using Gatto.Cli.Setup;
using Gatto.Core.Acquire;

namespace Gatto.Tests.Setup;

//the local shelf names a model without its quant, puts its files in one row, and picks the row's file by the rules in order
public class LocalGroupingTests
{
    [Theory]
    [InlineData("gemma-4-E4B-it-Q4_K_M.gguf", "gemma-4-E4B-it")]
    [InlineData("Qwen3.6-35B-A3B-UD-Q4_K_XL-00001-of-00003.gguf", "Qwen3.6-35B-A3B")]
    [InlineData("gemma-4-26B_q4_0-it.gguf", "gemma-4-26B")]
    [InlineData("model.gguf", "model")]
    [InlineData("Some-Model-GGUF-Q8_0.gguf", "Some-Model")]
    [InlineData("Q4_K_M.gguf", "Q4_K_M")]
    public void THE_MODEL_NAME_DROPS_THE_QUANT(string file, string name) =>
        Assert.Equal(name, LocalShelf.ModelNameOf(file));

    [Theory]
    [InlineData("a-UD-Q4_K_XL.gguf", "a-")]
    [InlineData("a-Q4_K_M.gguf", "a-")]
    [InlineData("a.gguf", null)]
    public void THE_HEAD_STOPS_BEFORE_THE_TOKEN_AND_ITS_UD(string file, string? head) =>
        Assert.Equal(head, QuantToken.HeadBefore(file));

    //the head is found where Of finds the token, so the two never disagree on whether a name declares one
    [Theory]
    [InlineData("gemma-4-E4B-it-Q4_K_M.gguf")]
    [InlineData("gemma-4-26B_q4_0-it.gguf")]
    [InlineData("a-UD-Q4_K_XL-00001-of-00003.gguf")]
    [InlineData("model.gguf")]
    [InlineData("af16k.gguf")]
    [InlineData("x-BF16.gguf")]
    [InlineData("x.MXFP4_MOE.gguf")]
    public void THE_HEAD_AND_THE_TOKEN_AGREE(string name) =>
        Assert.Equal(QuantToken.Of(name) is null, QuantToken.HeadBefore(name) is null);

    [Fact]
    public void THE_SERVED_FILE_BEATS_THE_ACTIVE_ONE()
    {
        var row = LocalShelf.RowFileOf(TwoQuants(), ShelfFixtures.Big,
            served: f => f.Path.EndsWith("Q8_0.gguf"), activePath: @"C:\m\x-Q4_K_M.gguf", listed: _ => true);
        Assert.EndsWith("Q8_0.gguf", row!.Path);
    }

    [Fact]
    public void TWO_QUANTS_OF_ONE_NAME_ARE_ONE_GROUP() =>
        Assert.Single(LocalShelf.Group(ShelfFixtures.LocalScan(
            (@"C:\m\gemma-4-E4B-it-Q4_K_M.gguf", "gemma4", "8B", 5), (@"C:\m\gemma-4-E4B-it-Q8_0.gguf", "gemma4", "8B", 9))));

    [Fact]
    public void ONE_NAME_IN_TWO_FOLDERS_IS_ONE_GROUP() =>
        Assert.Single(LocalShelf.Group(ShelfFixtures.LocalScan(
            (@"C:\a\x-Q4_K_M.gguf", "llama", "8B", 5), (@"D:\b\x-Q8_0.gguf", "llama", "8B", 9))));

    [Fact]
    public void A_NAME_IS_ONE_GROUP_WHATEVER_ITS_CASE() =>
        Assert.Single(LocalShelf.Group(ShelfFixtures.LocalScan(
            (@"C:\a\Qwen3-8B-Q4_K_M.gguf", "qwen3", "8B", 5), (@"C:\b\qwen3-8b-Q8_0.gguf", "qwen3", "8B", 9))));

    [Fact]
    public void A_GENERIC_NAME_WITH_TWO_ARCHITECTURES_IS_TWO_GROUPS() =>
        Assert.Equal(2, LocalShelf.Group(ShelfFixtures.LocalScan(
            (@"C:\a\model.gguf", "llama", "8B", 5), (@"C:\b\model.gguf", "qwen3", "4B", 3))).Count);

    //with no size label the architecture alone tells two generic names apart, and one model's quants stay one row
    [Theory]
    [InlineData(@"C:\models\a\model.gguf", "llama", null, @"D:\models\b\model.gguf", "qwen3", null, 2)]
    [InlineData(@"C:\llama\ggml-model-Q4_K_M.gguf", "llama", null, @"C:\qwen\ggml-model-Q4_K_M.gguf", "qwen3", null, 2)]
    [InlineData(@"C:\a\model.gguf", "llama", "8B", @"C:\b\model.gguf", "qwen3", null, 2)]
    [InlineData(@"C:\a\gemma-4-E4B-it-Q4_K_M.gguf", "gemma3", null, @"C:\b\gemma-4-E4B-it-Q8_0.gguf", "gemma3", null, 1)]
    public void THE_ARCHITECTURE_SPLITS_A_NAME_THE_LABELS_CANNOT(string a, string archA, string? labelA,
        string b, string archB, string? labelB, int rows) =>
        Assert.Equal(rows, LocalShelf.Group(ShelfFixtures.LocalScan((a, archA, labelA, 5), (b, archB, labelB, 4))).Count);

    [Fact]
    public void A_FILE_WITH_NO_HEADER_FIELDS_JOINS_THE_FIRST_GROUP()
    {
        var groups = LocalShelf.Group(ShelfFixtures.LocalScan(
            (@"C:\a\model.gguf", "llama", "8B", 5), (@"C:\b\model.gguf", "qwen3", "4B", 3), (@"C:\c\model.gguf", null, null, 2)));
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Files.Count);
    }

    [Fact]
    public void A_FILE_WITH_ONE_FIELD_JOINS_THE_GROUP_THAT_MATCHES_IT()
    {
        var groups = LocalShelf.Group(ShelfFixtures.LocalScan(
            (@"C:\a\model.gguf", "llama", "8B", 5), (@"C:\b\model.gguf", "qwen3", "4B", 3), (@"C:\c\model.gguf", "qwen3", null, 2)));
        Assert.Equal(2, groups[1].Files.Count);
    }

    private static LocalShelf.LocalGroup Only(params (string, string?, string?, int)[] files) =>
        Assert.Single(LocalShelf.Group(ShelfFixtures.LocalScan(files)));

    private static LocalShelf.LocalGroup TwoQuants() =>
        Only((@"C:\m\x-Q4_K_M.gguf", "llama", "8B", 5), (@"C:\m\x-Q8_0.gguf", "llama", "8B", 9));

    private static LocalShelf.LocalGroup SameNameTwoFolders() =>
        Only((@"C:\a\x-Q4_K_M.gguf", "llama", "8B", 5), (@"D:\b\x-Q4_K_M.gguf", "llama", "8B", 5));

    private static LocalShelf.LocalGroup PartialQ6AndCompleteQ4()
    {
        var scan = ShelfFixtures.LocalScan((@"C:\m\x-Q6_K-00001-of-00003.gguf", "llama", "8B", 3), (@"C:\m\x-Q4_K_M.gguf", "llama", "8B", 5));
        return Assert.Single(LocalShelf.Group([scan[0] with { ShardsPresent = 2 }, scan[1]]));
    }

    private static LocalShelf.LocalGroup Q3OnlyNoLabel() => Only((@"C:\m\x-Q3_K_M.gguf", "llama", null, 4));

    [Fact]
    public void A_SERVED_FILE_WINS() =>
        Assert.EndsWith("Q8_0.gguf", LocalShelf.RowFileOf(TwoQuants(), ShelfFixtures.Big,
            served: f => f.Path.EndsWith("Q8_0.gguf"), activePath: null, listed: _ => false)!.Path);

    [Fact]
    public void THE_ACTIVE_FILE_WINS_BY_PATH_ACROSS_FOLDERS() =>
        Assert.Equal(@"D:\b\x-Q4_K_M.gguf", LocalShelf.RowFileOf(SameNameTwoFolders(), ShelfFixtures.Big,
            served: _ => false, activePath: @"D:\b\x-Q4_K_M.gguf", listed: _ => true)!.Path);

    [Fact]
    public void A_LISTED_FILE_BEATS_A_BETTER_ONE() =>
        Assert.EndsWith("Q4_K_M.gguf", LocalShelf.RowFileOf(TwoQuants(), ShelfFixtures.Big,
            served: _ => false, activePath: null, listed: f => f.Path.EndsWith("Q4_K_M.gguf"))!.Path);

    [Fact]
    public void THE_RULE_PICKS_THE_BAND_OVER_A_HEAVIER_FILE() =>
        Assert.EndsWith("Q4_K_M.gguf", LocalShelf.RowFileOf(TwoQuants(), ShelfFixtures.Big,
            served: _ => false, activePath: null, listed: _ => false)!.Path);

    [Fact]
    public void WITH_NO_HARDWARE_THE_RULE_PICKS_BY_QUANT_CLASS_ALONE() =>
        Assert.EndsWith("Q4_K_M.gguf", LocalShelf.RowFileOf(TwoQuants(), null,
            served: _ => false, activePath: null, listed: _ => false)!.Path);

    [Fact]
    public void A_PARTIAL_SET_NEVER_BEATS_A_COMPLETE_FILE() =>
        Assert.EndsWith("Q4_K_M.gguf", LocalShelf.RowFileOf(PartialQ6AndCompleteQ4(), ShelfFixtures.Big,
            served: _ => false, activePath: null, listed: _ => true)!.Path);

    [Fact]
    public void UNDER_THE_FLOOR_WITH_NO_LABEL_RULE_FOUR_PICKS_NONE() =>
        Assert.Null(LocalShelf.RowFileOf(Q3OnlyNoLabel(), ShelfFixtures.Big,
            served: _ => false, activePath: null, listed: _ => false));
}
