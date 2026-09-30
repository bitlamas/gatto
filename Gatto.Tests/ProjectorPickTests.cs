using Gatto.Core.Acquire;

namespace Gatto.Tests;

//tests which vision encoder a repo would offer, and the reason that one is chosen.
public class ProjectorPickTests
{
    private static HubQuant P(string name, long bytes, string? sha = "oid") => new(name, bytes, sha);

    [Fact]
    public void THE_REFERENCE_ENCODER_WINS_over_a_larger_quantized_one()
    {
        //the f16 file is deliberately the smaller one, so a rule that took the biggest fails here
        var best = ProjectorPick.Best([
            P("mmproj-model-Q8_0.gguf", 1_200_000_000),
            P("mmproj-model-f16.gguf", 900_000_000),
        ]);

        Assert.Equal("mmproj-model-f16.gguf", best!.FileName);
    }

    [Fact]
    public void A_REPO_WITH_ONLY_A_QUANTIZED_ENCODER_STILL_GETS_AN_OFFER()
    {
        //reference precision is a preference, so a quantized encoder still gets an offer
        var best = ProjectorPick.Best([P("mmproj-model-Q8_0.gguf", 1_200_000_000)]);

        Assert.Equal("mmproj-model-Q8_0.gguf", best!.FileName);
    }

    [Fact]
    public void PRECISION_IS_MATCHED_ON_NAME_TOKENS_and_not_as_a_substring()
    {
        //match f16 as a whole name segment, since a substring match fires on a name that merely embeds it
        var best = ProjectorPick.Best([
            P("mmproj-af16k-tuned-Q8_0.gguf", 2_000_000_000),
            P("mmproj-plain-Q6_K.gguf", 1_000_000_000),
        ]);

        //neither file has reference precision, so the largest must win and the embedded f16 must not match as a token
        Assert.Equal("mmproj-af16k-tuned-Q8_0.gguf", best!.FileName);
    }

    [Fact]
    public void THE_PICK_IS_DETERMINISTIC_when_two_encoders_tie()
    {
        //two runs against one repo must not disagree about what the user was offered, so the file name breaks the final tie
        var a = ProjectorPick.Best([P("mmproj-b-f16.gguf", 900_000_000), P("mmproj-a-f16.gguf", 900_000_000)]);
        var b = ProjectorPick.Best([P("mmproj-a-f16.gguf", 900_000_000), P("mmproj-b-f16.gguf", 900_000_000)]);

        Assert.Equal(a!.FileName, b!.FileName);
        Assert.Equal("mmproj-a-f16.gguf", a.FileName);
    }

    [Fact]
    public void NO_ENCODERS_MEANS_NO_PICK()
    {
        Assert.Null(ProjectorPick.Best([]));
        Assert.Null(ProjectorPick.Best(null));
    }

    [Fact]
    public void THE_ENCODERS_SHA_SURVIVES_so_the_download_can_be_checked()
    {
        //the chosen encoder keeps its sha, since that is what Checksum verifies a finished download against
        var best = ProjectorPick.Best([P("mmproj-model-f16.gguf", 900_000_000, sha: "deadbeef")]);

        Assert.Equal("deadbeef", best!.Sha256);
    }
}
