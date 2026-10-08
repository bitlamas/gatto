using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Home;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the download takes the file the user chose by reference: its repo, every shard it has, and that repo's own encoder
public sealed class ShelfDownloadTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-sdl-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }

    //the offer needs a fingerprint on every file, and the flow's arrival check would hash a file the fake cannot read
    private static HubQuant Q(string name, long bytes, bool sha = true) => new(name, bytes, sha ? "sha-" + name : null, Path: name);

    //three shards of one quant, each with its own fingerprint, so a fetch of the first alone is visible
    private static HubQuant Shards(string stem, long each) =>
        new(stem + "-00001-of-00003.gguf", each * 3, "sha-1", ShardCount: 3,
            Files: [.. Enumerable.Range(1, 3).Select(i =>
                new HubFile($"{stem}-0000{i}-of-00003.gguf", each, "sha-" + i))]);

    private static readonly FileRef StaticQ4 = new("bartowski", "bartowski/m-GGUF", "m-Q4_K_M.gguf");
    private static readonly FileRef I1Q4 = new("bartowski", "bartowski/m-i1-GGUF", "m.i1-Q4_K_M.gguf");

    //bartowski holds a static and an i1 repo with a Q4_K_M each, the i1 repo has no encoder and the static one has, unsloth has one too
    private static ModelRow Row(FileRef chosen, HubQuant? i1Weights = null, bool sha = true)
    {
        var uns = new RepoFiles("unsloth/m-GGUF", [Q("m-Q4_K_M.gguf", 5_000_000_000, sha)], [Q("mmproj-F16.gguf", 800_000_000, sha)], 2, 900);
        var bStatic = new RepoFiles("bartowski/m-GGUF", [Q("m-Q4_K_M.gguf", 5_100_000_000, sha)],
            [Q("mmproj-m-f16.gguf", 900_000_000, sha)], 2, 50);
        var bI1 = new RepoFiles("bartowski/m-i1-GGUF", [i1Weights ?? Q("m.i1-Q4_K_M.gguf", 5_200_000_000, sha)], [], 1, 40);
        return new ModelRow("m", "qwen", 0, 9_000_000_000, null, "qwen35", 32768, Vision: true,
        [
            new PublisherOffer("unsloth", [uns], new FileRef("unsloth", "unsloth/m-GGUF", "m-Q4_K_M.gguf"),
                uns.Quants[0], FitRegime.FitsGpu, 900),
            new PublisherOffer("bartowski", [bStatic, bI1], StaticQ4, bStatic.Quants[0], FitRegime.FitsGpu, 90),
        ], 1, chosen, FitRegime.FitsGpu, Structure: "dense");
    }

    private ModelFetchOffer Offer(ModelRow row)
    {
        GattoHome.EnsureInitialized(_home);
        var offer = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode).ModelOffer(row);
        return Assert.IsType<ModelFetchOffer>(offer);
    }

    [Fact]
    public void A_FILE_FROM_THE_SECOND_REPO_DOWNLOADS_FROM_THAT_REPO()
    {
        var offer = Offer(Row(I1Q4));

        Assert.Equal("bartowski/m-i1-GGUF", offer.RepoId);
        Assert.Equal("m.i1-Q4_K_M.gguf", offer.Weights.RepoPath);
    }

    [Fact]
    public void A_SHARD_SET_DOWNLOADS_EVERY_MEMBER()
    {
        var set = Shards("m.i1-Q4_K_M", 2_000_000_000) with { Path = "m.i1-Q4_K_M-00001-of-00003.gguf" };
        var chosen = I1Q4 with { Path = set.RepoPath };

        var offer = Offer(Row(chosen, set));

        Assert.Equal(3, offer.Weights.Members.Count);
        Assert.Equal(["m.i1-Q4_K_M-00001-of-00003.gguf", "m.i1-Q4_K_M-00002-of-00003.gguf", "m.i1-Q4_K_M-00003-of-00003.gguf"],
            offer.Weights.Members.Select(m => m.FileName));
    }

    //the static repo's encoder is the one fetched for the static file
    [Fact]
    public void THE_ENCODER_COMES_FROM_THE_CHOSEN_FILES_REPO()
    {
        Assert.Equal("mmproj-m-f16.gguf", Offer(Row(StaticQ4)).Projector?.FileName);
    }

    //the i1 repo has none, and neither the static repo's nor unsloth's encoder rides along with its file
    [Fact]
    public void A_REPO_WITH_NO_ENCODER_FETCHES_NONE()
    {
        Assert.Null(Offer(Row(I1Q4)).Projector);
    }

    //the model's source and the check's repo id both name the chosen file's repo
    [Fact]
    public void THE_SOURCE_AND_THE_CHECK_READ_THE_CHOSEN_REPO()
    {
        var probes = new WizardProbes { Rows = [Row(StaticQ4, sha: false)], Found = [] };
        var flow = new SetupFlow(probes);
        flow.StartPastEngine();
        flow.Answer(ShelfControls.PickAnswer("0", I1Q4));
        probes.Found = [new FoundModel(@"C:\Users\me\Downloads\m.i1-Q4_K_M.gguf", 5_200_000_000, null)];
        Assert.True(flow.PollForDownload());
        flow.Answer(SetupFlow.Landed);

        Assert.Equal(new Gatto.Roles.ModelSource("bartowski/m-i1-GGUF", "m.i1-Q4_K_M.gguf"), flow.Writes.CreateModel!.Source);

        flow.ResumeAfterWrites("m");
        flow.Answer(SetupFlow.Yes);
        Assert.True(SpinWait.SpinUntil(() => probes.AuditionStarts > 0, TimeSpan.FromSeconds(10)), "the check never started");
        Assert.Equal("bartowski/m-i1-GGUF", probes.AuditionRepoId);
    }
}
