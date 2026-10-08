using Gatto.Core.Acquire;

namespace Gatto.Tests;

//an original is the releaser's own tuned model of one generation, keyed by its name less the build suffixes
public class OriginalsTests
{
    private static readonly Families F = Families.Load();

    private static HubListing Row(string id, string? tag = null) => new(id, null, null, false, 0, PipelineTag: tag);

    private static IReadOnlyList<HubListing> Rows(string org, params string[] names) =>
        [.. names.Select(n => Row(org + "/" + n))];

    [Theory]
    [InlineData("gemma-4-E4B-it-qat-q4_0-unquantized-assistant", "gemma-4-E4B-it")]
    [InlineData("gemma-4-31B-it-qat-q4_0-gguf", "gemma-4-31B-it")]
    [InlineData("Qwen3.8-27B-FP8", "Qwen3.8-27B")]
    [InlineData("Qwen3-8B-GGUF", "Qwen3-8B")]
    [InlineData("Qwen3-4B-MLX-4bit", "Qwen3-4B")]
    [InlineData("Qwen3.5-9B-Base-FP8", "Qwen3.5-9B-Base")]
    [InlineData("gemma-4-12B-it-newthing", "gemma-4-12B-it-newthing")]
    public void THE_MODEL_KEY(string source, string key)
    {
        var builds = source.StartsWith("gemma") ? F.Entries["gemma"].Builds : F.Entries["qwen"].Builds;
        Assert.Equal(key, Originals.ModelKey(source, builds));
    }

    //owning the source does not make an original: the side models fail the prefix and the bare name is the base model
    [Fact]
    public void SIDE_MODELS_AND_BASE_MODELS_ARE_NOT_ORIGINALS()
    {
        var rows = Rows("google", "medgemma-4b-it", "functiongemma-270m-it", "translategemma-4b-it",
            "gemma-4-31B", "gemma-4-31B-it", "gemma-4-31B-it-qat-q4_0-unquantized");
        var keys = Originals.Of(F.Entries["gemma"], "gemma-4-", rows, [], ModelKinds.Load()).Select(s => s.ModelKey).Distinct();
        Assert.Equal(["gemma-4-31B-it"], keys);
    }

    [Fact]
    public void A_QWEN_SIDE_MODEL_FAILS_THE_PREFIX()
    {
        var rows = Rows("Qwen", "Qwen-AgentWorld-35B-A3B", "Qwen3.8-27B");
        var keys = Originals.Of(F.Entries["qwen"], "Qwen3.8-", rows, [], ModelKinds.Load()).Select(s => s.ModelKey);
        Assert.Equal(["Qwen3.8-27B"], keys);
    }

    [Fact]
    public void THE_RELEASERS_GGUF_REPOS_ARE_NOT_SOURCES()
    {
        var rows = new[] { Row("Qwen/Qwen3-8B"), Row("Qwen/Qwen3-8B-GGUF") };
        var src = Originals.Of(F.Entries["qwen"], "Qwen3-", rows, [rows[1]], ModelKinds.Load());
        Assert.Equal(["Qwen/Qwen3-8B"], src.Select(s => s.Id));
    }

    [Fact]
    public void A_REFUSED_KIND_IS_NOT_AN_ORIGINAL() =>
        Assert.Empty(Originals.Of(F.Entries["qwen"], "Qwen3-",
            [Row("Qwen/Qwen3-ASR-1.7B", "automatic-speech-recognition")], [], ModelKinds.Load()));

    //a flagship carries nothing after its version, so the name equal to the prefix less its dash is in the generation
    [Fact]
    public void A_FLAGSHIP_WITH_NOTHING_AFTER_ITS_VERSION()
    {
        var rows = Rows("zai-org", "GLM-5.3", "GLM-5.3-Flash", "GLM-5.30-X");
        var keys = Originals.Of(F.Entries["glm"], "GLM-5.3-", rows, [], ModelKinds.Load()).Select(s => s.ModelKey);
        Assert.Equal(["GLM-5.3", "GLM-5.3-Flash"], keys);
    }

    [Fact]
    public void THE_GENERATION_IS_THE_PREFIXS_PLACE()
    {
        var src = Originals.Of(F.Entries["qwen"], "Qwen3.5-", Rows("Qwen", "Qwen3.5-9B"), [], ModelKinds.Load());
        Assert.Equal(2, src.Single().Generation);
    }

    [Fact]
    public void RELEASER_BUILDS_JOIN_THEIR_MODEL()
    {
        var gemma = F.Entries["gemma"];
        var ggufs = Rows("google", "gemma-4-31B-it-qat-q4_0-gguf");
        var sources = Originals.Of(gemma, "gemma-4-", Rows("google", "gemma-4-31B-it"), ggufs, ModelKinds.Load());
        var joined = Originals.ReleaserBuilds(gemma, "gemma-4-", ggufs, sources);
        Assert.Equal(("gemma-4-31B-it", "google/gemma-4-31B-it-qat-q4_0-gguf"), (joined.Single().ModelKey, joined.Single().Repo.RepoId));
    }

    //the measured Qwen3 cases: an embedding GGUF goes with its refused model, and the odd kinds the deny-set does not name are kept
    [Fact]
    public void THE_QWEN3_KIND_CASES()
    {
        var qwen = F.Entries["qwen"];
        var rows = new[]
        {
            Row("Qwen/Qwen3-Embedding-0.6B", "feature-extraction"),
            Row("Qwen/Qwen3-Omni-30B-A3B-Instruct", "any-to-any"),
            Row("Qwen/Qwen3-TTS-Tokenizer-12Hz", "audio-to-audio"),
            Row("Qwen/Qwen3-ForcedAligner-0.6B-hf", "token-classification"),
        };
        var ggufs = Rows("Qwen", "Qwen3-Embedding-0.6B-GGUF");
        var sources = Originals.Of(qwen, "Qwen3-", rows, ggufs, ModelKinds.Load());
        Assert.Equal(["Qwen3-Omni-30B-A3B-Instruct", "Qwen3-TTS-Tokenizer-12Hz", "Qwen3-ForcedAligner-0.6B-hf"],
            sources.Select(s => s.ModelKey));
        Assert.Empty(Originals.ReleaserBuilds(qwen, "Qwen3-", ggufs, sources));
    }
}
