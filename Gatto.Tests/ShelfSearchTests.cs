using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Tests.Setup;

namespace Gatto.Tests;

//a model row prices every approved publisher, keeps drafts and gated repos out, and takes its file by the releaser rule
public class ShelfSearchTests
{
    private const long GB = 1_000_000_000L;
    private static readonly Families F = Families.Load();

    private static HubListing L(string id, long? total, long downloads = 0, string arch = "gemma4",
        long ctx = 131_072, bool gated = false) =>
        new(id, arch, ctx, gated, downloads, Params: total);

    private static HubQuant Q(string name, double gb) => new(name, (long)(gb * GB), null, Path: name);

    private static HubTree T(params HubQuant[] quants) => new(quants, [], quants.Length);

    private static HubTree TV(HubQuant[] quants, params HubQuant[] projectors) =>
        new(quants, projectors, quants.Length + projectors.Length);

    private static ModelRow Price(string model, string? releaser, HardwareClass hw,
        params (HubListing, HubTree)[] repos) =>
        ShelfSearch.Price(model, "gemma", 0, releaser, repos, F, hw, 8192, lifted: false);

    [Fact]
    public void THE_RELEASER_LEADS_THE_PANE()
    {
        var row = Price("gemma-4-26B-A4B-it", "google", ShelfMachines.Apu8060S,
            (L("google/gemma-4-26B-A4B-it-qat-q4_0-gguf", 26 * GB, 100), T(Q("gemma-4-26B_q4_0-it.gguf", 15))),
            (L("bartowski/gemma-4-26B-A4B-it-GGUF", 26 * GB, 500), T(Q("g-Q4_K_M.gguf", 16))),
            (L("unsloth/gemma-4-26B-A4B-it-GGUF", 26 * GB, 1000), T(Q("g-Q4_K_M.gguf", 16))));
        Assert.Equal(["google", "unsloth", "bartowski"], row.Publishers.Select(p => p.Org));
    }

    [Fact]
    public void THE_ROW_TAKES_THE_BETTER_OF_RELEASER_AND_UNSLOTH()
    {
        var row = Price("gemma-4-26B-A4B-it", "google", ShelfMachines.Apu8060S,
            (L("google/gemma-4-26B-A4B-it-qat-q4_0-gguf", 26 * GB, 100), T(Q("gemma-4-26B_q4_0-it.gguf", 15))),
            (L("unsloth/gemma-4-26B-A4B-it-GGUF", 26 * GB, 1000), T(Q("g-Q6_K.gguf", 21))));
        Assert.Equal(row.Publishers[0].Fit, row.Publishers[1].Fit);
        Assert.Equal("unsloth", row.Publishers[row.RowPublisher].Org);
        Assert.Equal("unsloth/gemma-4-26B-A4B-it-GGUF", row.RowFile!.RepoId);
    }

    [Fact]
    public void A_TIE_GOES_TO_THE_RELEASER()
    {
        for (var i = 0; i < 50; i++)
        {
            var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
                (L("unsloth/gemma-4-31B-it-GGUF", 31 * GB, 1000), T(Q("g-Q4_K_M.gguf", 19))),
                (L("google/gemma-4-31B-it-gguf", 31 * GB, 10), T(Q("g-Q4_K_M.gguf", 19))));
            Assert.Equal("google", row.RowFile!.Publisher);
        }
    }

    [Fact]
    public void NEITHER_HAS_A_PICK_SO_THE_NEXT_BY_DOWNLOADS()
    {
        var row = Price("gemma-4-12B-it", "google", ShelfMachines.Apu8060S,
            (L("google/gemma-4-12B-it-gguf", 12 * GB, 10), T(Q("g-IQ2_XXS.gguf", 4))),
            (L("unsloth/gemma-4-12B-it-GGUF", 12 * GB, 1000), T(Q("g-IQ1_S.gguf", 3))),
            (L("ggml-org/gemma-4-12B-it-GGUF", 12 * GB, 50), T(Q("g-Q8_0.gguf", 13))),
            (L("bartowski/gemma-4-12B-it-GGUF", 12 * GB, 500), T(Q("g-Q4_K_M.gguf", 7))));
        Assert.Equal("bartowski", row.RowFile!.Publisher);
        Assert.Equal("bartowski", row.Publishers[row.RowPublisher].Org);
    }

    [Fact]
    public void TWO_REPOS_ONE_PUBLISHER()
    {
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
            (L("bartowski/gemma-4-31B-it-GGUF", 31 * GB, 100), T(Q("g-Q4_K_M.gguf", 18))),
            (L("bartowski/gemma-4-31B-it-i1-GGUF", 31 * GB, 50), T(Q("g.i1-Q4_K_M.gguf", 19))));
        var bart = Assert.Single(row.Publishers);
        Assert.Equal(2, bart.Repos.Count);
        Assert.Equal("bartowski/gemma-4-31B-it-i1-GGUF", bart.Pick!.RepoId);
        Assert.Equal(150, bart.Downloads);
    }

    //a draft model's repo folds into no publisher, votes no total and is never the pick, even when it fits the card and nothing else does
    [Fact]
    public void A_DRAFT_REPO_GIVES_NOTHING()
    {
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Vega,
            (L("unsloth/gemma-4-31B-it-assistant-GGUF", 470_000_000, 900, arch: "gemma4-assistant"), T(Q("d-Q4_K_M.gguf", 0.3))),
            (L("unsloth/gemma-4-31B-it-GGUF", 31 * GB, 1000), T(Q("g-Q4_K_M.gguf", 12))));
        var unsloth = Assert.Single(row.Publishers);
        Assert.Equal(["unsloth/gemma-4-31B-it-GGUF"], unsloth.Repos.Select(r => r.RepoId));
        Assert.Equal(31 * GB, row.Params);
        Assert.Equal("g-Q4_K_M.gguf", row.RowFile!.Path);
        Assert.Equal(FitRegime.FitsRamOnly, row.Fit);
    }

    [Fact]
    public void A_TREE_WITH_NO_WEIGHTS()
    {
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
            (L("ggml-org/gemma-4-31B-it-GGUF", 31 * GB, 10), TV([], Q("mmproj-F16.gguf", 1))),
            (L("unsloth/gemma-4-31B-it-GGUF", 31 * GB, 1000), T(Q("g-Q4_K_M.gguf", 18))));
        var ggml = row.Publishers.Single(p => p.Org == "ggml-org");
        Assert.Null(ggml.Pick);
        Assert.Null(ggml.Fit);
        Assert.Equal("unsloth", row.RowFile!.Publisher);
    }

    [Theory]
    [InlineData(new[] { 27_000_000_000L, 27_000_000_000L, 27_800_000_000L }, 27_000_000_000L)]
    [InlineData(new[] { 27_000_000_000L, 27_800_000_000L }, 27_800_000_000L)]
    public void PARAMS_IS_THE_MOST_FREQUENT_TOTAL(long[] totals, long expected)
    {
        var repos = totals.Select((t, i) => (L($"o{i}/m-GGUF", t), T(Q("g-Q4_K_M.gguf", 16)))).ToArray();
        Assert.Equal(expected, Price("Qwen3.8-27B", "Qwen", ShelfMachines.Apu8060S, repos).Params);
    }

    [Fact]
    public void VISION_IS_THE_ROW_FILES_REPO()
    {
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
            (L("unsloth/gemma-4-31B-it-GGUF", 31 * GB, 1000), T(Q("g-Q6_K.gguf", 25))),
            (L("bartowski/gemma-4-31B-it-GGUF", 31 * GB, 10), TV([Q("g-Q4_K_M.gguf", 18)], Q("mmproj-F16.gguf", 1))));
        Assert.Equal("unsloth", row.RowFile!.Publisher);
        Assert.False(row.Vision);
    }

    [Theory]
    [InlineData("Qwen3.5-35B-A3B", 3_000_000_000L)]
    [InlineData("gemma-4-26B-A4B-it", 4_000_000_000L)]
    [InlineData("gemma-4-31B-it", null)]
    public void ACTIVE_FROM_THE_KEY(string key, long? active) =>
        Assert.Equal(active, Price(key, null, ShelfMachines.Apu8060S,
            (L("unsloth/m-GGUF", 30 * GB, 1), T(Q("g-Q4_K_M.gguf", 18)))).Active);

    [Fact]
    public void A_REPO_REACHED_TWICE_COUNTS_ONCE()
    {
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
            (L("google/gemma-4-31B-it-qat-q4_0-gguf", 31 * GB, 100), T(Q("g-Q4_0.gguf", 17))),
            (L("Google/Gemma-4-31B-it-qat-q4_0-gguf", 31 * GB, 100), T(Q("g-Q4_0.gguf", 17))));
        var google = Assert.Single(row.Publishers);
        Assert.Single(google.Repos);
        Assert.Equal(100, google.Downloads);
    }

    [Fact]
    public void A_GATED_REPO_IS_LEFT_OUT()
    {
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
            (L("bartowski/gemma-4-31B-it-GGUF", 31 * GB, 100, gated: true), T(Q("g-Q4_K_M.gguf", 18))),
            (L("unsloth/gemma-4-31B-it-GGUF", 31 * GB, 10), T(Q("g-Q4_K_M.gguf", 18))));
        Assert.Equal(["unsloth"], row.Publishers.Select(p => p.Org));
    }

    [Fact]
    public void NO_FILE_ANYWHERE()
    {
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
            (L("unsloth/gemma-4-31B-it-GGUF", 31 * GB, 10), T(Q("weights.gguf", 18))));
        Assert.Null(row.RowFile);
        Assert.Equal(-1, row.RowPublisher);
        Assert.Equal(FitRegime.DoesNotFit, row.Fit);
    }

    [Fact]
    public void NO_RELEASER()
    {
        var row = Price("some-model", null, ShelfMachines.Apu8060S,
            (L("bartowski/some-model-GGUF", 9 * GB, 5000), T(Q("g-Q6_K.gguf", 8))),
            (L("unsloth/some-model-GGUF", 9 * GB, 10), T(Q("g-Q4_K_M.gguf", 6))));
        Assert.Equal(["bartowski", "unsloth"], row.Publishers.Select(p => p.Org));
        Assert.Equal("unsloth", row.RowFile!.Publisher);
    }

    [Fact]
    public void QUANT_OF_A_REFERENCE()
    {
        var set = new HubQuant("g-Q4_K_M-00001-of-00002.gguf", 18 * GB, null, ShardCount: 2,
            Files: [new HubFile("g-Q4_K_M-00001-of-00002.gguf", 9 * GB, "a"), new HubFile("g-Q4_K_M-00002-of-00002.gguf", 9 * GB, "b")]);
        var row = Price("gemma-4-31B-it", "google", ShelfMachines.Apu8060S,
            (L("unsloth/gemma-4-31B-it-GGUF", 31 * GB, 10), T(set)));
        Assert.Equal(2, row.QuantOf(row.RowFile!)!.Members.Count);
    }
}
