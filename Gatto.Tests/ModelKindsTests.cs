using Gatto.Core.Acquire;

namespace Gatto.Tests;

//decide client-side what kind of thing a Hub listing is, from facts the listing already gives, and an absent tag passes through
public class ModelKindsTests
{
    private static readonly ModelKinds Kinds = ModelKinds.Load();

    [Fact]
    public void An_untagged_repo_passes_through()
    {
        //most allowlisted repos have no tag, an absent tag must pass through
        Assert.False(Kinds.WillNotServe(null, causal: null, arch: "qwen35"));
    }

    [Fact]
    public void A_tag_nobody_has_reviewed_passes_through()
    {
        //the rule must be a deny set, an allow set would silently hide every tag nobody has reviewed
        Assert.False(Kinds.WillNotServe("text-to-agent", causal: null, arch: "llama"));
    }

    [Theory]
    [InlineData("text-to-image")]      //models under these tags, flux and lumina2 for example, cannot be loaded by llama-server
    [InlineData("image-to-image")]
    [InlineData("image-to-video")]
    [InlineData("text-to-video")]
    [InlineData("image-text-to-video")]
    [InlineData("image-text-to-image")]
    [InlineData("automatic-speech-recognition")]
    [InlineData("text-to-speech")]
    [InlineData("feature-extraction")]
    [InlineData("sentence-similarity")]
    [InlineData("text-ranking")]
    public void A_reviewed_unservable_kind_is_refused(string tag) =>
        Assert.True(Kinds.WillNotServe(tag, causal: null, arch: "llama"));

    [Theory]
    //tags are uploader free-text and mistakes go both ways. a deny set costs one odd row, an allow set hides a real model
    [InlineData("summarization")]                //a real causal model is tagged this way on the allowlist.
    [InlineData("robotics")]                     //the measured example is Qwen3.8-Couture-Engine-27B, a qwen35 finetune
    [InlineData("translation")]                  //the measured examples are madlad400-3b-mt, hunyuan-dense and gemma4
    [InlineData("video-text-to-text")]           //the measured example is SmolVLM2-2.2B-Instruct, architecture llama
    [InlineData("visual-question-answering")]    //the measured example is OpenCaption-4B-VL, architecture qwen3vl
    [InlineData("audio-text-to-text")]           //the measured example is ultravox-v0_5-llama-3_2-1b, architecture llama
    [InlineData("text-to-3d")]                   //the measured example is LLaMA-Mesh, architecture llama, and it emits text
    [InlineData("text-generation")]
    [InlineData("image-text-to-text")]
    [InlineData("any-to-any")]
    public void A_kind_that_serves_is_kept(string tag) =>
        Assert.False(Kinds.WillNotServe(tag, causal: null, arch: "llama"));

    [Fact]
    public void A_listing_that_reports_causal_false_is_refused_however_it_is_tagged()
    {
        //a listing reporting causal false is refused whatever the tag says, with an arch no other rule refuses
        Assert.True(Kinds.WillNotServe("image-text-to-text", causal: false, arch: "diffusion-gemma"));
        Assert.True(Kinds.WillNotServe(null, causal: false, arch: "wavtokenizer-dec"));
        //include the real case too, two agreeing signals cannot be the only evidence the rule works
        Assert.True(Kinds.WillNotServe("text-generation", causal: false, arch: "bert"));
    }

    [Fact]
    public void Causal_is_evidence_only_when_it_says_false()
    {
        //the converter writes the causal key only to say false, absent is silence and true is confirmation
        Assert.False(Kinds.WillNotServe("text-generation", causal: null, arch: "llama"));
        Assert.False(Kinds.WillNotServe("text-generation", causal: true, arch: "llama"));
    }

    [Fact]
    public void An_untagged_embedding_architecture_is_refused_by_evidence_rather_than_by_accident()
    {
        //a real embedding repo has no pipeline tag and no causal key, so only the architecture signal reaches it
        Assert.True(Kinds.WillNotServe(null, causal: null, arch: "gemma-embedding"));
        Assert.True(Kinds.WillNotServe(null, causal: null, arch: "bert"));
    }

    [Fact]
    public void A_generative_architecture_is_never_refused_by_the_architecture_rule()
    {
        //the refusal set holds embedding architectures, a reasoning model named pangu-embedded passes
        Assert.False(Kinds.WillNotServe(null, causal: null, arch: "pangu-embedded"));
        Assert.False(Kinds.WillNotServe(null, causal: null, arch: "t5"));
        Assert.False(Kinds.WillNotServe(null, causal: null, arch: "qwen3"));
        Assert.False(Kinds.WillNotServe(null, causal: null, arch: null));
    }

    [Fact]
    public void The_review_is_dated_and_the_sets_are_not_empty()
    {
        //the review date must exist and the sets must not be empty, an emptied set would disarm a user-facing exclusion
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", Kinds.Reviewed);
        Assert.NotEmpty(Kinds.UnservableKinds);
        Assert.NotEmpty(Kinds.EmbeddingArchitectures);
    }

    [Fact]
    public void The_tag_and_architecture_comparisons_are_ordinal_and_case_sensitive_to_neither()
    {
        //both are machine identifiers, the comparison is ordinal and case-folded
        Assert.True(Kinds.WillNotServe("Text-To-Image", causal: null, arch: null));
        Assert.True(Kinds.WillNotServe(null, causal: null, arch: "GEMMA-EMBEDDING"));
    }
}
